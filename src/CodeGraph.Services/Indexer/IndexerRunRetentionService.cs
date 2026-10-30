using CodeGraph.Data;

namespace CodeGraph.Services.Indexer;

public interface IIndexerRunRetentionService
{
    Task<long> CleanupAsync(int retentionDays = 30, CancellationToken ct = default);
}

public sealed class IndexerRunRetentionService(IIndexerRunStore store, TimeProvider? timeProvider = null)
    : IIndexerRunRetentionService
{
    private const int BatchSize = 5000;

    public async Task<long> CleanupAsync(int retentionDays = 30, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retentionDays);
        var cutoff = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime.AddDays(-retentionDays);
        long total = 0;
        int deleted;
        do
        {
            ct.ThrowIfCancellationRequested();
            deleted = await store.DeleteFinishedIndexerRunsBeforeAsync(cutoff, BatchSize, ct);
            total += deleted;
        } while (deleted == BatchSize);

        return total;
    }
}
