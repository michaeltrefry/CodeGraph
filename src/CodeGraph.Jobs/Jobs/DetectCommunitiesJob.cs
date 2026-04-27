using CodeGraph.Indexer.Client;

namespace CodeGraph.Jobs.Jobs;

public class DetectCommunitiesJob(
    IIndexerClient indexerClient) : IJobCommand<EmptyJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var response = await indexerClient.StartDetectCommunitiesAsync(IndexerClientJobUser.Username, ct);

        return new JobExecutionResult(
            Success: true,
            Message: response.Message ?? $"Queued community detection as run {response.RunId}.",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow);
    }
}
