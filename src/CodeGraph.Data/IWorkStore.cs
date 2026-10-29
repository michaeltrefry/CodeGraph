using CodeGraph.Models.Work;

namespace CodeGraph.Data;

/// <summary>
/// Persistence of the work tracker (projects, features, items, dependencies, claims, links,
/// comments, idempotency keys and the change feed). Every write runs in one transaction that
/// ends with its change-feed entries, returns the resulting state, and is refused with a
/// <see cref="WorkException"/> carrying one of <see cref="WorkErrorCodes"/>. The specs passed in
/// are already validated for shape by the service; the store checks what needs the database.
/// </summary>
public interface IWorkStore : IWorkMessageStore
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
    /// <paramref name="knowledge"/> (the save-point fields, S5) is stored in the same transaction,
    /// linked to the handoff's item, else <paramref name="itemId"/>, else the one item the session holds.
    /// </summary>
    Task<EndSessionResult> EndSessionAsync(Guid sessionId, Guid? itemId, HandoffSpec? handoff, IReadOnlyList<KnowledgeSpec>? knowledge, WorkActor actor, CancellationToken ct = default);

    Task<SessionView?> GetSessionAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Every section of the briefing of a session, unbudgeted (each list capped).</summary>
    Task<BriefingView> ReadBriefingAsync(Guid sessionId, Guid? focusItemId, CancellationToken ct = default);

    /// <summary>The project owning <paramref name="repository"/>, if exactly one does.</summary>
    Task<Guid?> FindOwningProjectAsync(string repository, CancellationToken ct = default);

    // ---- Read-only listings for the UI (sc-27874) ---------------------------------------------

    /// <summary>The sessions of a project, most recently seen first: the live ones, or with <paramref name="liveOnly"/> false the ended and silent ones too.</summary>
    Task<IReadOnlyList<SessionView>> ListSessionsAsync(Guid projectId, bool liveOnly, int limit, CancellationToken ct = default);

    /// <summary>A page of a project's resources by name (after <paramref name="afterName"/>), each with its holders and queued waiters as of now. Nothing is swept or written.</summary>
    Task<IReadOnlyList<ResourceView>> ListResourcesAsync(Guid projectId, string? afterName, int limit, CancellationToken ct = default);

    /// <summary>The newest <paramref name="limit"/> change feed entries of a project, oldest first (read only).</summary>
    Task<IReadOnlyList<ChangeView>> ReadRecentChangesAsync(Guid projectId, int limit, CancellationToken ct = default);

    /// <summary>The newest <paramref name="limit"/> change feed entries of one feature or item, oldest first (read only).</summary>
    Task<IReadOnlyList<ChangeView>> ReadEntityChangesAsync(Guid entityId, int limit, CancellationToken ct = default);

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

    /// <summary>
    /// The work areas of a project (sc-27872): each live claim held by a live session, on an
    /// unfinished item that has touches. Read-only; a claim whose session is silent past the idle
    /// threshold, or lapsed, is not listed.
    /// </summary>
    Task<IReadOnlyList<WorkArea>> ReadLiveWorkAreasAsync(Guid projectId, CancellationToken ct = default);

    // ---- Planning (sc-27868) -----------------------------------------------------------------

    /// <summary>
    /// The preview of a revised plan of a feature: what applying it would change, stored with the
    /// versions of everything it covers under a token of <paramref name="actor"/>'s identity. Writes
    /// no work: only the preview row.
    /// </summary>
    Task<SyncPlanPreview> PreviewPlanAsync(Guid featureId, RevisedPlanSpec plan, WorkActor actor, CancellationToken ct = default);

    /// <summary>
    /// Applies a stored preview all-or-nothing, if nothing it covers changed since (else conflict).
    /// A preview applied already answers its stored result (repeated). With
    /// <paramref name="projectId"/>, a preview of another project is refused.
    /// </summary>
    Task<SyncPlanApplied> ApplyPlanAsync(Guid previewId, Guid? projectId, WorkActor actor, CancellationToken ct = default);

    /// <summary>
    /// The undelivered notices for <paramref name="actor"/> (its session; or, without one, its
    /// identity's claims made without a session), marked delivered.
    /// </summary>
    Task<IReadOnlyList<NoticeView>> TakeNoticesAsync(WorkActor actor, CancellationToken ct = default);

    // ---- GitHub evidence (sc-27870) ----------------------------------------------------------

    /// <summary>
    /// Records one GitHub event on every item it names (by key or alias in the branch, PR title or PR
    /// body, or by a PR the item links): links, forward-only status moves, merge and review evidence
    /// (design §7). Each item is written in its own transaction with its change-feed entry, attributed
    /// to the GitHub login; a delivery handled for an item already changes nothing. Evidence tied
    /// only to superseded attempts is recorded and moves nothing.
    /// </summary>
    Task<GitHubEvidenceResult> RecordGitHubEvidenceAsync(GitHubEvidenceSpec spec, CancellationToken ct = default);

    /// <summary>The GitHub evidence of an item, newest first.</summary>
    Task<IReadOnlyList<GitHubEvidenceView>> ListGitHubEvidenceAsync(Guid itemId, int limit, CancellationToken ct = default);

    /// <summary>
    /// Gives an item external aliases (an imported story's <c>sc-27866</c>), lower-cased; an alias
    /// names one item (a repeat on the same item changes nothing, another item's is conflict).
    /// A versioned, fed item write.
    /// </summary>
    Task<ItemView> AddItemAliasesAsync(Guid itemId, IReadOnlyList<string> aliases, WorkActor actor, CancellationToken ct = default);
}

