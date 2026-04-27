using CodeGraph.Services.Assistant;

namespace CodeGraph.Jobs.Jobs;

public class AssistantRetentionCleanupJob(
    IAssistantRetentionCleanupService cleanupService) : IJobCommand<EmptyJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var result = await cleanupService.CleanupAsync(ct);

        return new JobExecutionResult(
            Success: true,
            Message: $"Assistant retention cleanup affected {result.TotalRowsAffected} rows.",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow);
    }
}
