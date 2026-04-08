using CodeGraph.Services;

namespace CodeGraph.Jobs.Jobs;

public class RegenerateMcpDocsJob(
    IMcpDocService mcpDocService) : IJobCommand<EmptyJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        await mcpDocService.RegenerateAsync();

        return new JobExecutionResult(
            Success: true,
            Message: "MCP documentation regenerated.",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow);
    }
}
