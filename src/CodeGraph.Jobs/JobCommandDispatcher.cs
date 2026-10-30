using System.Text.Json;
using CodeGraph.Jobs.Jobs;
using CodeGraph.Models;
using CodeGraph.Models.Requests;

namespace CodeGraph.Jobs;

public class JobCommandDispatcher(
    DiscoverRepositoriesJob discoverRepositoriesJob,
    ReIndexAllRepositoriesJob reIndexAllRepositoriesJob,
    ProcessBatchAnalysisJob processBatchAnalysisJob,
    LinkAndDetectJob linkAndDetectJob,
    DetectCommunitiesJob detectCommunitiesJob,
    RegenerateMcpDocsJob regenerateMcpDocsJob,
    AssistantRetentionCleanupJob assistantRetentionCleanupJob,
    IngestConventionEmbeddingsJob ingestConventionEmbeddingsJob,
    PruneWorkSessionsJob pruneWorkSessionsJob,
    PruneWorkMessagesJob pruneWorkMessagesJob,
    WorkImportJob workImportJob,
    IndexerRunRetentionJob indexerRunRetentionJob,
    DreamProjectsJob dreamProjectsJob) : IJobCommandDispatcher
{
    public IReadOnlyList<string> GetSupportedJobTypes() => JobTypes.All;

    public string NormalizeArgsJson(string jobType, JsonElement? args)
    {
        return jobType switch
        {
            JobTypes.Discover => Serialize(DeserializeOrDefault<DiscoverRequest>(args)),
            JobTypes.ProcessBatchAnalysis => Serialize(DeserializeOrDefault<ProcessBatchAnalysisJobRequest>(args)),
            JobTypes.DreamProjects => Serialize(new EmptyJobRequest()),
            JobTypes.ReIndexAll => Serialize(new EmptyJobRequest()),
            JobTypes.LinkAndDetect => Serialize(new EmptyJobRequest()),
            JobTypes.DetectCommunities => Serialize(new EmptyJobRequest()),
            JobTypes.RegenerateMcpDocs => Serialize(new EmptyJobRequest()),
            JobTypes.AssistantRetentionCleanup => Serialize(new EmptyJobRequest()),
            JobTypes.IngestConventionEmbeddings => Serialize(new EmptyJobRequest()),
            JobTypes.PruneWorkSessions => Serialize(new EmptyJobRequest()),
            JobTypes.PruneWorkMessages => Serialize(new EmptyJobRequest()),
            JobTypes.WorkImport => Serialize(DeserializeOrDefault<WorkImportJobRequest>(args)),
            JobTypes.IndexerRunRetention => NormalizeRetentionArgs(args),
            _ => throw new InvalidOperationException($"Unsupported job type '{jobType}'.")
        };
    }

    public Task<JobExecutionResult> ExecuteAsync(
        string jobType,
        string argsJson,
        string? executionKey = null,
        CancellationToken ct = default)
    {
        return jobType switch
        {
            JobTypes.DreamProjects => dreamProjectsJob.ExecuteAsync(
                Deserialize<EmptyJobRequest>(argsJson), RequireExecutionKey(executionKey), ct),
            JobTypes.Discover => discoverRepositoriesJob.ExecuteAsync(
                Deserialize<DiscoverRequest>(argsJson), RequireExecutionKey(executionKey), ct),
            JobTypes.ProcessBatchAnalysis => processBatchAnalysisJob.ExecuteAsync(
                Deserialize<ProcessBatchAnalysisJobRequest>(argsJson), RequireExecutionKey(executionKey), ct),
            JobTypes.ReIndexAll => reIndexAllRepositoriesJob.ExecuteAsync(
                Deserialize<EmptyJobRequest>(argsJson), RequireExecutionKey(executionKey), ct),
            JobTypes.LinkAndDetect => linkAndDetectJob.ExecuteAsync(Deserialize<EmptyJobRequest>(argsJson), ct),
            JobTypes.DetectCommunities => detectCommunitiesJob.ExecuteAsync(Deserialize<EmptyJobRequest>(argsJson), ct),
            JobTypes.RegenerateMcpDocs => regenerateMcpDocsJob.ExecuteAsync(Deserialize<EmptyJobRequest>(argsJson), ct),
            JobTypes.AssistantRetentionCleanup => assistantRetentionCleanupJob.ExecuteAsync(Deserialize<EmptyJobRequest>(argsJson), ct),
            JobTypes.IngestConventionEmbeddings => ingestConventionEmbeddingsJob.ExecuteAsync(Deserialize<EmptyJobRequest>(argsJson), ct),
            JobTypes.PruneWorkSessions => pruneWorkSessionsJob.ExecuteAsync(Deserialize<EmptyJobRequest>(argsJson), ct),
            JobTypes.PruneWorkMessages => pruneWorkMessagesJob.ExecuteAsync(Deserialize<EmptyJobRequest>(argsJson), ct),
            JobTypes.WorkImport => workImportJob.ExecuteAsync(Deserialize<WorkImportJobRequest>(argsJson), ct),
            JobTypes.IndexerRunRetention => indexerRunRetentionJob.ExecuteAsync(Deserialize<IndexerRunRetentionJobRequest>(argsJson), ct),
            _ => throw new InvalidOperationException($"Unsupported job type '{jobType}'.")
        };
    }

    private static string RequireExecutionKey(string? executionKey)
    {
        if (string.IsNullOrWhiteSpace(executionKey))
            throw new InvalidOperationException("A durable execution key is required for publication-producing jobs.");
        return executionKey.Trim();
    }

    private static string NormalizeRetentionArgs(JsonElement? args)
    {
        var request = DeserializeOrDefault<IndexerRunRetentionJobRequest>(args);
        if (request.RetentionDays <= 0)
            throw new InvalidOperationException("Retention days must be greater than zero.");
        return Serialize(request);
    }

    private static T Deserialize<T>(string argsJson) where T : new()
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return new T();

        var parsed = JsonSerializer.Deserialize<T>(argsJson, CodeGraphJsonDefaults.CamelCase);
        return parsed ?? new T();
    }

    private static T DeserializeOrDefault<T>(JsonElement? args) where T : new()
    {
        if (args is null || args.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new T();

        var parsed = args.Value.Deserialize<T>(CodeGraphJsonDefaults.CamelCase);
        return parsed ?? new T();
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, CodeGraphJsonDefaults.CamelCase);
}
