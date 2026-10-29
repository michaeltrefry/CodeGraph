using CodeGraph.Services.Work;

namespace CodeGraph.Jobs.Jobs;

public class PruneWorkMessagesJob(IWorkMessageRetentionService retentionService) : IJobCommand<EmptyJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var result = await retentionService.CleanupAsync(ct);

        return new JobExecutionResult(
            Success: true,
            Message: $"Work message retention deleted {result.TotalRowsDeleted} rows.",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow);
    }
}
