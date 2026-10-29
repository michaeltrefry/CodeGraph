using CodeGraph.Models.Work;

namespace CodeGraph.Data;

/// <summary>
/// Persistence of the work tracker (projects, features, items, dependencies, claims, links,
/// comments, idempotency keys and the change feed). Every write runs in one transaction that
/// ends with its change-feed entries, returns the resulting state, and is refused with a
/// <see cref="WorkException"/> carrying one of <see cref="WorkErrorCodes"/>. The specs passed in
/// are already validated for shape by the service; the store checks what needs the database.
/// </summary>
public interface IWorkStore
{
    Task<WorkCreated<ProjectView>> CreateProjectAsync(NewProjectSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default);

    Task<ProjectView> UpdateProjectAsync(Guid projectId, long expectedVersion, ProjectPatchSpec patch, WorkActor actor, CancellationToken ct = default);

    Task<WorkCreated<FeatureView>> CreateFeatureAsync(Guid projectId, NewFeatureSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default);

    Task<FeatureView> UpdateFeatureAsync(Guid featureId, long expectedVersion, FeaturePatchSpec patch, WorkActor actor, CancellationToken ct = default);

    Task<WorkCreated<ItemView>> CreateItemAsync(Guid projectId, NewItemSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default);

    Task<ItemView> UpdateItemAsync(Guid itemId, long expectedVersion, ItemPatchSpec patch, WorkActor actor, CancellationToken ct = default);

    Task<PlanResult> ImportPlanAsync(Guid projectId, PlanSpec plan, WorkActor actor, string keyHash, CancellationToken ct = default);

    Task<NextWorkResult> NextWorkAsync(NextWorkSpec spec, WorkActor actor, CancellationToken ct = default);

    /// <summary>Resolves a key (<c>SW</c>, <c>SW-142</c>, <c>SW-F12</c>) or a GUID of any kind.</summary>
    Task<WorkTarget?> ResolveAsync(string idOrKey, CancellationToken ct = default);

    Task<ProjectView?> GetProjectAsync(Guid projectId, CancellationToken ct = default);

    Task<FeatureView?> GetFeatureAsync(Guid featureId, CancellationToken ct = default);

    /// <summary>An item with its latest handoff and the handoff count, or, with <paramref name="fullHandoffs"/>, every handoff.</summary>
    Task<ItemView?> GetItemAsync(Guid itemId, bool fullHandoffs, CancellationToken ct = default);

    Task<FindWorkResult> FindAsync(FindWorkSpec spec, CancellationToken ct = default);

    /// <summary>The sequence of the newest change-feed entry, 0 for an empty feed.</summary>
    Task<long> GetChangeHeadAsync(CancellationToken ct = default);

    /// <summary>Entries after <paramref name="afterSequence"/>, oldest first.</summary>
    Task<IReadOnlyList<ChangeView>> ReadChangesAsync(Guid? projectId, long afterSequence, int limit, CancellationToken ct = default);

    // ---- Sessions (sc-27866) -----------------------------------------------------------------

    /// <summary>Registers a session of <paramref name="actor"/>'s identity; a repeated key answers the first one.</summary>
    Task<WorkCreated<SessionView>> StartSessionAsync(NewSessionSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default);

    /// <summary>
    /// The heartbeat: renews the session and its live claims, and answers the actor the call is
    /// attributed to (the identity with the session's harness, model and host). Refused when the
    /// session does not exist (not_found), belongs to another identity or has ended (invalid_argument).
    /// </summary>
    Task<WorkActor> TouchSessionAsync(Guid sessionId, WorkActor actor, CancellationToken ct = default);

    /// <summary>
    /// Ends the session: writes the handoff (on <paramref name="itemId"/>, or the one item the session
    /// holds), releases every claim of the session. Ending an ended session changes nothing and answers it.
    /// </summary>
    Task<EndSessionResult> EndSessionAsync(Guid sessionId, Guid? itemId, HandoffSpec? handoff, WorkActor actor, CancellationToken ct = default);

    Task<SessionView?> GetSessionAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Every section of the briefing of a session, unbudgeted (each list capped).</summary>
    Task<BriefingView> ReadBriefingAsync(Guid sessionId, Guid? focusItemId, CancellationToken ct = default);

    /// <summary>The project owning <paramref name="repository"/>, if exactly one does.</summary>
    Task<Guid?> FindOwningProjectAsync(string repository, CancellationToken ct = default);

    // ---- Resources and holds (sc-27872) -------------------------------------------------------

    /// <summary>
    /// Takes a slot of a resource, creating the resource when it is new. Granted when a slot is
    /// free (after expired holds and dead waiters are swept and queued waiters promoted); else a
    /// waiting entry when <see cref="AcquireSpec.Wait"/>, else not granted. A repeat by the same holder
    /// (or key) answers its hold. Capacity accounting is serialised per resource.
    /// </summary>
    Task<AcquireResult> AcquireAsync(AcquireSpec spec, WorkActor actor, CancellationToken ct = default);

    /// <summary>Extends a held hold to now + ttl. Refused conflict when it lapsed, was released or still waits; never regrants.</summary>
    Task<HoldView> RenewHoldAsync(Guid holdId, int? ttlSeconds, WorkActor actor, CancellationToken ct = default);

    /// <summary>Releases a hold or leaves the queue, then promotes waiters. Releasing an ended hold changes nothing.</summary>
    Task<ReleaseResult> ReleaseHoldAsync(Guid holdId, WorkActor actor, CancellationToken ct = default);
}

