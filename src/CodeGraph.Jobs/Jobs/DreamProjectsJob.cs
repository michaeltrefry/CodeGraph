using CodeGraph.Indexer.Client;

namespace CodeGraph.Jobs.Jobs;

/// <summary>Hands off the project sweep to the model-running host, using durable submission identity.</summary>
public sealed class DreamProjectsJob(IIndexerClient indexerClient) : IJobCommand<EmptyJobRequest>
{
    public Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, CancellationToken ct = default)
        => ExecuteAsync(request, null, ct);

    public async Task<JobExecutionResult> ExecuteAsync(EmptyJobRequest request, string? submissionKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(submissionKey))
            throw new InvalidOperationException("A durable submission key is required for dreaming jobs.");
        var startedAtUtc = DateTime.UtcNow;
        var response = await indexerClient.StartDreamProjectsAsync(IndexerClientJobUser.Username, submissionKey, ct);
        return new JobExecutionResult(true,
            response.Message ?? $"Queued project dreaming as run {response.RunId}.",
            startedAtUtc, DateTime.UtcNow);
    }
}
