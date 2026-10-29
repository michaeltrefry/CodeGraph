using System.ComponentModel;

namespace CodeGraph.Models.Work;

// The project knowledge store (sc-27869, design §5.3, §5.8, §10.3): goals, decisions, constraints,
// facts, gotchas, attempts, architecture notes and lessons, scoped to a Project and optionally to
// repositories, paths and graph nodes. Written synchronously by record and by the save-point fields
// of update_work and end_session; read by recall, the briefing and the get_work brief. Read and
// written with WorkJson.Options like every work tool (strict, snake_case).

public enum KnowledgeKind
{
    Goal,
    Decision,
    Constraint,
    Fact,
    Gotcha,
    Attempt,
    Architecture,
    Lesson
}

/// <summary>The kinds a <c>findings</c> save point may record.</summary>
public enum FindingKind
{
    Fact,
    Gotcha,
    Constraint
}

public enum KnowledgeConfidence
{
    High,
    Medium,
    Low
}

public enum KnowledgeStatus
{
    Active,
    Superseded,
    Retracted
}

/// <summary>The activity dimension of a lesson's applies-when (§5.7).</summary>
public enum LessonActivity
{
    Planning,
    Slicing,
    Implementing,
    Testing,
    Reviewing,
    Merging,
    Releasing,
    TrackerUpdates,
    Coordinating
}

/// <summary>Where a correction came from (§5.7). Priority: prompt highest, review high, the rest normal.</summary>
public enum CorrectionSourceKind
{
    Prompt,
    Review,
    BotReview,
    Subagent,
    Agent
}

/// <summary>The importing agent's verification of a native-memory candidate (§10.3).</summary>
public enum ImportVerification
{
    Verified,
    Stale,
    Unverifiable,
    Personal
}

public static class KnowledgeLimits
{
    public const int MaxSummaryLength = 500;
    public const int MaxBodyLength = 16_000;
    public const int MaxFieldLength = 4_000;
    public const int MaxAlternatives = 20;
    public const int MaxRepositories = 20;
    public const int MaxPaths = 50;
    public const int MaxPathLength = 500;
    public const int MaxNodes = 50;
    public const int MaxToolNameLength = 128;
    public const int MaxSaveEntries = 20;
    public const int MaxReasonLength = 1_000;
    public const int MaxSourceRefLength = 500;
    public const int MaxSourceFileLength = 1_024;
    public const int DefaultRecallLimit = 20;
    public const int MaxRecallLimit = 50;
    /// <summary>A recall with detail full answers at most this many entries per page.</summary>
    public const int MaxFullRecallLimit = 10;
    public const int MaxRecallWords = 10;
    public const int MaxImportCandidates = 200;
    /// <summary>Entries in the briefing's knowledge section and in an item brief.</summary>
    public const int MaxBriefingEntries = 20;
    /// <summary>A summary in a briefing is cut to this many characters.</summary>
    public const int BriefingSummaryLength = 200;
}

// ---- Shared input shapes --------------------------------------------------------------------

public sealed class AppliesWhenInput
{
    [Description("Activities the lesson applies to: planning, slicing, implementing, testing, reviewing, merging, releasing, tracker_updates, coordinating.")]
    public List<LessonActivity>? Activity { get; init; }

    [Description("Repositories it applies in (members of the project). Becomes the entry's repository scope.")]
    public List<string>? Repositories { get; init; }

    [Description("Paths or globs it applies to. Becomes the entry's path scope.")]
    public List<string>? Paths { get; init; }

    [Description("Tools it applies to (such as update_work, gh).")]
    public List<string>? Tools { get; init; }

    [Description("Harness it applies to (such as claude-code).")]
    public string? Harness { get; init; }
}

public sealed class CorrectionSourceInput
{
    [Description("prompt (the human's prompt; highest priority), review (a human code review), bot_review (an automated review), subagent, agent.")]
    public required CorrectionSourceKind Kind { get; init; }

