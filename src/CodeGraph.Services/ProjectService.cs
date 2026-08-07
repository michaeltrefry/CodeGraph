using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Models.Messages;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Messaging;
using CodeGraph.Services.Pipeline;
using CodeGraph.Services.Query;

namespace CodeGraph.Services;

public class ProjectService(
    IGraphStore graphStore,
    IBatchAnalysisService batchService,
    IMessageBus messageBus,
    IRepoProvider repoProvider,
    IndexingPipeline pipeline,
    IOptions<IndexingOptions> indexingOptionsAccessor,
    ILogger<ProjectService> logger) : IProjectService
{
    private readonly IndexingOptions indexingOptions = indexingOptionsAccessor.Value;
    // Shared across all transient instances to limit concurrent repo processing.
    // Initialized lazily from config on first use.
    private static SemaphoreSlim? _repoSemaphore;
    private static int _configuredMax;
    private static readonly object RepoSemaphoreSync = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private SemaphoreSlim RepoSemaphore
    {
        get
        {
            var max = indexingOptions.MaxParallelRepos;
            lock (RepoSemaphoreSync)
            {
                if (_repoSemaphore is null || _configuredMax != max)
                {
                    _repoSemaphore = new SemaphoreSlim(max, max);
                    _configuredMax = max;
                }
                return _repoSemaphore;
            }
        }
    }

    public async Task<AnalysisBatchResponse?> ReAnalyzeRepository(string repo, CancellationToken cancellationToken = new CancellationToken())
    {
        return await WithRepositoryLockAsync(repo, async () =>
        {
            var batch = await graphStore.GetLatestBatchAsync(repo);
            if (batch is not null && IsActiveAnalysisBatch(batch.Status))
                return ProjectQueryService.MapBatch(batch);

            // Re-analyze builds a complete replacement snapshot, then submits analysis
            // directly so the API can return the new batch synchronously.
            var message = new ProcessRepository
            {
                Name = repo,
                ShouldIndex = true,
                ShouldAnalyze = false,
                SkipIfUpToDate = false,
                IncludeAllSource = true,
                ReplaceExistingGraph = true
            };

            await ProcessRepositoryCore(message, cancellationToken);

            var repoPath = (await repoProvider.ResolveRepositoryAsync(repo, null, null, cancellationToken)).LocalPath;
            await batchService.SubmitAnalysisBatchAsync(repo, repoPath, includeAllSource: true, cancellationToken);

            var updated = await graphStore.GetLatestBatchAsync(repo);
            return updated is not null ? ProjectQueryService.MapBatch(updated) : null;
        }, cancellationToken);
    }

    public async Task ProcessRepository(ProcessRepository message, CancellationToken cancellationToken = new())
    {
        await WithRepositoryLockAsync(message.Name, async () =>
        {
            await ProcessRepositoryCore(message, cancellationToken);
            return true;
        }, cancellationToken);
    }

    private async Task ProcessRepositoryCore(ProcessRepository message, CancellationToken cancellationToken)
    {
        // 0. Resolve repo URL — fall back to stored repo_url if not provided
        var repoUrl = message.RepoUrl;
        if (string.IsNullOrWhiteSpace(message.Path) && string.IsNullOrWhiteSpace(repoUrl))
        {
            var repo = await graphStore.GetRepositoryByName(message.Name);
            if (repo?.RepoUrl is not null)
                repoUrl = repo.RepoUrl;
        }

        var resolvedRepository = await repoProvider.ResolveRepositoryAsync(
            message.Name, message.Path, repoUrl, cancellationToken);
        if (!string.IsNullOrWhiteSpace(message.SourceGroup)
            && !string.Equals(message.SourceGroup.Trim().Trim('/'), resolvedRepository.SourceGroup,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Repository source group '{message.SourceGroup}' is inconsistent with provider-resolved identity '{resolvedRepository.CanonicalIdentity}'.");
        }

        var repoPath = resolvedRepository.LocalPath;
        repoUrl = resolvedRepository.RepoUrl;
        var sourceGroup = resolvedRepository.SourceGroup;

        // 1. Skip if up to date
        if (message.SkipIfUpToDate)
        {
            var syncState = await graphStore.GetSyncStateAsync(message.Name);
            var currentSha = GetHeadCommitSha(repoPath);
            if (syncState?.LastCommitSha == currentSha && currentSha is not null)
            {
                logger.LogInformation("Skipping {Repo} — already at HEAD {Sha}", message.Name, currentSha);
                return;
            }
        }

        // 2. Index
        var commitSha = GetHeadCommitSha(repoPath);
        if (message.ShouldIndex)
        {
            logger.LogInformation("Indexing {Repo}", message.Name);
            await pipeline.IndexProjectAsync(message.Name, repoPath,
                repoUrl: repoUrl,
                sourceGroup: sourceGroup,
                repositoryToolingIdentity: resolvedRepository.CanonicalIdentity,
                replaceExistingGraph: message.ReplaceExistingGraph,
                replacementSyncState: message.ReplaceExistingGraph
                    ? CreateIdleSyncState(message.Name, commitSha)
                    : null,
                ct: cancellationToken);

            if (!message.ReplaceExistingGraph)
                await graphStore.UpsertSyncStateAsync(CreateIdleSyncState(message.Name, commitSha));

            // 3. Publish event — downstream consumers handle linking, vitals, and analysis
            await messageBus.PublishAsync(new RepositoryIndexingCompleted
            {
                Name = message.Name,
                RepoPath = repoPath,
                RepoUrl = repoUrl,
                CommitSha = commitSha,
                ShouldAnalyze = message.ShouldAnalyze,
                IncludeAllSource = message.IncludeAllSource,
                ShouldComputeVitals = message.ShouldComputeVitals
            });

            logger.LogInformation("Published RepositoryIndexingCompleted for {Repo}", message.Name);
        }
        else if (message.ShouldAnalyze)
        {
            // Analysis-only (no indexing) — verify graph data exists, then submit directly
            var allNodes = await graphStore.GetAllNodesByProjectAsync(message.Name);
            var hasAnalyzableNodes = allNodes.Any(n =>
                n.Label is "Class" or "Interface");
            if (!hasAnalyzableNodes)
                throw new InvalidOperationException(
                    $"Cannot analyze {message.Name}: no graph data exists and ShouldIndex=false.");

            logger.LogInformation("Submitting analysis batch for {Repo} (no indexing)", message.Name);
            await batchService.SubmitAnalysisBatchAsync(message.Name, repoPath, message.IncludeAllSource, cancellationToken);
        }
    }

    private static bool IsActiveAnalysisBatch(string status) =>
        status.Equals("submitted", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("in-progress", StringComparison.OrdinalIgnoreCase);

    private async Task<T> WithRepositoryLockAsync<T>(
        string repository,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        var repositoryLock = RepositoryLocks.GetOrAdd(repository, _ => new SemaphoreSlim(1, 1));
        await repositoryLock.WaitAsync(cancellationToken);
        try
        {
            var capacity = RepoSemaphore;
            await capacity.WaitAsync(cancellationToken);
            try
            {
                await using var distributedLock =
                    await graphStore.AcquireProjectIndexingLockAsync(repository, cancellationToken);
                return await action();
            }
            finally
            {
                capacity.Release();
            }
        }
        finally
        {
            repositoryLock.Release();
        }
    }

    private static SyncStateEntity CreateIdleSyncState(string project, string? commitSha) => new()
    {
        Project = project,
        LastCommitSha = commitSha,
        LastSyncAt = DateTime.UtcNow,
        Status = "idle"
    };

    public async Task<bool> DeleteRepositoryAsync(string repo)
    {
        var existing = await graphStore.GetRepositoryByName(repo);
        if (existing is null) return false;

        logger.LogInformation("Deleting repository {Repo} from graph database", repo);

        await graphStore.DeleteAnalysisDataForProjectAsync(repo);
        await graphStore.DeleteCrossRepoEdgesForProjectAsync(repo);
        await graphStore.DeleteAllEdgesForProjectAsync(repo);
        await graphStore.DeleteNodesByProjectAsync(repo);
        await graphStore.DeleteSyncStateAsync(repo);
        await graphStore.DeleteRepositoryAsync(repo);

        logger.LogInformation("Repository {Repo} deleted successfully", repo);
        return true;
    }

    private string? GetHeadCommitSha(string repoPath)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var sha = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            return proc.ExitCode == 0 && sha.Length >= 40 ? sha : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to get HEAD commit SHA for {RepoPath}", repoPath);
            return null;
        }
    }
}
