using System.ComponentModel;
using System.Text.Json;

namespace CodeGraph.Models.Work;

// Feature planning, re-planning and completion (sc-27868): feature goals, numbered requirements
// (E1..En) and acceptance tests; item goal, acceptance criteria, inherits and touches; sync_plan
// with versioned previews; execution attempts; acceptance reports; changed_by_replan notices.

// ---- Arguments -----------------------------------------------------------------------------------

public sealed class RequirementInput
{
    [Description("The requirement's number, E1 to E9999, unique in the feature and kept across re-plans.")]
    public required string Id { get; init; }

    [Description("What the requirement demands, at most 2000 characters.")]
    public required string Text { get; init; }
}

public sealed class DropRequirementInput
{
    [Description("Number of an active requirement of the feature (E2).")]
    public required string Id { get; init; }

    [Description("The decision that drops it, at most 2000 characters. A dropped requirement no longer has to be covered for the feature to be complete.")]
    public required string Decision { get; init; }
}

public sealed class AcceptanceCriterionInput
{
    [Description("Id of the criterion within the item, such as AC1: a letter, then up to 15 letters, digits, - or _. Default: AC1, AC2, AC3 by position. A criterion that keeps its id and text keeps its state and evidence.")]
    public string? Id { get; init; }

    [Description("What must be true, at most 2000 characters.")]
    public required string Text { get; init; }

    [Description("unmet (default) or met.")]
    public AcceptanceState? State { get; init; }

    [Description("Evidence the criterion is met (test name, link, output), at most 4000 characters.")]
    public string? Evidence { get; init; }
}

public sealed class AcceptanceReportInput
{
    [Description("Summary of how the item meets its acceptance criteria, at most 8000 characters.")]
    public string? Summary { get; init; }

    [Description("The state and evidence of named criteria of the item; they are stored on the criteria. CodeGraph does not check them.")]
    public List<AcceptanceResultInput>? Criteria { get; init; }
}

public sealed class AcceptanceResultInput
{
    [Description("Id of an acceptance criterion of the item.")]
    public required string Id { get; init; }

    public required AcceptanceState State { get; init; }

    [Description("At most 4000 characters.")]
    public string? Evidence { get; init; }
}

public sealed class SyncPlanRequest : ISessionArguments
{
    [Description("Id of the caller's session (from start_session). Renews the session's heartbeat, attributes the call to it, and binds claims to it.")]
    public string? SessionId { get; init; }

    [Description("Key (SW-F12) or id of the feature to re-plan. Required with plan.")]
    public string? Feature { get; init; }

    [Description("The revised plan of the feature: answered with a preview and its preview_token; nothing is changed.")]
    public RevisedPlanInput? Plan { get; init; }

    [Description("A preview_token from an earlier sync_plan: applies that preview if nothing it covers changed since, else refused conflict (regenerate the preview). Pass plan or confirm, not both.")]
    public string? Confirm { get; init; }
}

public sealed class RevisedPlanInput
{
    [Description("New title of the feature. Left out: unchanged.")]
    public string? Title { get; init; }

    [Description("New description of the feature. Left out: unchanged.")]
    public string? Description { get; init; }

    [Description("Replaces the goals of the feature. Left out: unchanged.")]
    public List<string>? Goals { get; init; }

    [Description("The active requirements of the feature, in order. Left out: unchanged. Every active requirement left out must be named in drop_requirements.")]
    public List<RequirementInput>? Requirements { get; init; }

    [Description("Active requirements to drop, each with the decision that drops it.")]
    public List<DropRequirementInput>? DropRequirements { get; init; }

    [Description("Replaces the acceptance tests of the feature. Left out: unchanged.")]
    public List<string>? AcceptanceTests { get; init; }

    [Description("Every item the feature should hold, 0 to 200, matched by ref to the feature's items (an item's ref, or its key such as SW-142). A ref that matches no item adds one. An item of the feature left out is closed, unless it is complete or closed already. An item that is in progress or claimed may be edited or closed: its current attempt is superseded and its holder is notified (changed_by_replan).")]
    public required List<SyncPlanItemInput> Items { get; init; }
}

public sealed class SyncPlanItemInput
{
    [Description("The item's ref in the plan (1 to 64 characters, compared exactly), or the key of an existing item of the feature or of no feature.")]
    public required string Ref { get; init; }

    [Description("Required for a new item. Every other field left out leaves an existing item's value unchanged.")]
    public string? Title { get; init; }

    public WorkItemType? Type { get; init; }

    [Description("Any status but complete: a re-plan never completes an item (mark it complete with update_work).")]
    public WorkStatus? Status { get; init; }

    public WorkPriority? Priority { get; init; }

    public string? Description { get; init; }

    public string? Goal { get; init; }

    public List<string>? Labels { get; init; }

    public string? Repository { get; init; }

