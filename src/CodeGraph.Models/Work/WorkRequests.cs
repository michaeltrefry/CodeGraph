using System.ComponentModel;

namespace CodeGraph.Models.Work;

// The arguments of the work tools. The same types are read from MCP tool arguments and from REST
// bodies with WorkJson.Options, which refuses any member a type does not declare, at every level,
// and any value of the wrong JSON type. A property left out or passed as null is not set.
// Names are snake_case on the wire (the naming policy of WorkJson.Options).

public sealed class CreateWorkRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId)]
    public string? SessionId { get; init; }

    [Description("Key (e.g. SW) or id of the Project to create the feature, item or plan in. Not passed with new_project.")]
    public string? Project { get; init; }

    [Description("Create a new Project. Pass exactly one of new_project, feature, item, plan.")]
    public NewProjectInput? NewProject { get; init; }

    [Description("Create one feature in project.")]
    public NewFeatureInput? Feature { get; init; }

    [Description("Create one item in project.")]
    public NewItemInput? Item { get; init; }

    [Description("Create a whole plan in project in one all-or-nothing call: features with documents, and items that name each other by ref.")]
    public PlanInput? Plan { get; init; }

    [Description("Caller-chosen key, 1 to 128 printable ASCII characters without spaces, compared exactly. Required with plan. A repeat by the same identity in the same place creates nothing and answers with what the key created, with created false.")]
    public string? IdempotencyKey { get; init; }
}

public sealed class NewProjectInput
{
    [Description("Short speakable key, 2 to 10 letters and digits starting with a letter, such as SW. Stored in upper case; item keys are <KEY>-<n>, feature keys <KEY>-F<n>. Cannot be changed later.")]
    public required string Key { get; init; }

    [Description("Name, at most 160 characters.")]
    public required string Name { get; init; }

    public string? Description { get; init; }

    [Description("Goals of the project, one line each.")]
    public List<string>? Goals { get; init; }

    [Description("Member repositories. A repository is owned by at most one project and may be a dependency of any number.")]
    public List<RepositoryMemberInput>? Repositories { get; init; }
}

public sealed class RepositoryMemberInput
{
    [Description("Repository name as CodeGraph indexes it.")]
    public required string Repository { get; init; }

    [Description("owned: work items, branches and PRs happen here. dependency: indexed for context, no work items.")]
    public required RepositoryRole Role { get; init; }
}

public sealed class NewFeatureInput
{
    [Description("Title, at most 240 characters.")]
    public required string Title { get; init; }

    public string? Description { get; init; }

    [Description("Documents of the feature (design notes, plans).")]
    public List<DocumentInput>? Documents { get; init; }

    [Description("Goals of the feature, one line each.")]
    public List<string>? Goals { get; init; }

    [Description("Numbered requirements (E1..En) every item of the feature serves through inherits.")]
    public List<RequirementInput>? Requirements { get; init; }

    [Description("Acceptance tests of the feature, one per entry.")]
    public List<string>? AcceptanceTests { get; init; }
}

public sealed class DocumentInput
{
    [Description("Title, at most 200 characters.")]
    public required string Title { get; init; }

    public required string Body { get; init; }
}

public sealed class NewItemInput
{
    [Description("Title, at most 240 characters.")]
    public required string Title { get; init; }

    [Description("story (default), task or bug.")]
    public WorkItemType? Type { get; init; }

    [Description("backlog (default), ready, in_progress, in_review, complete or closed.")]
    public WorkStatus? Status { get; init; }

    [Description("low, medium (default), high or critical.")]
    public WorkPriority? Priority { get; init; }

    public string? Description { get; init; }

    [Description("Labels; each non-empty and without a comma. Duplicates (ignoring case) are dropped.")]
    public List<string>? Labels { get; init; }

    [Description("Repository the work is done in: an owned repository of the project.")]
    public string? Repository { get; init; }

    [Description("Key or id of a feature of the project.")]
    public string? Feature { get; init; }

    [Description("Keys or ids of items of the project that block this one. Only a complete blocker stops blocking.")]
    public List<string>? BlockedBy { get; init; }

    [Description("Explicit order within a priority, lowest first (default 0). next_work orders by priority, then order, then creation.")]
    public int? Order { get; init; }