/// <summary>A requirement as a write names it (validated for shape).</summary>
public sealed record RequirementSpec(string Id, string Text);

public sealed record RequirementDropSpec(string Id, string Decision);

/// <summary>Acceptance criteria as written: an id null when the caller left it to the position.</summary>
public sealed record CriterionSpec(string? Id, string Text, AcceptanceState? State, string? Evidence);

public sealed record AcceptanceReportSpec(string? Summary, IReadOnlyList<AcceptanceResultView> Criteria);

/// <summary>The planning fields of a new item.</summary>
public sealed record ItemPlanFields(
    string? Goal = null,
    IReadOnlyList<CriterionSpec>? AcceptanceCriteria = null,
    IReadOnlyList<string>? Inherits = null,
    IReadOnlyList<string>? Touches = null,
    string? Ref = null);

/// <summary>The planning fields of a new feature.</summary>
public sealed record FeaturePlanFields(
    IReadOnlyList<string>? Goals = null,
    IReadOnlyList<RequirementSpec>? Requirements = null,
    IReadOnlyList<string>? AcceptanceTests = null);

/// <summary>A blocker in a revised plan: another item of the plan (by index) or an item of the project.</summary>
public sealed record PlanBlockerSpec(int? PlanIndex, Guid? ItemId);

/// <param name="KeyMatch">The item the ref names when the ref is the key of an item of the project.</param>
public sealed record RevisedItemSpec(
    string Ref,
    Guid? KeyMatch,
    string? Title,
    WorkItemType? Type,
    WorkStatus? Status,
    WorkPriority? Priority,
    string? Description,
    string? Goal,
    IReadOnlyList<string>? Labels,
    string? Repository,
    IReadOnlyList<CriterionSpec>? AcceptanceCriteria,
    IReadOnlyList<string>? Inherits,
    IReadOnlyList<string>? Touches,
    IReadOnlyList<PlanBlockerSpec>? BlockedBy,
    int? Order);

public sealed record RevisedPlanSpec(
    string? Title,
    string? Description,
    IReadOnlyList<string>? Goals,
    IReadOnlyList<RequirementSpec>? Requirements,
    IReadOnlyList<RequirementDropSpec>? DropRequirements,
    IReadOnlyList<string>? AcceptanceTests,
    IReadOnlyList<RevisedItemSpec> Items);

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
    string? Intent,
    // The activities the session declared (S9): its briefings serve the rules that apply to them.
    IReadOnlyList<LessonActivity>? Activity = null,
    IReadOnlyList<string>? Skills = null);

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
    IReadOnlyList<RepositoryMemberView>? Repositories = null,
    // The save-point fields (S5), project-wide unless they name repositories.
    IReadOnlyList<KnowledgeSpec>? Knowledge = null);

public sealed record DocumentSpec(string Title, string Body);

public sealed record DocumentPatchSpec(Guid Id, string? Title, string? Body);

public sealed record NewFeatureSpec(string Title, string? Description, IReadOnlyList<DocumentSpec> Documents, FeaturePlanFields? Plan = null);

public sealed record FeaturePatchSpec(
    string? Title = null,
    string? Description = null,
    bool ClearDescription = false,
    IReadOnlyList<DocumentSpec>? AddDocuments = null,
    IReadOnlyList<DocumentPatchSpec>? UpdateDocuments = null,
    IReadOnlyList<string>? Goals = null,
    bool ClearGoals = false,
    IReadOnlyList<RequirementSpec>? Requirements = null,
    IReadOnlyList<RequirementDropSpec>? DropRequirements = null,
    IReadOnlyList<string>? AcceptanceTests = null,
    bool ClearAcceptanceTests = false,
    // The save-point fields (S5), linked to the feature.
    IReadOnlyList<KnowledgeSpec>? Knowledge = null);

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
    int Order,
    ItemPlanFields? Plan = null);

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
    HandoffSpec? Handoff = null,
    string? Goal = null,
    bool ClearGoal = false,
    IReadOnlyList<CriterionSpec>? AcceptanceCriteria = null,
    bool ClearAcceptanceCriteria = false,
    IReadOnlyList<string>? Inherits = null,
    bool ClearInherits = false,
    IReadOnlyList<string>? Touches = null,
    bool ClearTouches = false,
    AcceptanceReportSpec? AcceptanceReport = null,
    // Set from merge evidence (S6); not a tool argument. Never completes the item.
    bool? MergedAwaitingAcceptance = null,
    // The save-point fields (S5): knowledge entries stored in the write's transaction, linked to the item.
    IReadOnlyList<KnowledgeSpec>? Knowledge = null)
{
    /// <summary>Whether the patch only claims the item (a retried claim may then answer the claim it already holds).</summary>
    public bool IsClaimOnly =>
        Claim is { Action: ClaimAction.Claim } && Title is null && Description is null && !ClearDescription && Type is null && Status is null &&
        Priority is null && Labels is null && !ClearLabels && Repository is null && !ClearRepository && FeatureId is null && !ClearFeature &&
        Order is null && AddBlockedBy is not { Count: > 0 } && RemoveBlockedBy is not { Count: > 0 } && AddLinks is not { Count: > 0 } &&
        RemoveLinks is not { Count: > 0 } && Comment is null && Handoff is null &&
        Goal is null && !ClearGoal && AcceptanceCriteria is null && !ClearAcceptanceCriteria && Inherits is null && !ClearInherits &&
        Touches is null && !ClearTouches && AcceptanceReport is null && MergedAwaitingAcceptance is null && Knowledge is not { Count: > 0 };

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
    int Order,
    ItemPlanFields? Plan = null);

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
