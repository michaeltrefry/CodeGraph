using CodeGraph.Models.Requests;
using CodeGraph.Indexer.Client;

namespace CodeGraph.Jobs.Jobs;

public class DiscoverRepositoriesJob(
    IIndexerClient indexerClient,
    ILogger<DiscoverRepositoriesJob> logger) : IJobCommand<DiscoverRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(DiscoverRequest request, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var response = await indexerClient.StartDiscoverAsync(IndexerClientJobUser.Username, request, ct);
        logger.LogInformation(
            "Queued repository discovery through indexer host as run {RunId}.",
            response.RunId);

        return new JobExecutionResult(
            Success: true,
            Message: response.Message ?? $"Queued repository discovery as run {response.RunId}.",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow);
    }
}
