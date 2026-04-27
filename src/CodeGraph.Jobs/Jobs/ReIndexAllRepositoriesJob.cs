using CodeGraph.Indexer.Client;

namespace CodeGraph.Jobs.Jobs;

public class ReIndexAllRepositoriesJob(
    IIndexerClient indexerClient) : IJobCommand<EmptyJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var response = await indexerClient.StartReIndexAllAsync(IndexerClientJobUser.Username, ct);

        return new JobExecutionResult(
            Success: true,
            Message: response.Message ?? $"Queued re-indexing as run {response.RunId}.",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow);
    }
}
