namespace CodeGraph.Data;

/// <summary>What one prune pass removed.</summary>
public sealed record WorkSessionPruneResult(int SessionsDeleted, int ClaimRowsDeleted, int IdempotencyKeysDeleted);

/// <summary>Retention of work sessions (sc-27882): keeps <c>work_sessions</c> and <c>work_session_claims</c> bounded.</summary>
public interface IWorkSessionPruner
{
    /// <summary>
    /// Deletes every session whose end (or, if it never ended, its last heartbeat) is before
    /// <paramref name="cutoffUtc"/> together with its closed claim rows. A session is kept while it has an open claim row or an item claimed by it, a live hold or
    /// queued waiter, or a direct message a later session may still inherit. Any other row that names the
    /// session keeps its harness, model and host: they are copied onto it before the session is deleted.
    /// </summary>
    Task<WorkSessionPruneResult> PruneSessionsAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct = default);
}
