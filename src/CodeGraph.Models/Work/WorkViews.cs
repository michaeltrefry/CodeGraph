namespace CodeGraph.Models.Work;

// What the work tools answer. Serialized with WorkJson.Options (snake_case, nulls left out).

/// <summary>Who made a write. S2 adds the session (harness, model, host) through <see cref="SessionId"/>.</summary>
public sealed record WorkActor(string Name, string Kind = WorkActor.UserKind, string? SessionId = null)
{
    public const string UserKind = "user";
}

public sealed record RepositoryMemberView(string Repository, RepositoryRole Role);

public sealed record ProjectView(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    IReadOnlyList<string> Goals,
    IReadOnlyList<RepositoryMemberView> Repositories,
    long Version,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyDictionary<string, int>? ItemCounts = null)
{
    public string Kind => "project";
}

public sealed record DocumentView(Guid Id, string Title, string Body, int Position, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record FeatureView(
    Guid Id,
    string Key,
    Guid ProjectId,
    string ProjectKey,
    string Title,
    string? Description,
    IReadOnlyList<DocumentView> Documents,
    long Version,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyDictionary<string, int>? ItemCounts = null)
{
    public string Kind => "feature";
}

public sealed record EntityRef(Guid Id, string Key, string Title);

public sealed record DependencyView(Guid Id, string Key, string Title, WorkStatus Status);

public sealed record ClaimView(string Holder, string Marker, DateTime ClaimedAt, DateTime ExpiresAt, string? SessionId = null);

public sealed record LinkView(Guid Id, WorkLinkKind Kind, string Url, DateTime CreatedAt);

public sealed record CommentView(Guid Id, string Author, string Body, DateTime CreatedAt);

public sealed record ItemView(
    Guid Id,
    string Key,
    Guid ProjectId,
    string ProjectKey,
    EntityRef? Feature,
    WorkItemType Type,
    string Title,
    string? Description,
    WorkStatus Status,
    WorkPriority Priority,
    int Order,
    IReadOnlyList<string> Labels,
    string? Repository,
    bool Blocked,
    string? BlockedReason,
    IReadOnlyList<DependencyView> BlockedBy,
    IReadOnlyList<DependencyView> Blocks,
    ClaimView? Claim,
    IReadOnlyList<LinkView> Links,
    IReadOnlyList<CommentView> Comments,
    long Version,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public string Kind => "item";
}

/// <summary>One line of a list (E4): enough to choose, not to work.</summary>
public sealed record WorkRow(
    Guid Id,
    string Key,
    string Kind,
    string Title,
    WorkStatus? Status = null,
    WorkPriority? Priority = null,
    WorkItemType? Type = null,
    bool? Blocked = null,
    string? ClaimedBy = null,
    long? Version = null);

public sealed record FindWorkResult(IReadOnlyList<object> Items, string? NextCursor);

public sealed record CreatedRef(Guid Id, string Key);

public sealed record PlanResult(
    bool Created,
    Guid ProjectId,
    string ProjectKey,
    IReadOnlyDictionary<string, CreatedRef> Features,
    IReadOnlyDictionary<string, CreatedRef> Items);

public sealed record CreateWorkResult(
    bool Created,
    ProjectView? Project = null,
    FeatureView? Feature = null,
    ItemView? Item = null);

public sealed record UpdateWorkResult(ProjectView? Project = null, FeatureView? Feature = null, ItemView? Item = null);

/// <param name="Reused">true when this is the caller's existing claim or the allocation of a repeated key, not a new claim.</param>
/// <param name="Reason">Why there is no item: <c>no_ready_work</c>.</param>
public sealed record NextWorkResult(ItemView? Item, bool Reused, string? Reason = null);

public sealed record ChangeView(
    long Sequence,
    Guid ProjectId,
    string ProjectKey,
    WorkEntityKind Entity,
    Guid EntityId,
    string EntityKey,
    string Kind,
    WorkActor Actor,
    System.Text.Json.JsonElement Changes,
    DateTime CreatedAt);

public sealed record ChangesResult(IReadOnlyList<ChangeView> Items, string NextCursor);

public sealed record WorkError(string Code, string Message, object? Current = null);