    [Description("Replaces the item's acceptance criteria (at most 10).")]
    public List<AcceptanceCriterionInput>? AcceptanceCriteria { get; init; }

    [Description("Replaces the requirements (E2) of the feature the item serves.")]
    public List<string>? Inherits { get; init; }

    [Description("Replaces the paths and symbols the item is expected to touch.")]
    public List<string>? Touches { get; init; }

    [Description("Replaces the item's blockers: refs of items of this plan, or keys of other items of the project.")]
    public List<string>? BlockedBy { get; init; }

    public int? Order { get; init; }
}

// ---- Views -----------------------------------------------------------------------------------------

/// <param name="CoveredBy">Keys of the complete items that inherit the requirement (feature view only).</param>
public sealed record RequirementView(string Id, string Text, RequirementState State, string? Decision = null, IReadOnlyList<string>? CoveredBy = null);

/// <summary>An acceptance criterion of an item, as stored and answered.</summary>
public sealed record AcceptanceCriterionView(string Id, string Text, AcceptanceState State, string? Evidence = null);

public sealed record AcceptanceResultView(string Id, AcceptanceState State, string? Evidence = null);

/// <summary>The optional report an item was marked complete with. Stored as written; not validated.</summary>
public sealed record AcceptanceReportView(string ReportedBy, DateTime ReportedAt, string? Summary = null, IReadOnlyList<AcceptanceResultView>? Criteria = null, string? SessionId = null);

/// <summary>How far a feature is from complete (§5.2: closing every item does not complete a feature).</summary>
public sealed record FeatureCompletionView(int Items, int Finished, int Complete, IReadOnlyList<string>? UncoveredRequirements = null);

/// <summary>
/// An execution attempt: one holder's claim on the item with the branch and PR links and handoffs
/// made while it was active. Evidence of a superseded attempt never moves the item's status.
/// </summary>
public sealed record AttemptView(
    Guid Id,
    int Number,
    AttemptOutcome Outcome,
    HolderView Holder,
    DateTime StartedAt,
    DateTime? EndedAt = null,
    string? Reason = null,
    string? Branch = null,
    IReadOnlyList<string>? Branches = null,
    IReadOnlyList<string>? Prs = null,
    Guid? HandoffId = null);

/// <summary>A notice for the caller, carried in <c>notices</c> of its next work-tool response.</summary>
public sealed record NoticeView(
    string Kind,
    Guid ItemId,
    string Item,
    string By,
    string Summary,
    DateTime CreatedAt,
    string? Feature = null,
    bool? Closed = null,
    Guid? SupersededAttempt = null,
    JsonElement? Changes = null);

/// <param name="ReopensFinished">true when the entry changes an item that is complete or closed: confirming rewrites or reopens finished work.</param>
public sealed record PlanItemChange(string Ref, string? Key, string Title, JsonElement? Changes = null, bool? Pivot = null, bool? ReopensFinished = null);

public sealed record PlanDependencyChange(string Item, string BlockedBy);

public sealed record PlanPivot(string Key, string Action, HolderView? Holder = null, Guid? Attempt = null);

/// <summary>
/// What a revised plan would change, and the token that applies it. The token is bound to the
/// versions of the feature (its requirements included), every item of the feature or named by the
/// plan, and the dependency edges into them: if any changed, confirming is refused conflict.
/// </summary>
public sealed record SyncPlanPreview(
    string PreviewToken,
    DateTime ExpiresAt,
    string Feature,
    long FeatureVersion,
    bool Unchanged,
    JsonElement? FeatureChanges = null,
    IReadOnlyList<string>? RequirementsAdded = null,
    IReadOnlyList<string>? RequirementsChanged = null,
    IReadOnlyList<string>? RequirementsDropped = null,
    IReadOnlyList<PlanItemChange>? ItemsAdded = null,
    IReadOnlyList<PlanItemChange>? ItemsEdited = null,
    IReadOnlyList<PlanItemChange>? ItemsClosed = null,
    IReadOnlyList<PlanDependencyChange>? DependenciesAdded = null,
    IReadOnlyList<PlanDependencyChange>? DependenciesRemoved = null,
    IReadOnlyList<PlanPivot>? Pivots = null,
    IReadOnlyList<string>? Warnings = null)
{
    public string Kind => "plan_preview";
}

/// <param name="Repeated">true when the preview had been applied already: this is its stored answer.</param>
/// <param name="Items">The id and key of every item of the plan, by ref.</param>
public sealed record SyncPlanApplied(
    bool Repeated,
    FeatureView Feature,
    IReadOnlyDictionary<string, CreatedRef> Items,
    IReadOnlyList<string>? Added = null,
    IReadOnlyList<string>? Edited = null,
    IReadOnlyList<string>? Closed = null,
    IReadOnlyList<PlanPivot>? Pivots = null)
{
    public string Kind => "plan_applied";
}