    [Description(WorkRequestText.Goal)]
    public string? Goal { get; init; }

    [Description(WorkRequestText.AcceptanceCriteria)]
    public List<AcceptanceCriterionInput>? AcceptanceCriteria { get; init; }

    [Description(WorkRequestText.Inherits)]
    public List<string>? Inherits { get; init; }

    [Description(WorkRequestText.Touches)]
    public List<string>? Touches { get; init; }
}

public sealed class PlanInput
{
    [Description("Features to create, at most 20.")]
    public List<PlanFeatureInput>? Features { get; init; }

    [Description("Items to create, 1 to 200.")]
    public required List<PlanItemInput> Items { get; init; }
}

public sealed class PlanFeatureInput
{
    [Description("The caller's name for this feature within the plan, 1 to 64 characters, compared exactly. Not stored.")]
    public required string Ref { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public List<DocumentInput>? Documents { get; init; }

    public List<string>? Goals { get; init; }

    [Description("Numbered requirements (E1..En); items of the plan name the ones they serve in inherits.")]
    public List<RequirementInput>? Requirements { get; init; }

    public List<string>? AcceptanceTests { get; init; }
}

public sealed class PlanItemInput
{
    [Description("The caller's name for this item within the plan, 1 to 64 characters, unique and compared exactly. Not stored.")]
    public required string Ref { get; init; }

    public required string Title { get; init; }

    public WorkItemType? Type { get; init; }

    public WorkStatus? Status { get; init; }

    public WorkPriority? Priority { get; init; }

    public string? Description { get; init; }

    public List<string>? Labels { get; init; }

    public string? Repository { get; init; }

    [Description("ref of a feature of this plan.")]
    public string? Feature { get; init; }

    [Description("refs of items of this plan that block this one.")]
    public List<string>? BlockedBy { get; init; }

    public int? Order { get; init; }

    [Description(WorkRequestText.Goal)]
    public string? Goal { get; init; }

    [Description(WorkRequestText.AcceptanceCriteria)]
    public List<AcceptanceCriterionInput>? AcceptanceCriteria { get; init; }

    [Description("Requirements (E2) of the item's plan feature that it serves.")]
    public List<string>? Inherits { get; init; }

    [Description(WorkRequestText.Touches)]
    public List<string>? Touches { get; init; }
}

public sealed class GetWorkRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId)]
    public string? SessionId { get; init; }

    [Description("Key (SW, SW-142, SW-F12) or id of a project, feature or item.")]
    public required string Id { get; init; }

    [Description("summary (default): an item carries its latest handoff and handoff_count, and its latest three attempts and attempt_count. full: its whole handoff and attempt history, newest first.")]
    public WorkDetail? Detail { get; init; }
}

public sealed class FindWorkRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId)]
    public string? SessionId { get; init; }

    [Description("item (default), feature or project.")]
    public WorkEntityKind? Kind { get; init; }

    [Description("Key or id of the Project. Required for items and features.")]
    public string? Project { get; init; }

    [Description("Items in any of these statuses. Default: every status except closed.")]
    public List<WorkStatus>? Status { get; init; }

    public WorkItemType? Type { get; init; }

    public WorkPriority? Priority { get; init; }

    [Description("Items of this repository.")]
    public string? Repository { get; init; }

    [Description("Key or id of a feature: its items.")]
    public string? Feature { get; init; }

    [Description("Items carrying this label (whole label, ignoring case).")]
    public string? Label { get; init; }

    [Description("true: only items next_work could hand out now (ready, every blocker complete, no live claim). false: only items that are not.")]
    public bool? Ready { get; init; }

    [Description("true: only items with a live claim. false: only items without one.")]
    public bool? Claimed { get; init; }

    [Description("Words that must all appear in the title or description (ignoring case).")]
    public string? Text { get; init; }

    [Description("summary (default): one line per row. full: the whole object per row.")]
    public WorkDetail? Detail { get; init; }

    [Description("Page size, 1 to 200 (default 50).")]
    public int? Limit { get; init; }

    [Description("next_cursor of the previous page, with the same filters.")]
    public string? Cursor { get; init; }
}

