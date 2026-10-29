using CodeGraph.Models;

namespace CodeGraph.Data;

/// <summary>
/// Persistence for branch overlay indexes: per-branch graph slices covering only the files
/// that differ from the commit of the default-branch index. Overlays never write the
/// default-branch nodes/edges tables.
/// </summary>
public interface IBranchOverlayStore
{
    Task<long> CreateOverlayAsync(BranchOverlayEntity overlay, CancellationToken ct = default);

    Task<BranchOverlayEntity?> GetOverlayAsync(long overlayId, CancellationToken ct = default);

    /// <summary>
    /// Overlays for the commit <paramref name="sha"/> (a full lowercase SHA), newest first: overlays
    /// whose resolved head is <paramref name="sha"/>, plus overlays requested by that SHA whose head
    /// is not resolved yet (queued). Restricted to <paramref name="repository"/> when given.
    /// </summary>
    Task<IReadOnlyList<BranchOverlayEntity>> FindOverlaysByShaAsync(
        string sha,
        string? repository,
        CancellationToken ct = default);

    Task SetOverlayRunAsync(long overlayId, long indexerRunId, CancellationToken ct = default);

    /// <summary>Moves a non-expired overlay to <c>indexing</c>. Returns false when it no longer exists or has expired.</summary>
    Task<bool> MarkOverlayIndexingAsync(long overlayId, DateTime startedAt, CancellationToken ct = default);

    /// <summary>
    /// Atomically replaces the overlay's file/node/edge rows and marks it <c>ready</c>.
    /// Returns false when the overlay expired (or was removed) while indexing.
    /// </summary>
    Task<bool> CompleteOverlayAsync(
        long overlayId,
        string headSha,
        string baseSha,
        IReadOnlyList<BranchOverlayFileEntity> files,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        DateTime completedAt,
        CancellationToken ct = default);

    Task MarkOverlayFailedAsync(
        long overlayId,
        string error,
        string? headSha,
        string? baseSha,
        DateTime completedAt,
        CancellationToken ct = default);

    Task<IReadOnlyList<BranchOverlayFileEntity>> GetOverlayFilesAsync(long overlayId, CancellationToken ct = default);

    Task<IReadOnlyList<GraphNode>> GetOverlayNodesAsync(long overlayId, CancellationToken ct = default);

    Task<IReadOnlyList<PendingEdge>> GetOverlayEdgesAsync(long overlayId, CancellationToken ct = default);

    /// <summary>
    /// Deletes all file/node/edge rows of every non-expired overlay created at or before
    /// <paramref name="createdAtOrBefore"/> and marks it <c>expired</c>. Returns the expired ids.
    /// </summary>
    Task<IReadOnlyList<long>> ExpireOverlaysAsync(
        DateTime createdAtOrBefore,
        DateTime expiredAt,
        CancellationToken ct = default);

    /// <summary>
    /// Serializes overlay requests for <paramref name="repository"/> across hosts, so two concurrent
    /// identical requests cannot both miss the live overlay and create two. Disposing releases it.
    /// </summary>
    Task<IAsyncDisposable> AcquireOverlayRequestLockAsync(string repository, CancellationToken ct = default);
}

public static class BranchOverlayStatuses
{
    public const string Queued = "queued";
    public const string Indexing = "indexing";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Expired = "expired";
}

public static class BranchOverlayChangeKinds
{
    public const string Added = "added";
    public const string Modified = "modified";
    public const string Deleted = "deleted";

    /// <summary>The new path of a rename; the old path is recorded separately as <see cref="Deleted"/>.</summary>
    public const string Renamed = "renamed";
}

public class BranchOverlayEntity
{
    public long Id { get; set; }
    public string Repository { get; set; } = "";
    public string RequestedRef { get; set; } = "";
    public string? HeadSha { get; set; }
    public string? BaseSha { get; set; }
    public string Status { get; set; } = BranchOverlayStatuses.Queued;
    public string? Error { get; set; }
    public string? RequestedByUsername { get; set; }
    public long? IndexerRunId { get; set; }
    public int ChangedFileCount { get; set; }
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ExpiredAt { get; set; }
}

public class BranchOverlayFileEntity
{
    public long OverlayId { get; set; }
    public string FilePath { get; set; } = "";
    public string ChangeKind { get; set; } = "";
    public string? PreviousPath { get; set; }
}
