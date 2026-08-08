using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Indexer;

public sealed class StandaloneIndexerOperationsService(
    IIndexerRunStore runStore,
    IDatabaseSourceStore databaseSourceStore,
    IIndexerRunBackgroundRunner backgroundRunner) : IIndexerOperationsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(string username, ProcessRequest request, CancellationToken ct = default)
        => StartProcessRepositoriesAsync(username, request, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartReIndexAllAsync(string username, CancellationToken ct = default)
        => StartReIndexAllAsync(username, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartDiscoverAsync(string username, DiscoverRequest? request, CancellationToken ct = default)
        => StartDiscoverAsync(username, request, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartSyncSchemaAsync(string username, long sourceId, CancellationToken ct = default)
        => StartSyncSchemaAsync(username, sourceId, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(string username, CancellationToken ct = default)
        => StartSyncAllSchemasAsync(username, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartLinkAsync(string username, CancellationToken ct = default)
        => StartLinkAsync(username, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(string username, CancellationToken ct = default)
        => StartDetectCommunitiesAsync(username, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(string username, CancellationToken ct = default)
        => StartLinkAndDetectAsync(username, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(string username, string? repo = null, CancellationToken ct = default)
        => StartProcessBatchAnalysisAsync(username, repo, submissionKey: null, ct);

    public Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(
        string username,
        ProcessRequest request,
        string? submissionKey,
        CancellationToken ct = default)
    {
        if (request.Repos is not { Count: > 0 })
            throw new ArgumentException("At least one repo entry is required.", nameof(request));

        if (request.Repos.Count > 500)
            throw new ArgumentException("Maximum 500 repos per request.", nameof(request));

        return QueueRunAsync(
            IndexerRunOperations.ProcessRepositories,
            username,
            request.Repos.Count == 1 ? request.Repos[0] : $"{request.Repos.Count} repositories",
            $"Queued processing for {request.Repos.Count} repositor{(request.Repos.Count == 1 ? "y" : "ies")}.",
            request,
            submissionKey,
            ct);
    }

    public Task<IndexerAcceptedResponse> StartReIndexAllAsync(
        string username,
        string? submissionKey,
        CancellationToken ct = default)
        => QueueRunAsync(
            IndexerRunOperations.ReIndexAll,
            username,
            "all",
            "Queued re-indexing for all known repositories.",
            args: null,
            submissionKey,
            ct);

    public Task<IndexerAcceptedResponse> StartDiscoverAsync(
        string username,
        DiscoverRequest? request,
        string? submissionKey,
        CancellationToken ct = default)
    {
        request ??= new DiscoverRequest();
        return QueueRunAsync(
            IndexerRunOperations.Discover,
            username,
            string.IsNullOrWhiteSpace(request.NamePattern) ? "all" : request.NamePattern.Trim(),
            "Queued repository discovery.",
            request,
            submissionKey,
            ct);
    }

    public async Task<IndexerAcceptedResponse> StartSyncSchemaAsync(
        string username,
        long sourceId,
        string? submissionKey,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceId);

        var source = await databaseSourceStore.GetAsync(sourceId);
        if (source is null)
            throw new KeyNotFoundException($"Database source {sourceId} was not found.");

        var accepted = await CreateQueuedRunAsync(
            IndexerRunOperations.SyncSchema,
            NormalizeUsername(username),
            sourceId.ToString(),
            $"Queued schema sync for {source.ServerName}/{(string.IsNullOrWhiteSpace(source.DatabaseName) ? "all databases" : source.DatabaseName)}.",
            argsJson: null,
            submissionKey,
            ct);

        if (!accepted.Duplicate && accepted.Status == "queued")
            await backgroundRunner.EnqueueAsync(accepted.RunId!.Value, ct);
        return accepted;
    }

    public async Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(
        string username,
        string? submissionKey,
        CancellationToken ct = default)
    {
        var accepted = await CreateQueuedRunAsync(
            IndexerRunOperations.SyncAllSchemas,
            NormalizeUsername(username),
            "all",
            "Queued schema sync for all enabled database sources.",
            argsJson: null,
            submissionKey,
            ct);
        if (!accepted.Duplicate && accepted.Status == "queued")
            await backgroundRunner.EnqueueAsync(accepted.RunId!.Value, ct);
        return accepted;
    }

    public Task<IndexerAcceptedResponse> StartLinkAsync(string username, string? submissionKey, CancellationToken ct = default)
        => QueueRunAsync(
            IndexerRunOperations.Link,
            username,
            "all",
            "Queued cross-repository linking.",
            args: null,
            submissionKey,
            ct);

    public Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(string username, string? submissionKey, CancellationToken ct = default)
        => QueueRunAsync(
            IndexerRunOperations.DetectCommunities,
            username,
            "all",
            "Queued community detection.",
            args: null,
            submissionKey,
            ct);

    public Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(string username, string? submissionKey, CancellationToken ct = default)
        => QueueRunAsync(
            IndexerRunOperations.LinkAndDetect,
            username,
            "all",
            "Queued cross-repository linking and community detection.",
            args: null,
            submissionKey,
            ct);

    public Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(
        string username,
        string? repo,
        string? submissionKey,
        CancellationToken ct = default)
    {
        repo = string.IsNullOrWhiteSpace(repo) ? null : repo.Trim();
        return QueueRunAsync(
            IndexerRunOperations.ProcessBatchAnalysis,
            username,
            repo ?? "all",
            repo is null
                ? "Queued processing for pending batch analysis results."
                : $"Queued processing for pending batch analysis results in {repo}.",
            new BatchAnalysisIndexerRunArgs(repo),
            submissionKey,
            ct);
    }

    public async Task<IndexerRunResponse?> GetRunAsync(long runId, CancellationToken ct = default)
    {
        var run = await runStore.GetIndexerRunAsync(runId, ct);
        return run is null ? null : MapRun(run);
    }

    public async Task<IndexerRunResponse?> CancelRunAsync(long runId, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runId);
        var run = await runStore.RequestIndexerRunCancellationAsync(runId, DateTime.UtcNow, ct);
        return run is null ? null : MapRun(run);
    }

    public async Task<IReadOnlyList<IndexerRunResponse>> ListRunsAsync(
        string? status = null,
        string? operation = null,
        int take = 50,
        CancellationToken ct = default)
    {
        var runs = await runStore.ListIndexerRunsAsync(
            NormalizeOptionalFilter(status),
            NormalizeOptionalFilter(operation),
            Math.Clamp(take, 1, 200),
            ct);

        return runs.Select(MapRun).ToList();
    }

    private async Task<IndexerAcceptedResponse> CreateQueuedRunAsync(
        string operation,
        string username,
        string? target,
        string message,
        string? argsJson,
        string? submissionKey,
        CancellationToken ct)
    {
        username = NormalizeUsername(username);
        submissionKey = NormalizeSubmissionKey(operation, submissionKey);
        var submissionHash = ComputeSubmissionHash(operation, target, argsJson);
        var submitted = await runStore.CreateOrGetIndexerRunAsync(new IndexerRunEntity
        {
            Operation = operation,
            RequestedByUsername = username,
            Target = target,
            ArgsJson = argsJson,
            Status = "queued",
            Message = message,
            RetrySafe = IndexerRunOperations.IsRetrySafe(operation),
            SubmissionKey = submissionKey,
            SubmissionHash = submissionHash,
            CreatedAt = DateTime.UtcNow
        }, ct);

        var persisted = await runStore.GetIndexerRunAsync(submitted.RunId, ct)
            ?? throw new InvalidOperationException($"Indexer run {submitted.RunId} disappeared after submission.");
        if (!string.Equals(persisted.SubmissionHash, submissionHash, StringComparison.Ordinal))
        {
            throw new IndexerSubmissionConflictException(
                $"Submission key '{submissionKey}' is already associated with different indexer work.");
        }

        return new IndexerAcceptedResponse(
            Status: persisted.Status,
            Message: submitted.Created ? message : "An existing indexer run matched this submission key.",
            RunId: submitted.RunId,
            RunStatusUrl: $"/api/indexer/runs/{submitted.RunId}",
            SubmissionKey: submissionKey,
            Duplicate: !submitted.Created);
    }

    private static IndexerRunResponse MapRun(IndexerRunEntity run) => new(
        run.Id,
        run.Operation,
        run.Status,
        run.RequestedByUsername,
        run.Target,
        run.Message,
        run.Error,
        run.CreatedAt,
        run.StartedAt,
        run.CompletedAt,
        run.AttemptCount,
        run.HeartbeatAt,
        run.LeaseExpiresAt,
        run.CancelRequestedAt,
        run.NextAttemptAt,
        run.RetrySafe,
        run.SubmissionKey);

    private static string NormalizeUsername(string? username)
        => string.IsNullOrWhiteSpace(username) ? "unknown" : username.Trim().ToLowerInvariant();

    private static string? NormalizeOptionalFilter(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private async Task<IndexerAcceptedResponse> QueueRunAsync(
        string operation,
        string username,
        string? target,
        string message,
        object? args,
        string? submissionKey,
        CancellationToken ct)
    {
        var accepted = await CreateQueuedRunAsync(
            operation,
            NormalizeUsername(username),
            target,
            message,
            args is null ? null : JsonSerializer.Serialize(args, JsonOptions),
            submissionKey,
            ct);

        if (!accepted.Duplicate && accepted.Status == "queued")
            await backgroundRunner.EnqueueAsync(accepted.RunId!.Value, ct);
        return accepted;
    }

    private static string? NormalizeSubmissionKey(string operation, string? submissionKey)
    {
        submissionKey = string.IsNullOrWhiteSpace(submissionKey) ? null : submissionKey.Trim();
        if (submissionKey is null && !IndexerRunOperations.IsRetrySafe(operation))
            throw new ArgumentException("An Idempotency-Key is required for publication-producing indexer operations.", nameof(submissionKey));
        if (submissionKey?.Length > 191)
            throw new ArgumentException("Idempotency-Key must be 191 characters or fewer.", nameof(submissionKey));
        return submissionKey;
    }

    private static string ComputeSubmissionHash(string operation, string? target, string? argsJson)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{operation}\n{target ?? string.Empty}\n{argsJson ?? string.Empty}"))).ToLowerInvariant();
}

public sealed class IndexerSubmissionConflictException(string message) : InvalidOperationException(message);

public static class IndexerRunOperations
{
    public const string ProcessRepositories = "process_repositories";
    public const string ReIndexAll = "reindex_all";
    public const string Discover = "discover";
    public const string SyncSchema = "sync_schema";
    public const string SyncAllSchemas = "sync_all_schemas";
    public const string Link = "link";
    public const string DetectCommunities = "detect_communities";
    public const string LinkAndDetect = "link_and_detect";
    public const string ProcessBatchAnalysis = "process_batch_analysis";

    public static bool IsRetrySafe(string operation)
        => operation is SyncSchema
            or SyncAllSchemas
            or Link
            or DetectCommunities
            or LinkAndDetect;
}

public sealed class BatchAnalysisIndexerRunArgs
{
    public BatchAnalysisIndexerRunArgs()
    {
    }

    public BatchAnalysisIndexerRunArgs(string? repo)
    {
        Repo = repo;
    }

    public string? Repo { get; set; }
}
