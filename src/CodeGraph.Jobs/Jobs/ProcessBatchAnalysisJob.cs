using CodeGraph.Services.Analyzers;

namespace CodeGraph.Jobs.Jobs;

public class ProcessBatchAnalysisJob(
    IBatchAnalysisService batchService) : IJobCommand<ProcessBatchAnalysisJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(ProcessBatchAnalysisJobRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        await batchService.ProcessCompletedBatchesAsync(request.Repo, ct);

        return new JobExecutionResult(
            Success: true,
            Message: string.IsNullOrWhiteSpace(request.Repo)
                ? "Processed pending batch analysis for all repositories."
                : $"Processed pending batch analysis for {request.Repo}.",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow);
    }
}