public sealed class UpdateWorkRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId)]
    public string? SessionId { get; init; }

    [Description("Key (SW, SW-142, SW-F12) or id of the project, feature or item to change.")]
    public required string Id { get; init; }

    [Description("The version of the object as last read. The write happens only if it still has that version; otherwise it is refused conflict with the current object.")]
    public required long ExpectedVersion { get; init; }

    [Description("Project: new name.")]
    public string? Name { get; init; }

    [Description("Feature or item: new title.")]
    public string? Title { get; init; }

    [Description("Project, feature or item: new description.")]
    public string? Description { get; init; }

    [Description("Project or feature: replaces the goals.")]
    public List<string>? Goals { get; init; }

    [Description("Feature: replaces the active requirements (E1..En), in order. Every active requirement left out must be named in drop_requirements.")]
    public List<RequirementInput>? Requirements { get; init; }

    [Description("Feature: active requirements to drop, each with the decision that drops it.")]
    public List<DropRequirementInput>? DropRequirements { get; init; }

    [Description("Feature: replaces the acceptance tests.")]
    public List<string>? AcceptanceTests { get; init; }

    [Description("Item: " + WorkRequestText.Goal)]
    public string? Goal { get; init; }

    [Description("Item: replaces the acceptance criteria. " + WorkRequestText.AcceptanceCriteria)]
    public List<AcceptanceCriterionInput>? AcceptanceCriteria { get; init; }

    [Description("Item: replaces " + WorkRequestText.Inherits)]
    public List<string>? Inherits { get; init; }

    [Description("Item: replaces " + WorkRequestText.Touches)]
    public List<string>? Touches { get; init; }

    [Description("Item: the acceptance report the item is marked complete with (only with status complete, or on a complete item): a summary and the state and evidence of named criteria. Optional; not validated.")]
    public AcceptanceReportInput? AcceptanceReport { get; init; }

    [Description("Project: replaces the member repositories. An owned repository that items still name cannot be removed.")]
    public List<RepositoryMemberInput>? Repositories { get; init; }

    [Description("Feature: documents to add.")]
    public List<DocumentInput>? AddDocuments { get; init; }

    [Description("Feature: documents to change.")]
    public List<DocumentPatchInput>? UpdateDocuments { get; init; }

    [Description("Item: new type.")]
    public WorkItemType? Type { get; init; }

    [Description("Item: new status. Every transition is allowed. Moving to complete or closed releases the claim.")]
    public WorkStatus? Status { get; init; }

    [Description("Item: new priority.")]
    public WorkPriority? Priority { get; init; }

    [Description("Item: replaces the labels.")]
    public List<string>? Labels { get; init; }

    [Description("Item: an owned repository of the project.")]
    public string? Repository { get; init; }

    [Description("Item: key or id of a feature of the project.")]
    public string? Feature { get; init; }

    [Description("Item: explicit order within its priority.")]
    public int? Order { get; init; }

    [Description("Item: keys or ids of items of the project that now block it. Refused cycle if that would make an item block itself.")]
    public List<string>? AddBlockedBy { get; init; }

    [Description("Item: keys or ids of blockers to remove.")]
    public List<string>? RemoveBlockedBy { get; init; }

    [Description("Item: links to attach (an existing kind and url is kept as it is).")]
    public List<LinkInput>? AddLinks { get; init; }

    [Description("Item: links to remove, by kind and url.")]
    public List<LinkInput>? RemoveLinks { get; init; }

    [Description("Item: a comment to add, authored by the caller.")]
    public string? Comment { get; init; }

    [Description("Item: claim it for the caller under marker. Refused already_claimed when another identity or marker holds a live claim.")]
    public ClaimInput? Claim { get; init; }

    [Description("Item: extend the caller's live claim under marker.")]
    public ClaimInput? RenewClaim { get; init; }

    [Description("Item: release the caller's claim under marker.")]
    public ReleaseClaimInput? ReleaseClaim { get; init; }

    [Description("Item: the state of the work as the caller leaves it (done, decisions, open issues, corrections, next action, branch, commit), stored on the item and shown to its next holder.")]
    public HandoffInput? Handoff { get; init; }

    [Description("Optional fields to empty: description (any kind); goals, acceptance_tests (feature); labels, repository, feature, goal, acceptance_criteria, inherits, touches (item).")]
    public List<string>? Clear { get; init; }

    // Save points (§5.8, S5): knowledge entries stored in the same transaction as the write, on any
    // kind; on an item they are linked to it and scoped to its repository unless they name others.

    [Description(SavePointText.Decisions)]
    public List<DecisionInput>? Decisions { get; init; }

    [Description(SavePointText.Attempts)]
    public List<AttemptInput>? Attempts { get; init; }

    [Description(SavePointText.Corrections)]
    public List<CorrectionInput>? Corrections { get; init; }

    [Description(SavePointText.Findings)]
    public List<FindingInput>? Findings { get; init; }
}