    [Description("The source event, such as a review comment id or URL, a message id or a prompt/session id. Repeats of one event are the same correction.")]
    public string? Ref { get; init; }
}

// ---- Save points (§5.8): fields of update_work and end_session --------------------------------

public sealed class DecisionInput
{
    [Description("The decision, one line, at most 500 characters.")]
    public required string Decision { get; init; }

    [Description("Why it was made.")]
    public string? Why { get; init; }

    [Description("Alternatives considered, one per entry.")]
    public List<string>? Alternatives { get; init; }

    [Description("The context of the decision.")]
    public string? Context { get; init; }

    [Description("Its consequences.")]
    public string? Consequences { get; init; }

    [Description("high, medium (default) or low.")]
    public KnowledgeConfidence? Confidence { get; init; }

    [Description("true: the agent decided without a human (shown in the review queue).")]
    public bool? MadeAutonomously { get; init; }

    [Description(KnowledgeText.Repositories)]
    public List<string>? Repositories { get; init; }

    [Description(KnowledgeText.Paths)]
    public List<string>? Paths { get; init; }

    [Description(KnowledgeText.Nodes)]
    public List<long>? Nodes { get; init; }

    [Description(KnowledgeText.Commit)]
    public string? Commit { get; init; }
}

public sealed class AttemptInput
{
    [Description("What was tried, one line, at most 500 characters.")]
    public required string Tried { get; init; }

    [Description("Why it failed or was abandoned.")]
    public required string WhyFailed { get; init; }

    public KnowledgeConfidence? Confidence { get; init; }

    [Description(KnowledgeText.Repositories)]
    public List<string>? Repositories { get; init; }

    [Description(KnowledgeText.Paths)]
    public List<string>? Paths { get; init; }

    [Description(KnowledgeText.Nodes)]
    public List<long>? Nodes { get; init; }

    [Description(KnowledgeText.Commit)]
    public string? Commit { get; init; }
}

public sealed class CorrectionInput
{
    [Description("What happened (what was done that had to be corrected).")]
    public required string What { get; init; }

    [Description("The correct behavior, one line, at most 500 characters: the lesson.")]
    public required string CorrectBehavior { get; init; }

    [Description("When the lesson applies: activities and optionally repositories, paths, tools, harness.")]
    public AppliesWhenInput? AppliesWhen { get; init; }

    [Description("Where the correction came from.")]
    public required CorrectionSourceInput Source { get; init; }

    public KnowledgeConfidence? Confidence { get; init; }

    [Description(LessonText.SameAs)]
    public string? SameAs { get; init; }

    [Description(LessonText.Contradicts)]
    public string? Contradicts { get; init; }
}

public sealed class FindingInput
{
    [Description("fact, gotcha or constraint.")]
    public required FindingKind Kind { get; init; }

    [Description("The finding, one line, at most 500 characters.")]
    public required string Summary { get; init; }

    [Description("More detail, at most 16000 characters.")]
    public string? Body { get; init; }

    public KnowledgeConfidence? Confidence { get; init; }

    [Description(KnowledgeText.Repositories)]
    public List<string>? Repositories { get; init; }

    [Description(KnowledgeText.Paths)]
    public List<string>? Paths { get; init; }

    [Description(KnowledgeText.Nodes)]
    public List<long>? Nodes { get; init; }

    [Description(KnowledgeText.Commit)]
    public string? Commit { get; init; }
}

// ---- record ------------------------------------------------------------------------------------

