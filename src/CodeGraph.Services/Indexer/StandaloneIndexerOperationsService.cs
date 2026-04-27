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

    public Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(
        string username,
        ProcessRequest request,
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
            ct);
    }

    public Task<IndexerAcceptedResponse> StartReIndexAllAsync(string username, CancellationToken ct = default)
        => QueueRunAsync(
            IndexerRunOperations.ReIndexAll,
            username,
            "all",
            "Queued re-indexing for all known repositories.",
            args: null,
            ct);

    public Task<IndexerAcceptedResponse> StartDiscoverAsync(
        string username,
        DiscoverRequest? request,
        CancellationToken ct = default)
    {
        request ??= new DiscoverRequest();
        return QueueRunAsync(
            IndexerRunOperations.Discover,
            username,
            string.IsNullOrWhiteSpace(request.NamePattern) ? "all" : request.NamePattern.Trim(),
            "Queued repository discovery.",
            request,
            ct);
    }

    public async Task<IndexerAcceptedResponse> StartSyncSchemaAsync(
        string username,
        long sourceId,
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
            ct);

        await backgroundRunner.EnqueueAsync(accepted.RunId!.Value, ct);
        return accepted;
    }

    public async Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(string username, CancellationToken ct = default)
    {
        var accepted = await CreateQueuedRunAsync(
            IndexerRunOperations.SyncAllSchemas,
            NormalizeUsername(username),
            "all",
            "Queued schema sync for all enabled database sources.",
            argsJson: null,
            ct);
        await backgroundRunner.EnqueueAsync(accepted.RunId!.Value, ct);
        return accepted;
    }

    public Task<IndexerAcceptedResponse> StartLinkAsync(string username, CancellationToken ct = default)
        => QueueRunAsync(
            IndexerRunOperations.Link,
            username,
            "all",
            "Queued cross-repository linking.",
            args: null,
            ct);

    public Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(string username, CancellationToken ct = default)
        => QueueRunAsync(
            IndexerRunOperations.DetectCommunities,
            username,
            "all",
            "Queued community detection.",
            args: null,
            ct);

    public Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(string username, CancellationToken ct = default)
        => QueueRunAsync(
            IndexerRunOperations.LinkAndDetect,
            username,
            "all",
            "Queued cross-repository linking and community detection.",
            args: null,
            ct);

    public Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(
        string username,
        string? repo = null,
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
            ct);
    }

    public async Task<IndexerRunResponse?> GetRunAsync(long runId, CancellationToken ct = default)
    {
        var run = await runStore.GetIndexerRunAsync(runId, ct);
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
        CancellationToken ct)
    {
        var runId = await runStore.CreateIndexerRunAsync(new IndexerRunEntity
        {
            Operation = operation,
            RequestedByUsername = username,
            Target = target,
            ArgsJson = argsJson,
            Status = "queued",
            Message = message,
            CreatedAt = DateTime.UtcNow
        }, ct);

        return new IndexerAcceptedResponse(
            Status: "queued",
            Message: message,
            RunId: runId,
            RunStatusUrl: $"/api/indexer/runs/{runId}");
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
        run.CompletedAt);

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
        CancellationToken ct)
    {
        var accepted = await CreateQueuedRunAsync(
            operation,
            NormalizeUsername(username),
            target,
            message,
            args is null ? null : JsonSerializer.Serialize(args, JsonOptions),
            ct);

        await backgroundRunner.EnqueueAsync(accepted.RunId!.Value, ct);
        return accepted;
    }
}

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