public sealed class DocumentPatchInput
{
    [Description("Id of a document of the feature.")]
    public required string Id { get; init; }

    public string? Title { get; init; }

    public string? Body { get; init; }
}

public sealed class LinkInput
{
    [Description("pr, branch or other.")]
    public required WorkLinkKind Kind { get; init; }

    [Description("Absolute http or https URL, at most 2048 characters, without credentials. Compared exactly.")]
    public required string Url { get; init; }
}

public sealed class ClaimInput
{
    [Description("Caller-chosen id of the claim, such as a run id, at most 200 characters, compared exactly. Required without session_id; left out with session_id (the session is the marker).")]
    public string? Marker { get; init; }

    [Description("Seconds until the claim lapses unless renewed, 1 to 86400 (default 3600). Not with session_id: a session's claim lasts while the session is live.")]
    public int? TtlSeconds { get; init; }
}

public sealed class ReleaseClaimInput
{
    [Description("The marker of the claim. Required without session_id; left out with session_id.")]
    public string? Marker { get; init; }
}

public sealed class NextWorkRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId)]
    public string? SessionId { get; init; }

    [Description("Key or id of the Project.")]
    public required string Project { get; init; }

    [Description("Caller-chosen id of the claim, such as a run id, at most 200 characters, compared exactly. The claim belongs to the caller's identity and this marker. Required without session_id; left out with session_id (the claim is bound to the session).")]
    public string? Marker { get; init; }

    [Description("Seconds until the claim lapses unless renewed, 1 to 86400 (default 3600). Not with session_id: a session's claim lasts while the session is live.")]
    public int? TtlSeconds { get; init; }

    [Description("false (default): when the caller already holds a live claim (under marker, or of its session) in this project, return it instead of claiming another. true: claim an additional item.")]
    public bool? Another { get; init; }

    [Description("Caller-chosen key: a repeat by the same identity in this project returns the same allocation, even after the first answer was lost.")]
    public string? IdempotencyKey { get; init; }

    [Description("Key or id of a Feature of the project: only its items, so a session works through one feature.")]
    public string? Feature { get; init; }

    [Description("Only items of this repository.")]
    public string? Repository { get; init; }

    [Description("Only items of this type.")]
    public WorkItemType? Type { get; init; }

    [Description("Only items carrying this label.")]
    public string? Label { get; init; }
}

public sealed class ChangesRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId)]
    public string? SessionId { get; init; }

    [Description("Key or id of a Project: only its entries. One cursor works with any project or none.")]
    public string? Project { get; init; }

    [Description("next_cursor of an earlier call, passed back unchanged. Without it the answer has no entries and the cursor of the newest entry.")]
    public string? Cursor { get; init; }

    [Description("true: read from the oldest entry. Cannot be combined with cursor.")]
    public bool? FromStart { get; init; }

    [Description("Most entries to return, 1 to 200 (default 50).")]
    public int? Limit { get; init; }

    [Description("Wait up to this many seconds (at most 55) for an entry after cursor before answering with none.")]
    public int? WaitSeconds { get; init; }
}

internal static class WorkRequestText
{
    public const string SessionId =
        "Id of the caller's session (from start_session). Renews the session's heartbeat, attributes the call to it, and binds claims to it.";

    public const string Goal = "What the item is for, at most 2000 characters.";

    public const string AcceptanceCriteria = "At most 10 acceptance criteria (plan 1 to 10; an item may have none, for backward compatibility), each with text and optionally id, state (unmet or met) and evidence.";

    public const string Inherits = "the requirements (E2) of the item's feature that the item serves; a feature is complete only when every active requirement is inherited by a complete item.";

    public const string Touches = "the paths and symbols the item is expected to touch, at most 50 of up to 512 characters.";
}