public sealed class RecordRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId)]
    public string? SessionId { get; init; }

    [Description("Key or id of the Project. May be left out with session_id (the session's project), and with revise, retract, verify.")]
    public string? Project { get; init; }

    [Description("A new entry. Pass exactly one of entry, revise, retract, verify, relate, acknowledge.")]
    public KnowledgeEntryInput? Entry { get; init; }

    [Description("A new revision of an active entry: the fields passed replace the stored ones; the entry is re-verified against its files.")]
    public KnowledgeRevisionInput? Revise { get; init; }

    [Description("Retract an active entry (it was wrong). It stays readable with status retracted.")]
    public KnowledgeRetractInput? Retract { get; init; }

    [Description("Re-verify an active entry: its files' current indexed content becomes the baseline, clearing possibly_stale.")]
    public KnowledgeVerifyInput? Verify { get; init; }

    [Description("Lessons: decide about a stored lesson after seeing its similar lessons: same_as (merge it into that lesson) or contradicts (keep both, linked and flagged).")]
    public KnowledgeRelateInput? Relate { get; init; }

    [Description("Review queue: an autonomous decision was reviewed and stands; it leaves the review queue.")]
    public KnowledgeAcknowledgeInput? Acknowledge { get; init; }

    [Description("With entry: caller-chosen key, 1 to 128 printable ASCII characters. A repeat by the same identity in the project stores nothing and answers the first entry, with created false.")]
    public string? IdempotencyKey { get; init; }
}

public sealed class KnowledgeEntryInput
{
    [Description("goal, decision, constraint, fact, gotcha, attempt, architecture or lesson.")]
    public required KnowledgeKind Kind { get; init; }

    [Description("One line, at most 500 characters. For a decision the decision, an attempt what was tried, a lesson the correct behavior.")]
    public required string Summary { get; init; }

    [Description("More detail, at most 16000 characters.")]
    public string? Body { get; init; }

    [Description("high, medium (default) or low.")]
    public KnowledgeConfidence? Confidence { get; init; }

    [Description(KnowledgeText.Repositories)]
    public List<string>? Repositories { get; init; }

    [Description(KnowledgeText.Paths)]
    public List<string>? Paths { get; init; }

    [Description(KnowledgeText.Nodes)]
    public List<long>? Nodes { get; init; }

    [Description(KnowledgeText.Commit)]
    public string? Commit { get; init; }

    [Description("Key or id of an item of the project the entry is about.")]
    public string? Item { get; init; }

    [Description("Decision: why it was made.")]
    public string? Why { get; init; }

    [Description("Decision: alternatives considered.")]
    public List<string>? Alternatives { get; init; }

    [Description("Decision: its context.")]
    public string? Context { get; init; }

    [Description("Decision: its consequences.")]
    public string? Consequences { get; init; }

    [Description("Decision: true when the agent decided without a human.")]
    public bool? MadeAutonomously { get; init; }

    [Description("Attempt (required for one): why it failed.")]
    public string? WhyFailed { get; init; }

    [Description("Lesson: what happened.")]
    public string? What { get; init; }

    [Description("Lesson: when it applies (activities, and optionally repositories, paths, tools, harness).")]
    public AppliesWhenInput? AppliesWhen { get; init; }

    [Description("Lesson: where the correction came from.")]
    public CorrectionSourceInput? Source { get; init; }

    [Description("Lesson: " + LessonText.SameAs)]
    public string? SameAs { get; init; }

    [Description("Lesson: " + LessonText.Contradicts)]
    public string? Contradicts { get; init; }

    [Description("An active entry of the project this one replaces: it becomes superseded, linked to this one.")]
    public SupersedeInput? Supersedes { get; init; }
}

public sealed class SupersedeInput
{
    [Description("Id of the entry replaced.")]
    public required string Id { get; init; }

    [Description("Its version as last read.")]
    public required long ExpectedVersion { get; init; }
}

public sealed class KnowledgeRevisionInput
{
    [Description("Id of the entry.")]
    public required string Id { get; init; }

    [Description("Its version as last read; refused conflict with the current entry otherwise.")]
    public required long ExpectedVersion { get; init; }

    public string? Summary { get; init; }

    public string? Body { get; init; }

    public KnowledgeConfidence? Confidence { get; init; }

