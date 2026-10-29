using System.ComponentModel;

namespace CodeGraph.Models.Work;

// Sessions, handoffs and briefings (sc-27866). A session is one running agent; its id, passed as
// session_id to any work tool, is its heartbeat and the attribution of what it writes.

/// <summary>Liveness and briefing settings, bound from configuration section <see cref="Section"/>.</summary>
public sealed class WorkSessionOptions
{
    public const string Section = "Work:Sessions";

    /// <summary>A session silent for longer than this is presumed dead and its claims lapse (default 15 minutes).</summary>
    public int IdleSeconds { get; set; } = 900;

    /// <summary>The token budget of a briefing when the call names none.</summary>
    public int DefaultBriefingTokens { get; set; } = 1500;

    /// <summary>
    /// The PruneWorkSessions job deletes a session that ended, or was last seen, longer ago than this
    /// many days (default 30), unless something durable still refers to it (sc-27882).
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Sessions deleted per transaction by the prune job.</summary>
    public int PruneBatchSize { get; set; } = 500;
}

/// <summary>A tool argument type that can carry the caller's session.</summary>
public interface ISessionArguments
{
    string? SessionId { get; }
}

public static class WorkSessionLimits
{
    public const int MaxSessionIdLength = 64;
    public const int MaxHarnessLength = 64;
    public const int MaxModelLength = 128;
    public const int MaxHostLength = 255;
    public const int MaxBranchLength = 255;
    public const int MaxWorktreeLength = 1024;
    public const int MaxIntentLength = 1000;
    public const int MinBriefingTokens = 200;
    public const int MaxBriefingTokens = 20_000;
    public const int MaxHandoffText = 8_000;
    public const int MaxHandoffEntries = 30;
    public const int MaxCommitLength = 64;
    /// <summary>Text of one handoff field in a briefing is cut to this many characters.</summary>
    public const int BriefingTextLength = 400;
}

public sealed class StartSessionRequest
{
    [Description("Key (e.g. SW) or id of the Project the session works in. May be left out when repository is an owned repository of exactly one project.")]
    public string? Project { get; init; }

    [Description("Name of the harness running the agent, such as claude-code or codex. 1 to 64 characters.")]
    public required string Harness { get; init; }

    [Description("Model the agent runs on.")]
    public string? Model { get; init; }

    [Description("Host name of the machine the agent runs on.")]
    public string? Host { get; init; }

    [Description("Repository the session works in, as CodeGraph indexes it.")]
    public string? Repository { get; init; }

    [Description("Git branch the session works on.")]
    public string? Branch { get; init; }

    [Description("Path of the worktree the session works in.")]
    public string? Worktree { get; init; }

    [Description("Free text: what the session means to do, at most 1000 characters.")]
    public string? Intent { get; init; }

    [Description("Token budget of the briefing returned, 200 to 20000 (default 1500).")]
    public int? BudgetTokens { get; init; }

    [Description(SavePointText.BriefingPaths)]
    public List<string>? Paths { get; init; }

    [Description("Caller-chosen key: a repeat by the same identity answers the session the first call started, with created false.")]
    public string? IdempotencyKey { get; init; }
}

public sealed class EndSessionRequest : ISessionArguments
{
    [Description("Id of the session to end (from start_session).")]
    public required string SessionId { get; init; }

    [Description("Key or id of the item the handoff is about. May be left out when the session holds exactly one item.")]
    public string? Item { get; init; }

    [Description("State of the item as the session leaves it; stored on the item and shown to the next session that picks it up.")]
    public HandoffInput? Handoff { get; init; }

    // Save points (§5.8, S5): stored in the same transaction as the end, linked to item (or the one
    // item the session holds), scoped like the save points of update_work.

    [Description(SavePointText.Decisions)]
    public List<DecisionInput>? Decisions { get; init; }

    [Description(SavePointText.Attempts)]
    public List<AttemptInput>? Attempts { get; init; }

    [Description(SavePointText.Corrections)]
    public List<CorrectionInput>? Corrections { get; init; }

    [Description(SavePointText.Findings)]
    public List<FindingInput>? Findings { get; init; }
}

public sealed class GetBriefingRequest : ISessionArguments
{
    [Description("Id of the session (from start_session). Renews its heartbeat.")]
    public required string SessionId { get; init; }

    [Description("Token budget, 200 to 20000 (default 1500).")]
    public int? BudgetTokens { get; init; }

    [Description("Key or id of an item of the project to include in the briefing (focus), with its latest handoff.")]
    public string? Item { get; init; }

    [Description(SavePointText.BriefingPaths)]
    public List<string>? Paths { get; init; }
}

