using CodeGraph.Indexer.Client;

namespace CodeGraph.Jobs.Jobs;

public class ProcessBatchAnalysisJob(
    IIndexerClient indexerClient) : IJobCommand<ProcessBatchAnalysisJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(ProcessBatchAnalysisJobRequest request, CancellationToken ct = default)
        => await ExecuteAsync(request, submissionKey: null, ct);

    public async Task<JobExecutionResult> ExecuteAsync(
        ProcessBatchAnalysisJobRequest request,
        string? submissionKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(submissionKey))
            throw new InvalidOperationException("A durable submission key is required for batch-analysis jobs.");
        var startedAtUtc = DateTime.UtcNow;
        var response = await indexerClient.StartProcessBatchAnalysisAsync(
            IndexerClientJobUser.Username,
            request.Repo,
            submissionKey,
            ct);

        return new JobExecutionResult(
            Success: true,
            Message: response.Message ?? $"Queued batch-analysis processing as run {response.RunId}.",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow);
    }
}