    [Description("Replaces the repositories.")]
    public List<string>? Repositories { get; init; }

    [Description("Replaces the paths.")]
    public List<string>? Paths { get; init; }

    [Description("Replaces the graph nodes.")]
    public List<long>? Nodes { get; init; }

    [Description(KnowledgeText.Commit)]
    public string? Commit { get; init; }

    public string? Why { get; init; }

    public List<string>? Alternatives { get; init; }

    public string? Context { get; init; }

    public string? Consequences { get; init; }

    public bool? MadeAutonomously { get; init; }

    public string? WhyFailed { get; init; }

    public string? What { get; init; }

    public AppliesWhenInput? AppliesWhen { get; init; }

    public CorrectionSourceInput? Source { get; init; }

    [Description("Optional fields to empty: body, why, alternatives, context, consequences, what, applies_when, repositories, paths, nodes.")]
    public List<string>? Clear { get; init; }
}

public sealed class KnowledgeRetractInput
{
    public required string Id { get; init; }

    public required long ExpectedVersion { get; init; }

    [Description("Why the entry is retracted, at most 1000 characters.")]
    public required string Reason { get; init; }
}

public sealed class KnowledgeVerifyInput
{
    public required string Id { get; init; }

    public required long ExpectedVersion { get; init; }

    [Description(KnowledgeText.Commit)]
    public string? Commit { get; init; }
}

// ---- recall --------------------------------------------------------------------------------------

public sealed class RecallRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId)]
    public string? SessionId { get; init; }

    [Description("Key or id of the Project. May be left out with session_id or with id.")]
    public string? Project { get; init; }

    [Description("Id of one entry: answered in full with its revisions (detail full) or its fields. Takes no filters.")]
    public string? Id { get; init; }

    [Description("Only these kinds.")]
    public List<KnowledgeKind>? Kind { get; init; }

    [Description("Only entries scoped to this repository.")]
    public string? Repository { get; init; }

    [Description("Only entries whose paths or globs cover any of these paths (or lie under one of them).")]
    public List<string>? Paths { get; init; }

    [Description("Only entries about these graph nodes, by node or by the node's file.")]
    public List<long>? Nodes { get; init; }

    [Description("Only entries about this item (key or id).")]
    public string? Item { get; init; }

    [Description("Only lessons applying to this activity.")]
    public LessonActivity? Activity { get; init; }

    [Description("Only lessons from this correction source.")]
    public CorrectionSourceKind? Source { get; init; }

    [Description("Only lessons in these rule states: candidate (task-scoped), proposed, rule, rejected.")]
    public List<LessonRuleState>? RuleState { get; init; }

    [Description("Words that must all appear in the summary or body (ignoring case).")]
    public string? Text { get; init; }

    [Description("Only entries in these statuses (default active).")]
    public List<KnowledgeStatus>? Status { get; init; }

    [Description("true: only entries flagged possibly_stale. false: only entries that are not.")]
    public bool? PossiblyStale { get; init; }

    [Description("summary (default): one line per entry. full: the whole entry (at most 10 per page).")]
    public WorkDetail? Detail { get; init; }

    [Description("Page size, 1 to 50 (default 20); with detail full 1 to 10 (default 10).")]
    public int? Limit { get; init; }

    [Description("next_cursor of the previous page, with the same filters.")]
    public string? Cursor { get; init; }
}

// ---- import_knowledge ------------------------------------------------------------------------------