public sealed class HandoffInput
{
    [Description("What was done.")]
    public string? Done { get; init; }

    [Description("Decisions made, one per entry.")]
    public List<string>? Decisions { get; init; }

    [Description("Open issues, one per entry.")]
    public List<string>? OpenIssues { get; init; }

    [Description("Corrections received, one per entry.")]
    public List<string>? Corrections { get; init; }

    [Description("The next action for whoever continues.")]
    public string? NextAction { get; init; }

    [Description("Branch the work is on.")]
    public string? Branch { get; init; }

    [Description("Last commit of the work.")]
    public string? Commit { get; init; }
}

public sealed record SessionView(
    string Id,
    Guid ProjectId,
    string ProjectKey,
    string Identity,
    string Harness,
    string? Model,
    string? Host,
    string? Repository,
    string? Branch,
    string? Worktree,
    string? Intent,
    bool Live,
    DateTime StartedAt,
    DateTime LastSeenAt,
    DateTime? EndedAt = null)
{
    public string Kind => "session";
}

public sealed record HandoffView(
    Guid Id,
    string Author,
    DateTime CreatedAt,
    string? SessionId = null,
    string? Harness = null,
    string? Done = null,
    IReadOnlyList<string>? Decisions = null,
    IReadOnlyList<string>? OpenIssues = null,
    IReadOnlyList<string>? Corrections = null,
    string? NextAction = null,
    string? Branch = null,
    string? Commit = null);

/// <summary>Who a write or a claim belongs to, in one line of a briefing or a refusal.</summary>
public sealed record HolderView(string Identity, string? SessionId = null, string? Harness = null, string? Model = null, string? Host = null);

public sealed record StartSessionResult(bool Created, SessionView Session, BriefingView Briefing);

public sealed record EndSessionResult(
    SessionView Session,
    IReadOnlyList<WorkRow> Released,
    HandoffView? Handoff = null,
    IReadOnlyList<HoldView>? ReleasedHolds = null,
    IReadOnlyList<KnowledgeRow>? Recorded = null);

/// <summary>
/// What the next holder of an item needs to continue it (§5.9): the previous holder, the branch and
/// PR links, the latest handoff, or when the previous session stopped without one its last change
/// to the item, and the acceptance criteria not yet reported met.
/// </summary>
public sealed record RecoveryView(
    HolderView? PreviousHolder,
    IReadOnlyList<string>? Branches = null,
    IReadOnlyList<string>? Prs = null,
    HandoffView? LatestHandoff = null,
    ChangeView? LastActivity = null,
    IReadOnlyList<AcceptanceCriterionView>? UnmetAcceptance = null);

// ---- Briefing ---------------------------------------------------------------------------------

/// <summary>
/// The briefing: data about the session's project, never instructions. Sections are listed in rank
/// order and left out when empty; <see cref="Truncated"/> names sections the budget shortened.
/// Later stories add sections (decisions and rules, inbox, holds) beside these.
/// </summary>
public sealed record BriefingView(
    string SessionId,
    int BudgetTokens,
    int EstimatedTokens,
    BriefingProject Project,
    IReadOnlyList<BriefingItem>? MyWork = null,
    BriefingItem? Focus = null,
    IReadOnlyList<BriefingSession>? OtherSessions = null,
    BriefingItem? SuggestedNext = null,
    BriefingInbox? Inbox = null,
    IReadOnlyList<string>? Truncated = null,
    IReadOnlyList<HoldView>? MyHolds = null,
    IReadOnlyList<ResourceView>? Resources = null,
    IReadOnlyList<OverlapWarning>? OverlapWarnings = null,
    IReadOnlyList<KnowledgeRow>? Knowledge = null);

public sealed record BriefingProject(string Key, string Name, IReadOnlyList<string>? Goals = null);

public sealed record BriefingItem(
    string Key,
    string Title,
    WorkStatus Status,
    WorkPriority? Priority = null,
    string? Repository = null,
    bool? Recoverable = null,
    IReadOnlyList<string>? Branches = null,
    IReadOnlyList<string>? Prs = null,
    HandoffView? LatestHandoff = null,
    HolderView? PreviousHolder = null,
    IReadOnlyList<AcceptanceCriterionView>? UnmetAcceptance = null);

public sealed record BriefingSession(
    string SessionId,
    string Identity,
    string Harness,
    DateTime LastSeenAt,
    string? Model = null,
    string? Host = null,
    string? Repository = null,
    string? Branch = null,
    string? Intent = null,
    IReadOnlyList<BriefingHold>? Holds = null);

public sealed record BriefingHold(string Key, string Title, WorkStatus Status);
