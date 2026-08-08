namespace CodeGraph.Data;

public interface IIndexerRunStore
{
    Task<long> CreateIndexerRunAsync(IndexerRunEntity run, CancellationToken ct = default);

    async Task<IndexerRunSubmissionResult> CreateOrGetIndexerRunAsync(
        IndexerRunEntity run,
        CancellationToken ct = default)
        => new(await CreateIndexerRunAsync(run, ct), Created: true);

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

    Task<IndexerRunLease?> TryClaimNextIndexerRunAsync(
        string owner,
        DateTime now,
        DateTime leaseExpiresAt,
        CancellationToken ct = default);

    Task<IndexerRunLease?> TryClaimNextIndexerRunAsync(
        string owner,
        DateTime now,
        DateTime leaseExpiresAt,
        int maxAttempts,
        CancellationToken ct = default)
        => throw new NotSupportedException("This indexer run store does not support bounded stale-lease recovery.");

    Task<IndexerRunLeaseRenewal> RenewIndexerRunLeaseAsync(
        long runId,
        string owner,
        long fencingToken,
        DateTime now,
        DateTime leaseExpiresAt,
        CancellationToken ct = default);

    Task<bool> CompleteIndexerRunAsync(
        IndexerRunLease lease,
        string message,
        DateTime completedAt,
        CancellationToken ct = default);

    Task<IndexerRunFailureDisposition> FailOrRetryIndexerRunAsync(
        IndexerRunLease lease,
        string errorCode,
        string error,
        DateTime now,
        DateTime nextAttemptAt,
        int maxAttempts,
        CancellationToken ct = default);

    Task<bool> CancelOwnedIndexerRunAsync(
        IndexerRunLease lease,
        string message,
        DateTime completedAt,
        CancellationToken ct = default);

    Task<IndexerRunEntity?> RequestIndexerRunCancellationAsync(
        long runId,
        DateTime requestedAt,
        CancellationToken ct = default);
}

public sealed record IndexerRunLease(IndexerRunEntity Run, string Owner, long FencingToken);

public sealed record IndexerRunSubmissionResult(long RunId, bool Created);

public sealed record IndexerRunLeaseRenewal(bool Renewed, bool CancellationRequested);

public enum IndexerRunFailureDisposition
{
    LeaseLost,
    Retrying,
    Failed
}