public sealed class ImportKnowledgeRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId)]
    public string? SessionId { get; init; }

    [Description("Key or id of the Project the candidates are imported into. May be left out with session_id.")]
    public string? Project { get; init; }

    [Description("The candidate entries, 1 to 200, each with its source file and the importing agent's verification.")]
    public required List<ImportCandidateInput> Candidates { get; init; }

    [Description("false (default): a dry run that stores nothing. true: store the accepted candidates (needs accept and preview_token).")]
    public bool? Confirm { get; init; }

    [Description("With confirm: the indexes of the candidates to store. Each must have previewed as would_store.")]
    public List<int>? Accept { get; init; }

    [Description("With confirm: the preview_token of the dry run of this same batch.")]
    public string? PreviewToken { get; init; }

    [Description("With confirm (required): caller-chosen key, 1 to 128 printable ASCII characters. A repeat by the same identity in the project stores nothing and answers the first confirm's result.")]
    public string? IdempotencyKey { get; init; }
}

public sealed class ImportCandidateInput
{
    [Description("The entry as it would be recorded. Takes no item and no supersedes.")]
    public required KnowledgeEntryInput Entry { get; init; }

    [Description("The memory file the candidate came from.")]
    public required string SourceFile { get; init; }

    [Description("verified, stale, unverifiable or personal: the importing agent's check against the repository. Only verified and unverifiable candidates can be stored.")]
    public required ImportVerification Verification { get; init; }

    [Description("What the verification found (what no longer matches, why it cannot be verified).")]
    public string? VerificationNote { get; init; }
}

internal static class SavePointText
{
    public const string Decisions =
        "Save point: decisions made, each stored as a decision entry of the project's knowledge (shown by recall, briefings and get_work) in the same " +
        "transaction as this write: {decision, why, alternatives, context, consequences, confidence, made_autonomously, repositories, paths, nodes, commit}. At most 20.";
    public const string Attempts = "Save point: approaches tried and abandoned, each stored as an attempt entry: {tried, why_failed, confidence, repositories, paths, nodes, commit}. At most 20.";
    public const string Corrections = "Save point: corrections received (from a human, a reviewer or another agent), each stored as a lesson entry: {what, correct_behavior, applies_when, source}. At most 20.";
    public const string Findings = "Save point: facts, gotchas and constraints the next agent would otherwise rediscover, each stored as an entry of that kind: {kind, summary, body, confidence, repositories, paths, nodes, commit}. At most 20.";
    public const string BriefingPaths = "Paths the session works on, relative to the repository root: the briefing's knowledge section adds the decisions and rules scoped to them.";
    public const string BriefingActivity =
        "What the session is doing: planning, slicing, implementing, testing, reviewing, merging, releasing, tracker_updates, coordinating. The briefing's rules section serves " +
        "the rules whose applies-when names one of these activities (and whose location, if any, matches). Kept on the session until a get_briefing passes another.";
}

internal static class LessonText
{
    public const string SameAs = "Id of an existing lesson this correction says the same as (from similar or a briefing's rules): it is merged into that lesson as a recurrence with its evidence; no new lesson is stored.";
    public const string Contradicts = "Id of an existing lesson this one contradicts: both are kept, linked, served together with the conflict shown, and flagged in the review queue.";
}

internal static class KnowledgeText
{
    public const string Repositories = "Repositories the entry is scoped to, members of the project. Left out: on an item, the item's repository; otherwise the whole project.";
    public const string Paths = "Paths or globs (*, **, ?) relative to the repository root. The indexed files among them are what the entry is verified against: when one changes, the entry reads possibly_stale.";
    public const string Nodes = "Graph node ids the entry is about. Their files join what the entry is verified against.";
    public const string Commit = "The commit the entry was verified at (default: the repository's indexed commit).";
}

// ---- Views -------------------------------------------------------------------------------------------

/// <summary>Kind-specific fields of an entry.</summary>
public sealed record KnowledgeDetails(
    string? Why = null,
    IReadOnlyList<string>? Alternatives = null,
    string? Context = null,
    string? Consequences = null,
    string? WhyFailed = null,
    string? What = null,
    AppliesWhenView? AppliesWhen = null,
    CorrectionSourceView? Source = null);

