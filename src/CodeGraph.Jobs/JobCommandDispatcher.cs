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
    RegenerateMcpDocsJob regenerateMcpDocsJob) : IJobCommandDispatcher
{
    public IReadOnlyList<string> GetSupportedJobTypes() => JobTypes.All;

    public string NormalizeArgsJson(string jobType, JsonElement? args)
    {
        return jobType switch
        {
            JobTypes.Discover => Serialize(DeserializeOrDefault<DiscoverRequest>(args)),
            JobTypes.ProcessBatchAnalysis => Serialize(DeserializeOrDefault<ProcessBatchAnalysisJobRequest>(args)),
            JobTypes.ReIndexAll => Serialize(new EmptyJobRequest()),
            JobTypes.LinkAndDetect => Serialize(new EmptyJobRequest()),
            JobTypes.DetectCommunities => Serialize(new EmptyJobRequest()),
            JobTypes.RegenerateMcpDocs => Serialize(new EmptyJobRequest()),
            _ => throw new InvalidOperationException($"Unsupported job type '{jobType}'.")
        };
    }

    public Task<JobExecutionResult> ExecuteAsync(string jobType, string argsJson, CancellationToken ct = default)
    {
        return jobType switch
        {
            JobTypes.Discover => discoverRepositoriesJob.ExecuteAsync(Deserialize<DiscoverRequest>(argsJson), ct),
            JobTypes.ProcessBatchAnalysis => processBatchAnalysisJob.ExecuteAsync(Deserialize<ProcessBatchAnalysisJobRequest>(argsJson), ct),
            JobTypes.ReIndexAll => reIndexAllRepositoriesJob.ExecuteAsync(Deserialize<EmptyJobRequest>(argsJson), ct),
            JobTypes.LinkAndDetect => linkAndDetectJob.ExecuteAsync(Deserialize<EmptyJobRequest>(argsJson), ct),
            JobTypes.DetectCommunities => detectCommunitiesJob.ExecuteAsync(Deserialize<EmptyJobRequest>(argsJson), ct),
            JobTypes.RegenerateMcpDocs => regenerateMcpDocsJob.ExecuteAsync(Deserialize<EmptyJobRequest>(argsJson), ct),
            _ => throw new InvalidOperationException($"Unsupported job type '{jobType}'.")
        };
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
