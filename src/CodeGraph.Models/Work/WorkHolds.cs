using System.ComponentModel;

namespace CodeGraph.Models.Work;

// Resources and holds (sc-27872; design §5.6 and §5.9). A resource is a named finite thing of a
// project with a capacity; a hold is one caller's use of one slot, with its own time to live and its
// own renewals. A hold does NOT lapse because its session went quiet: it frees on release, on
// end_session, or when its time to live runs out. Holds are advisory (E6): nothing enforces them.

public static class WorkHoldLimits
{
    public const int MaxResourceNameLength = 200;
    public const int MaxPurposeLength = 500;
    public const int MaxCapacity = 1000;
    public const int MaxLabels = 20;
    public const int MaxLabelLength = 64;
    // A hold's ttl is 1 to WorkLimits.MaxClaimTtlSeconds (24 h) from its grant or latest renewal,
    // default WorkLimits.DefaultClaimTtlSeconds: one source of truth with claims.
    /// <summary>
    /// A waiter whose acquire call has ended keeps its place in the queue for this long (and each
    /// poll, an acquire by the same holder, renews it), so a caller that polls does not lose its turn.
    /// </summary>
    public const int WaiterLingerSeconds = 120;
    /// <summary>The most holders and waiters one resource lists in an answer.</summary>
    public const int MaxListed = 50;
}

public static class WorkHoldStates
{
    public const string Waiting = "waiting";
    public const string Held = "held";
    public const string Released = "released";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
}

public static class WorkHoldOutcomes
{
    public const string Granted = "granted";
    public const string Held = "held";
}

public sealed class AcquireRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId + " The hold belongs to the session (end_session releases it) but does not depend on its heartbeat.")]
    public string? SessionId { get; init; }

    [Description("Key or id of the Project the resource belongs to. May be left out with session_id (the session's project).")]
    public string? Project { get; init; }

    [Description("Name of the resource, 1 to 200 characters of letters, digits and . _ : / @ + -, such as gpu:4090-box. Compared ignoring case. The resource is created by its first acquire.")]
    public required string Resource { get; init; }

    [Description("Seconds the hold lasts, unless renewed with renew_hold, 1 to 86400 (default 3600). Set it to cover the job.")]
    public int? TtlSeconds { get; init; }

    [Description("What the resource is used for, at most 500 characters; shown to anyone who finds it held.")]
    public required string Purpose { get; init; }

    [Description("Key or id of the item this use is for.")]
    public string? Item { get; init; }

    [Description("0 to 55 (default 0): when the resource is full, wait this long in the queue for a slot. Queued callers are served first come first served within a priority. A repeat call by the same holder resumes its place.")]
    public int? WaitSeconds { get; init; }

    [Description("Priority among waiters, low, medium (default), high or critical.")]
    public WorkPriority? Priority { get; init; }

    [Description("Capacity of the resource, 1 to 1000. Only used when this call creates the resource (default 1); for a resource that exists it must equal its capacity, else the call is refused conflict.")]
    public int? Capacity { get; init; }

    [Description("Labels of the resource, at most 20. Only used when this call creates the resource; for a resource that exists they must equal its labels, else the call is refused conflict.")]
    public List<string>? Labels { get; init; }

    [Description("Caller-chosen key: a repeat by the same identity answers the hold the first call made (even after it ended), with reused true.")]
    public string? IdempotencyKey { get; init; }
}

public sealed class RenewHoldRequest
{
    [Description("Id of the hold (from acquire). Only the identity that made the hold may renew it. A renewal is not a session heartbeat.")]
    public required string HoldId { get; init; }

    [Description("Seconds from now until the hold lapses unless renewed again, 1 to 86400 (default: the hold's own ttl_seconds). A hold that already lapsed, was released or is still waiting is refused conflict: acquire again.")]
    public int? TtlSeconds { get; init; }
}

public sealed class ReleaseHoldRequest
{
    [Description("Id of the hold (from acquire). Releases it, or leaves the queue when it is still waiting. Releasing an ended hold changes nothing and answers it.")]
    public required string HoldId { get; init; }
}

public sealed record HoldView(
    Guid Id,
    string Resource,
    string Project,
    HolderView Holder,
    string Purpose,
    string State,
    WorkPriority Priority,
    int TtlSeconds,
    DateTime RequestedAt,
    string? Item = null,
    DateTime? GrantedAt = null,
    DateTime? ExpiresAt = null,
    DateTime? RenewedAt = null,
    DateTime? EndedAt = null,
    int? QueuePosition = null,
    bool? OwnerUnreachable = null,
    DateTime? OwnerUnreachableSince = null,
    DateTime? OwnerLastSeenAt = null)
{
    public string Kind => "hold";
}

/// <summary>
/// A resource with who holds it and who waits. <see cref="Available"/> is true only when a slot is
/// free: a hold whose owner went silent still occupies its slot (it reads owner_unreachable on the
/// hold), so a resource is never shown available while a live hold sits on it.
/// </summary>
public sealed record ResourceView(
    Guid Id,
    string Project,
    string Name,
    int Capacity,
    IReadOnlyList<string> Labels,
    int Used,
    bool Available,
    IReadOnlyList<HoldView> Holders,
    IReadOnlyList<HoldView>? Waiters = null)
{
    public string Kind => "resource";
}

/// <summary>
/// The answer to acquire: <c>granted</c> with the caller's hold, or <c>held</c> with the resource's
/// holders, their purposes and expiry (and the caller's place when it waited: <see cref="Hold"/> is
/// then its waiting entry).
/// </summary>
public sealed record AcquireResult(
    string Outcome,
    ResourceView Resource,
    HoldView? Hold = null,
    bool? Reused = null,
    bool? ResourceCreated = null);

public sealed record ReleaseResult(HoldView Hold, ResourceView Resource);