public sealed record AppliesWhenView(
    IReadOnlyList<LessonActivity>? Activity = null,
    IReadOnlyList<string>? Repositories = null,
    IReadOnlyList<string>? Paths = null,
    IReadOnlyList<string>? Tools = null,
    string? Harness = null);

/// <param name="Priority">highest (prompt), high (review) or normal.</param>
public sealed record CorrectionSourceView(CorrectionSourceKind Kind, string Priority, string? Ref = null);

public sealed record KnowledgeNodeView(long Id, string Repository, string QualifiedName, string? FilePath = null);

public sealed record KnowledgeFileView(string Repository, string Path);

public sealed record KnowledgeRevisionView(int Revision, string Action, WorkActor Author, DateTime CreatedAt);

/// <summary>
/// One line of knowledge (E4). <see cref="Status"/>, <see cref="PossiblyStale"/> and
/// <see cref="MadeAutonomously"/> appear only when they say something (not active, stale, autonomous).
/// <see cref="Match"/> says why a briefing or brief chose it: item, path, repository or project.
/// </summary>
public sealed record KnowledgeRow(
    Guid Id,
    KnowledgeKind Kind,
    string Summary,
    KnowledgeConfidence Confidence,
    long Version,
    KnowledgeStatus? Status = null,
    bool? PossiblyStale = null,
    bool? MadeAutonomously = null,
    string? Item = null,
    string? Match = null,
    bool? Rule = null,
    int? Recurrence = null,
    bool? Merged = null);

public sealed record KnowledgeView(
    Guid Id,
    Guid ProjectId,
    string ProjectKey,
    KnowledgeKind Kind,
    KnowledgeStatus Status,
    string Summary,
    KnowledgeConfidence Confidence,
    bool PossiblyStale,
    int Revision,
    long Version,
    WorkActor Author,
    DateTime VerifiedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? Body = null,
    KnowledgeDetails? Details = null,
    bool? MadeAutonomously = null,
    IReadOnlyList<string>? Repositories = null,
    IReadOnlyList<string>? Paths = null,
    IReadOnlyList<KnowledgeNodeView>? Nodes = null,
    EntityRef? Item = null,
    EntityRef? Feature = null,
    Guid? Supersedes = null,
    Guid? SupersededBy = null,
    string? StatusReason = null,
    string? VerifiedCommit = null,
    IReadOnlyList<KnowledgeFileView>? VerifiedFiles = null,
    IReadOnlyList<KnowledgeFileView>? StaleFiles = null,
    string? SourceFile = null,
    IReadOnlyList<KnowledgeRevisionView>? Revisions = null,
    LessonView? Lesson = null)
{
    public string Entity => "knowledge";
}

/// <param name="Merged">true when the lesson was merged into an existing one (same_as, or an exact normalized match): Entry is that lesson.</param>
/// <param name="Similar">Lessons similar to the one recorded: candidates for same_as or contradicts (record relate).</param>
public sealed record RecordResult(bool Created, KnowledgeView Entry, KnowledgeRow? Superseded = null, bool? Merged = null, IReadOnlyList<SimilarLessons>? Similar = null);

public sealed record RecallResult(IReadOnlyList<object> Items, string? NextCursor = null);

/// <summary>Outcomes of a candidate in an import dry run (and on confirm, <c>stored</c>).</summary>
public static class ImportOutcomes
{
    public const string WouldStore = "would_store";
    public const string Stored = "stored";
    public const string Duplicate = "duplicate";
    public const string OutOfScope = "out_of_scope";
    public const string Personal = "personal";
    public const string Stale = "stale";
    public const string Invalid = "invalid";
}

public sealed record ImportCandidateResult(
    int Index,
    string Outcome,
    string? Message = null,
    KnowledgeRow? DuplicateOf = null,
    int? DuplicateOfCandidate = null,
    KnowledgeRow? Entry = null);

public sealed record ImportKnowledgeResult(bool Stored, string PreviewToken, IReadOnlyList<ImportCandidateResult> Candidates);
