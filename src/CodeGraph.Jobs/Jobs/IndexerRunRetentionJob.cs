using CodeGraph.Services.Indexer;

namespace CodeGraph.Jobs.Jobs;

public sealed class IndexerRunRetentionJobRequest
{
    public int RetentionDays { get; set; } = 30;
}

public sealed class IndexerRunRetentionJob(IIndexerRunRetentionService retentionService)
    : IJobCommand<IndexerRunRetentionJobRequest>
{
    public async Task<JobExecutionResult> ExecuteAsync(IndexerRunRetentionJobRequest request, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        var deleted = await retentionService.CleanupAsync(request.RetentionDays, ct);
        return new JobExecutionResult(true, $"Indexer run retention deleted {deleted} rows.", startedAt, DateTime.UtcNow);
    }
}
