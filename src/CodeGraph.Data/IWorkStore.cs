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

    Task<ItemView?> GetItemAsync(Guid itemId, CancellationToken ct = default);

    Task<FindWorkResult> FindAsync(FindWorkSpec spec, CancellationToken ct = default);

    /// <summary>The sequence of the newest change-feed entry, 0 for an empty feed.</summary>
    Task<long> GetChangeHeadAsync(CancellationToken ct = default);

    /// <summary>Entries after <paramref name="afterSequence"/>, oldest first.</summary>
    Task<IReadOnlyList<ChangeView>> ReadChangesAsync(Guid? projectId, long afterSequence, int limit, CancellationToken ct = default);
}

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
    ClaimSpec? Claim = null);

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
