using CodeGraph.Models.Responses;

namespace CodeGraph.Data;

public interface IIndexerRunStore
{
    Task<JobQueueResponse> GetJobQueueAsync(int limit, CancellationToken ct = default)
        => throw new NotSupportedException("This store does not support the admin job queue.");

    Task<bool> DismissJobAsync(string kind, long id, CancellationToken ct = default)
        => throw new NotSupportedException("This store does not support queue failure dismissal.");

    Task<int> DeleteFinishedIndexerRunsBeforeAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct = default)
        => throw new NotSupportedException("This store does not support indexer run retention.");

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

    /// <summary>
    /// As the bounded claim above, but only considers runs matching <paramref name="filter"/>, so
    /// separate worker lanes can claim different kinds of work. A null filter claims any run.
    /// </summary>
    Task<IndexerRunLease?> TryClaimNextIndexerRunAsync(
        string owner,
        DateTime now,
        DateTime leaseExpiresAt,
        int maxAttempts,
        IndexerRunOperationFilter? filter,
        CancellationToken ct = default)
        => filter is null
            ? TryClaimNextIndexerRunAsync(owner, now, leaseExpiresAt, maxAttempts, ct)
            : throw new NotSupportedException("This indexer run store does not support operation-filtered claims.");

    /// <summary>
    /// Creates <paramref name="run"/> unless an active (queued or running) run with the same
    /// operation and <see cref="IndexerRunEntity.SubmissionHash"/> already exists; that run is
    /// returned instead (<c>Created: false</c>). Concurrent submissions of the same work coalesce.
    /// </summary>
    Task<IndexerRunSubmissionResult> CreateOrCoalesceActiveIndexerRunAsync(
        IndexerRunEntity run,
        CancellationToken ct = default)
        => throw new NotSupportedException("This indexer run store does not support coalesced submissions.");

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

    /// <summary>
    /// Completes a run that succeeded with a recorded, non-fatal degradation (for example a
    /// Rust semantic fallback). A null <paramref name="errorCode"/> is a clean completion.
    /// </summary>
    Task<bool> CompleteIndexerRunAsync(
        IndexerRunLease lease,
        string message,
        string? errorCode,
        string? error,
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

/// <summary>Claims only runs of <paramref name="Operation"/> (<paramref name="Include"/>) or only other runs.</summary>
public sealed record IndexerRunOperationFilter(string Operation, bool Include);

public sealed record IndexerRunSubmissionResult(long RunId, bool Created);

public sealed record IndexerRunLeaseRenewal(bool Renewed, bool CancellationRequested);

public enum IndexerRunFailureDisposition
{
    LeaseLost,
    Retrying,
    Failed
}