public sealed record AcquireSpec(
    Guid ProjectId,
    string Resource,
    int TtlSeconds,
    string Purpose,
    Guid? ItemId,
    bool Wait,
    WorkPriority Priority,
    int? Capacity,
    IReadOnlyList<string>? Labels,
    string? KeyHash);

public sealed record NewSessionSpec(
    Guid ProjectId,
    string Harness,
    string? Model,
    string? Host,
    string? Repository,
    string? Branch,
    string? Worktree,
    string? Intent);

public sealed record HandoffSpec(
    string? Done,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> OpenIssues,
    IReadOnlyList<string> Corrections,
    string? NextAction,
    string? Branch,
    string? Commit);

public sealed record WorkTarget(WorkEntityKind Kind, Guid Id, Guid ProjectId);

public sealed record WorkCreated<T>(bool Created, T Value);

public sealed record NewProjectSpec(
    string Key,
    string Name,
    string? Description,
    IReadOnlyList<string> Goals,
    IReadOnlyList<RepositoryMemberView> Repositories);

/// <summary>A field of a patch is set when it is not null; <c>Clear*</c> empties an optional one.</summary>
public sealed record ProjectPatchSpec(
    string? Name = null,
    string? Description = null,
    bool ClearDescription = false,
    IReadOnlyList<string>? Goals = null,
    IReadOnlyList<RepositoryMemberView>? Repositories = null);

public sealed record DocumentSpec(string Title, string Body);

public sealed record DocumentPatchSpec(Guid Id, string? Title, string? Body);

public sealed record NewFeatureSpec(string Title, string? Description, IReadOnlyList<DocumentSpec> Documents);

public sealed record FeaturePatchSpec(
    string? Title = null,
    string? Description = null,
    bool ClearDescription = false,
    IReadOnlyList<DocumentSpec>? AddDocuments = null,
    IReadOnlyList<DocumentPatchSpec>? UpdateDocuments = null);

public sealed record NewItemSpec(
    string Title,
    WorkItemType Type,
    WorkStatus Status,
    WorkPriority Priority,
    string? Description,
    IReadOnlyList<string> Labels,
    string? Repository,
    Guid? FeatureId,
    IReadOnlyList<Guid> BlockedBy,
    int Order);

public sealed record LinkSpec(WorkLinkKind Kind, string Url);

public enum ClaimAction
{
    Claim,
    Renew,
    Release
}

public sealed record ClaimSpec(ClaimAction Action, string Marker, int TtlSeconds);

public sealed record ItemPatchSpec(
    string? Title = null,
    string? Description = null,
    bool ClearDescription = false,
    WorkItemType? Type = null,
    WorkStatus? Status = null,
    WorkPriority? Priority = null,
    IReadOnlyList<string>? Labels = null,
    bool ClearLabels = false,
    string? Repository = null,
    bool ClearRepository = false,
    Guid? FeatureId = null,
    bool ClearFeature = false,
    int? Order = null,
    IReadOnlyList<Guid>? AddBlockedBy = null,
    IReadOnlyList<Guid>? RemoveBlockedBy = null,
    IReadOnlyList<LinkSpec>? AddLinks = null,
    IReadOnlyList<LinkSpec>? RemoveLinks = null,
    string? Comment = null,
    ClaimSpec? Claim = null,
    HandoffSpec? Handoff = null)
{
    /// <summary>Whether the patch only claims the item (a retried claim may then answer the claim it already holds).</summary>
    public bool IsClaimOnly =>
        Claim is { Action: ClaimAction.Claim } && Title is null && Description is null && !ClearDescription && Type is null && Status is null &&
        Priority is null && Labels is null && !ClearLabels && Repository is null && !ClearRepository && FeatureId is null && !ClearFeature &&
        Order is null && AddBlockedBy is not { Count: > 0 } && RemoveBlockedBy is not { Count: > 0 } && AddLinks is not { Count: > 0 } &&
        RemoveLinks is not { Count: > 0 } && Comment is null && Handoff is null;

    /// <summary>The patch without its claim action.</summary>
    public bool IsEmptyBesidesClaim => (this with { Claim = new ClaimSpec(ClaimAction.Claim, "", 0) }).IsClaimOnly;
}

public sealed record PlanFeatureSpec(string Ref, NewFeatureSpec Feature);

/// <param name="FeatureIndex">Index into <see cref="PlanSpec.Features"/>, or null.</param>
/// <param name="BlockedBy">Indexes into <see cref="PlanSpec.Items"/>.</param>
public sealed record PlanItemSpec(
    string Ref,
    string Title,
    WorkItemType Type,
    WorkStatus Status,
    WorkPriority Priority,
    string? Description,
    IReadOnlyList<string> Labels,
    string? Repository,
    int? FeatureIndex,
    IReadOnlyList<int> BlockedBy,
    int Order);

public sealed record PlanSpec(IReadOnlyList<PlanFeatureSpec> Features, IReadOnlyList<PlanItemSpec> Items);

public sealed record NextWorkSpec(
    Guid ProjectId,
    string Marker,
    int TtlSeconds,
    bool Another,
    string? KeyHash,
    string? Repository,
    WorkItemType? Type,
    string? Label);

public sealed record FindWorkSpec(
    WorkEntityKind Kind,
    Guid? ProjectId,
    IReadOnlyList<WorkStatus>? Statuses,
    WorkItemType? Type,
    WorkPriority? Priority,
    string? Repository,
    Guid? FeatureId,
    string? Label,
    bool? Ready,
    bool? Claimed,
    IReadOnlyList<string> Words,
    WorkDetail Detail,
    int Limit,
    // Items and features page by number, projects by key: the last one of the previous page.
    string? After);
