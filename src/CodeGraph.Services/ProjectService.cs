using System.Diagnostics;
using Microsoft.Extensions.Logging;
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
    IndexingOptions indexingOptions,
    ILogger<ProjectService> logger) : IProjectService
{
    // Shared across all transient instances to limit concurrent repo processing.
    // Initialized lazily from config on first use.
    private static SemaphoreSlim? _repoSemaphore;
    private static int _configuredMax;

    private SemaphoreSlim RepoSemaphore
    {
        get
        {
            var max = indexingOptions.MaxParallelRepos;
            if (_repoSemaphore is null || _configuredMax != max)
            {
                _repoSemaphore = new SemaphoreSlim(max, max);
                _configuredMax = max;
            }
            return _repoSemaphore;
        }
    }

    public async Task<AnalysisBatchResponse?> ReAnalyzeRepository(string repo, CancellationToken cancellationToken = new CancellationToken())
    {
        var batch = await graphStore.GetLatestBatchAsync(repo);
        if (batch is not null && batch.Status == "in-progress")
            return ProjectQueryService.MapBatch(batch);

        // Re-analyze uses ShouldAnalyze=false to skip async analysis submission via consumer,
        // then submits analysis directly so we can return the batch response synchronously.
        var message = new ProcessRepository
        {
            Name             = repo,
            ShouldIndex      = true,
            ShouldAnalyze    = false,
            SkipIfUpToDate   = false,
            IncludeAllSource = true
        };

        await ProcessRepository(message, cancellationToken);

        // Submit analysis directly (synchronous path for API response)
        var repoPath = await repoProvider.EnsureLocalAsync(repo, null, null, cancellationToken);
        await batchService.SubmitAnalysisBatchAsync(repo, repoPath, includeAllSource: true, cancellationToken);

        var updated = await graphStore.GetLatestBatchAsync(repo);
        return updated is not null ? ProjectQueryService.MapBatch(updated) : null;
    }

    public async Task ProcessRepository(ProcessRepository message, CancellationToken cancellationToken = new())
    {
        var semaphore = RepoSemaphore;
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            await ProcessRepositoryCore(message, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
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

        if (string.IsNullOrWhiteSpace(message.SourceGroup) && !string.IsNullOrWhiteSpace(repoUrl))
        {
            // Extract group from URL path (e.g. "https://host/group/subgroup/project.git" → "group/subgroup")
            if (Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
            {
                var path = uri.AbsolutePath.Trim('/');
                var pathWithNamespace = path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                    ? path[..^4] : path;
                var lastSlash = pathWithNamespace.LastIndexOf('/');
                message.SourceGroup = lastSlash > 0 ? pathWithNamespace[..lastSlash] : null;
            }
        }

        var repoPath = await repoProvider.EnsureLocalAsync(message.Name, message.Path, repoUrl, cancellationToken);

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
            await pipeline.IndexProjectAsync(message.Name, repoPath, repoUrl: repoUrl, sourceGroup: message.SourceGroup, ct: cancellationToken);

            await graphStore.UpsertSyncStateAsync(new SyncStateEntity
            {
                Project = message.Name,
                LastCommitSha = commitSha,
                LastSyncAt = DateTime.UtcNow,
                Status = "idle"
            });

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
