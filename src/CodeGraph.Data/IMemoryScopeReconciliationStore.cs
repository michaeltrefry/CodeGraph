using CodeGraph.Models.Memory;

namespace CodeGraph.Data;

/// <summary>
/// The durable state of the memory scope reconciliation (CG-172), in the work database: the
/// repositories of each project whose full backfill has not yet succeeded, and the memory watermark
/// the last successful reconciliation reached. A project write records its added repositories as
/// pending in its own transaction, so a backfill that fails, is dropped by a full queue or is lost to
/// a restart is still run by the next reconciliation.
/// </summary>
public interface IMemoryScopeReconciliationStore
{
    /// <summary>Every project with repositories still to backfill, oldest first.</summary>
    Task<IReadOnlyList<PendingMemoryScopeBackfill>> ListPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// The backfill of these repositories succeeded: they leave the project's pending set, repositories
    /// added since stay, and the row goes when none is left.
    /// </summary>
    Task CompletePendingAsync(string projectKey, IReadOnlyCollection<string> repositories, CancellationToken ct = default);

    /// <summary>The project's backfill failed: counts the attempt and keeps the error; its repositories stay pending.</summary>
    Task FailPendingAsync(string projectKey, string error, CancellationToken ct = default);

    /// <summary>The watermark of the last successful reconciliation, or null before the first one.</summary>
    Task<MemoryScopeWatermark?> GetWatermarkAsync(CancellationToken ct = default);

    Task SetWatermarkAsync(MemoryScopeWatermark watermark, CancellationToken ct = default);
}

public sealed record PendingMemoryScopeBackfill(
    string ProjectKey,
    IReadOnlyList<string> Repositories,
    DateTime EnqueuedAt,
    int Attempts,
    string? LastError);
