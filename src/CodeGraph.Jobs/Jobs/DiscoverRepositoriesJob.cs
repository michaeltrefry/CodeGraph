using CodeGraph.Models.Requests;
using CodeGraph.Indexer.Client;

namespace CodeGraph.Jobs.Jobs;

public class DiscoverRepositoriesJob(
    IIndexerClient indexerClient,
    ILogger<DiscoverRepositoriesJob> logger) : IJobCommand<DiscoverRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(DiscoverRequest request, CancellationToken ct = default)
        => await ExecuteAsync(request, submissionKey: null, ct);

    public async Task<JobExecutionResult> ExecuteAsync(
        DiscoverRequest request,
        string? submissionKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(submissionKey))
            throw new InvalidOperationException("A durable submission key is required for repository discovery jobs.");
        var startedAtUtc = DateTime.UtcNow;
        var response = await indexerClient.StartDiscoverAsync(
            IndexerClientJobUser.Username,
            request,
            submissionKey,
            ct);
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
