using CodeGraph.Indexer.Client;

namespace CodeGraph.Jobs.Jobs;

public class ProcessBatchAnalysisJob(
    IIndexerClient indexerClient) : IJobCommand<ProcessBatchAnalysisJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(ProcessBatchAnalysisJobRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var response = await indexerClient.StartProcessBatchAnalysisAsync(IndexerClientJobUser.Username, request.Repo, ct);

        return new JobExecutionResult(
            Success: true,
            Message: response.Message ?? $"Queued batch-analysis processing as run {response.RunId}.",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow);
    }
}
