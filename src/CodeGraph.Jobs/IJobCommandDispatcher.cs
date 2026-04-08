using System.Text.Json;

namespace CodeGraph.Jobs;

public interface IJobCommandDispatcher
{
    IReadOnlyList<string> GetSupportedJobTypes();
    string NormalizeArgsJson(string jobType, JsonElement? args);
    Task<JobExecutionResult> ExecuteAsync(string jobType, string argsJson, CancellationToken ct = default);
}
