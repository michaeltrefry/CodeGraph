namespace CodeGraph.Data;

public interface IIndexerRunStore
{
    Task<long> CreateIndexerRunAsync(IndexerRunEntity run, CancellationToken ct = default);

    Task UpdateIndexerRunStatusAsync(
        long runId,
        string status,
        string? message = null,
        DateTime? completedAt = null,
        string? error = null,
        CancellationToken ct = default);

    Task<IndexerRunEntity?> GetIndexerRunAsync(long runId, CancellationToken ct = default);

    Task<IReadOnlyList<IndexerRunEntity>> ListIndexerRunsAsync(
        string? status = null,
        string? operation = null,
        int take = 50,
        CancellationToken ct = default);
}
