using CodeGraph.Indexer.Client;

namespace CodeGraph.Jobs.Jobs;

public class ReIndexAllRepositoriesJob(
    IIndexerClient indexerClient) : IJobCommand<EmptyJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, CancellationToken ct = default)
        => await ExecuteAsync(request, submissionKey: null, ct);

    public async Task<JobExecutionResult> ExecuteAsync(
        EmptyJobRequest request,
        string? submissionKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(submissionKey))
            throw new InvalidOperationException("A durable submission key is required for re-index jobs.");
        var startedAtUtc = DateTime.UtcNow;
        var response = await indexerClient.StartReIndexAllAsync(
            IndexerClientJobUser.Username,
            submissionKey,
            ct);

        return new JobExecutionResult(
            Success: true,
            Message: response.Message ?? $"Queued re-indexing as run {response.RunId}.",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow);
    }
}
