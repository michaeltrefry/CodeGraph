using System.ComponentModel;
using System.Text;

namespace CodeGraph.Models.Work;

// Lessons, rules and challenges (sc-27873, design §5.7, §6, R22-R28): the learning-loop state of
// lesson entries. A lesson from the human's prompt is a rule at once; other lessons become rules by
// recurrence (distinct source events) or by promote approve. Rules are served in briefings when
// their applies-when matches the session's activity and location, with contradictions and open
// challenges shown beside them. Nothing here is decided by a model (E5) and nothing blocks work (E6).

/// <summary>Where a lesson is on the tiers of §5.7.</summary>
public enum LessonRuleState
{
    /// <summary>Task-scoped: lives on its item, not served as a rule.</summary>
    Candidate,
    /// <summary>Proposed for rule status (promote propose); in the review queue.</summary>
    Proposed,
    /// <summary>A rule: served in briefings when its applies-when matches.</summary>
    Rule,
    /// <summary>Rejected for rule status with a reason: not re-proposed and not promoted by recurrence.</summary>
    Rejected
}

/// <summary>How a lesson became a rule.</summary>
public enum RuleReason
{
    /// <summary>From the human's prompt: a rule at once (R24).</summary>
    HumanPrompt,
    /// <summary>Its recurrence reached the threshold.</summary>
    Recurrence,
    /// <summary>promote approve.</summary>
    Approved
}

public enum PromoteAction
{
    Propose,
    Approve,
    Reject,
    AddressedBy
}

public enum AddressedByKind
{
    Skill,
    Instruction,
    Hook,
    Test,
    Ci
}

public enum ChallengeStatus
{
    Open,
    Upheld,
    Amended,
    Retracted
}

public enum ChallengeOutcome
{
    Uphold,
    Amend,
    Retract
}

public enum EvidenceKind
{
    TestResult,
    Source,
    CounterExample,
    Link,
    Other
}

public enum LessonsView
{
    /// <summary>Rules, newest first (prompt rules are the "recently learned" list).</summary>
    RecentlyLearned,
    /// <summary>Lessons that recurred, with recurrence while their rule was served and while it was not.</summary>
    Recurrence,
    /// <summary>Rules not addressed_by a local skill, hook or test, grouped by activity.</summary>
    Unaddressed,
    /// <summary>Rules not served to any session for since_days (or never).</summary>
    Unmatched,
    /// <summary>GitHub review evidence (reviews and review comments) no lesson was recorded from.</summary>
    UnlearnedReviews
}

/// <summary>Settings of the learning loop, bound from configuration section <see cref="Section"/>.</summary>
public sealed class WorkLessonOptions
{
    public const string Section = "Work:Lessons";

    /// <summary>Distinct correction events after which a lesson with an applies-when activity becomes a rule (default 2).</summary>
    public int RuleRecurrenceThreshold { get; set; } = 2;

    /// <summary>Cosine similarity at or above which a lesson is listed as similar when an embedding provider is available (default 0.8).</summary>
    public double EmbeddingCandidateThreshold { get; set; } = 0.8;
}

public static class LessonLimits
{
    public const int MaxEvidence = 10;
    public const int MaxEvidenceSummaryLength = 500;
    public const int MaxNameLength = 255;
    /// <summary>Rules in a briefing.</summary>
    public const int MaxBriefingRules = 10;
    /// <summary>Similar lessons listed for one recorded lesson.</summary>
    public const int MaxSimilar = 5;
    /// <summary>Active lessons of a project compared for similarity (the newest).</summary>
    public const int MaxSimilarityScan = 2_000;
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
    public const int DefaultUnmatchedDays = 30;
    /// <summary>Open challenges shown per rule in a briefing or review-queue item (the rest are counted).</summary>
    public const int MaxShownChallenges = 3;
    public const int MaxSkills = 50;
    /// <summary>Lessons of the pool embedded on one similarity call when they have no stored embedding yet.</summary>
    public const int MaxEmbedPerCall = 25;
}

// ---- Inputs ---------------------------------------------------------------------------------------

public sealed class PromoteRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId)]
    public string? SessionId { get; init; }

    [Description("Id of the lesson.")]
    public required string Id { get; init; }

    [Description("Its version as last read; refused conflict with the current lesson otherwise.")]
    public required long ExpectedVersion { get; init; }

    [Description("propose: ask for the lesson to become a rule (review queue). approve: make it a rule. reject: refuse rule status with reason (it is not proposed or promoted by recurrence again). " +
                 "addressed_by: the lesson is addressed locally by a skill, instruction, hook, test or CI gate (with addressed_by); it then leaves briefings.")]
    public required PromoteAction Action { get; init; }

    [Description("Why, at most 1000 characters. Required with reject.")]
    public string? Reason { get; init; }

    [Description("With action addressed_by: what addresses the lesson.")]
    public AddressedByInput? AddressedBy { get; init; }
}

public sealed class AddressedByInput
{
    [Description("skill, instruction, hook, test or ci.")]
    public required AddressedByKind Kind { get; init; }

    [Description("Its name, such as the skill's name or the test's name.")]
    public required string Name { get; init; }

    [Description("Where it is: a commit, a path or a URL.")]
    public string? Ref { get; init; }

    [Description("With kind instruction or hook (required, and only with those): the harness it lives in (such as claude-code); the rule is then not served to sessions of that harness. " +
                 "A skill hides the rule from sessions that declare it in skills; a test or ci gate from every briefing.")]
    public string? Harness { get; init; }
}

public sealed class ChallengeRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId)]
    public string? SessionId { get; init; }

    [Description("Open a challenge of an active rule. Pass exactly one of open, resolve.")]
    public ChallengeOpenInput? Open { get; init; }

    [Description("Resolve an open challenge: uphold the rule, amend it or retract it.")]
    public ChallengeResolveInput? Resolve { get; init; }

    [Description("With open: caller-chosen key, 1 to 128 printable ASCII characters. A repeat by the same identity in the project answers the first challenge, with created false.")]
    public string? IdempotencyKey { get; init; }
}

public sealed class ChallengeOpenInput
{
    [Description("Id of the rule (an active lesson that is a rule).")]
    public required string Id { get; init; }

    [Description("Why the rule is believed wrong, at most 1000 characters.")]
    public required string Reason { get; init; }

    [Description("The evidence, 1 to 10 entries: a test result, a source, a counter-example, a link.")]
    public required List<EvidenceInput> Evidence { get; init; }
}

public sealed class EvidenceInput
{
    [Description("test_result, source, counter_example, link or other.")]
    public required EvidenceKind Kind { get; init; }

    [Description("What it shows, one line, at most 500 characters.")]
    public required string Summary { get; init; }

    [Description("Where it is: a URL, a path, a commit, a run id.")]
    public string? Ref { get; init; }
}

public sealed class ChallengeResolveInput
{
    [Description("Id of the challenge.")]
    public required string ChallengeId { get; init; }

    [Description("The rule's version as last read; refused conflict with the current rule otherwise.")]
    public required long ExpectedVersion { get; init; }

    [Description("uphold (the rule stands), amend (a new revision of the rule, with amend) or retract (the rule was wrong).")]
    public required ChallengeOutcome Outcome { get; init; }

    [Description("Why, at most 1000 characters. Recorded on the challenge and the rule.")]
    public required string Reason { get; init; }

    [Description("With outcome amend: the rule's new text and trigger.")]
    public RuleAmendInput? Amend { get; init; }
}

public sealed class RuleAmendInput
{
    [Description("The correct behavior, one line, at most 500 characters.")]
    public string? Summary { get; init; }

    [Description("What happened.")]
    public string? What { get; init; }

    [Description("When the rule applies (replaces the stored applies-when).")]
    public AppliesWhenInput? AppliesWhen { get; init; }
}

public sealed class LessonsRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId)]
    public string? SessionId { get; init; }

    [Description("Key or id of the Project. May be left out with session_id.")]
    public string? Project { get; init; }

    [Description("recently_learned (rules, newest first), recurrence (lessons that recurred, with and without their rule served), unaddressed (rules not addressed_by a local fix, grouped by activity), unmatched (rules not served for since_days).")]
    public required LessonsView View { get; init; }

    [Description("Only lessons applying to this activity.")]
    public LessonActivity? Activity { get; init; }

    [Description("Only lessons first recorded from this correction source.")]
    public CorrectionSourceKind? Source { get; init; }

    [Description("With view unmatched: days without being served, 1 to 3650 (default 30).")]
    public int? SinceDays { get; init; }

    [Description("Page size, 1 to 50 (default 20).")]
    public int? Limit { get; init; }

    [Description("next_cursor of the previous page, with the same arguments.")]
    public string? Cursor { get; init; }
}

public sealed class ReviewQueueRequest : ISessionArguments
{
    [Description(WorkRequestText.SessionId)]
    public string? SessionId { get; init; }

    [Description("Key or id of the Project. May be left out with session_id.")]
    public string? Project { get; init; }

    [Description("Page size, 1 to 50 (default 20).")]
    public int? Limit { get; init; }

    [Description("next_cursor of the previous page.")]
    public string? Cursor { get; init; }
}

/// <summary>record relate: decide about an already stored lesson after seeing its similar lessons.</summary>
public sealed class KnowledgeRelateInput
{
    [Description("Id of the lesson.")]
    public required string Id { get; init; }

    [Description("Its version as last read.")]
    public required long ExpectedVersion { get; init; }

    [Description("The lesson says the same as this lesson: it is merged into it (its corrections become recurrences and evidence of that one; it becomes superseded).")]
    public string? SameAs { get; init; }

    [Description("The lesson contradicts this lesson: both are kept, linked, served together with the conflict shown, and flagged in the review queue.")]
    public string? Contradicts { get; init; }
}

/// <summary>record acknowledge: an autonomous decision was reviewed; it leaves the review queue (it stands).</summary>
public sealed class KnowledgeAcknowledgeInput
{
    public required string Id { get; init; }

    public required long ExpectedVersion { get; init; }
}

// ---- Views -------------------------------------------------------------------------------------------

public sealed record LessonLinkView(Guid Id, string Summary, KnowledgeStatus Status, LessonRuleState? State = null);

public sealed record EvidenceView(EvidenceKind Kind, string Summary, string? Ref = null);

public sealed record ChallengeView(
    Guid Id,
    Guid EntryId,
    ChallengeStatus Status,
    string Reason,
    IReadOnlyList<EvidenceView> Evidence,
    WorkActor Author,
    DateTime CreatedAt,
    string? ResolutionReason = null,
    WorkActor? ResolvedBy = null,
    DateTime? ResolvedAt = null)
{
    public string Entity => "challenge";
}

public sealed record AddressedByView(AddressedByKind Kind, string Name, string? Ref, WorkActor Author, DateTime CreatedAt, string? Harness = null);

public sealed record LessonEventView(
    Guid CorrectionId,
    bool Counted,
    string Summary,
    WorkActor Author,
    DateTime CreatedAt,
    CorrectionSourceKind? Source = null,
    string? SourceRef = null,
    string? What = null,
    bool? RuleAtEvent = null,
    bool? Served = null,
    string? SessionId = null);

/// <summary>
/// Recurrence of a lesson: distinct source events (<see cref="Events"/>), and of the recurrences
/// (every event after the first) those before it was a rule, while it was a rule and served to the
/// recording session, while it was a rule and not served to it, and while a rule with no session known.
/// </summary>
public sealed record RecurrenceView(int Events, int BeforeRule, int WhileServed, int WhileNotServed, int Unknown);

/// <summary>The learning-loop state of a lesson (on its <see cref="KnowledgeView"/>).</summary>
public sealed record LessonView(
    LessonRuleState State,
    string Priority,
    RecurrenceView Recurrence,
    RuleReason? RuleReason = null,
    DateTime? RuleSince = null,
    string? RejectedReason = null,
    IReadOnlyList<LessonLinkView>? Contradicts = null,
    IReadOnlyList<ChallengeView>? Challenges = null,
    IReadOnlyList<AddressedByView>? AddressedBy = null,
    int? ServedSessions = null,
    DateTime? LastServedAt = null,
    IReadOnlyList<LessonEventView>? Events = null);

/// <summary>A lesson similar to one just recorded: a merge or contradiction candidate. CodeGraph never decides (E5).</summary>
/// <param name="Score">Lexical similarity 0..1 (Jaccard of the content words).</param>
/// <param name="PossibleContradiction">One says never/not/don't and the other does not: likely a contradiction, not a duplicate.</param>
/// <param name="Method">embedding (cosine of the configured embedding model) or lexical (no embedding provider).</param>
public sealed record SimilarLesson(Guid Id, string Summary, double Score, long Version, LessonRuleState State, bool? PossibleContradiction = null, string Method = "lexical");

/// <summary>The similar lessons of one lesson a write recorded (or merged into).</summary>
public sealed record SimilarLessons(Guid Entry, IReadOnlyList<SimilarLesson> Candidates);

/// <summary>A rule in a briefing: data, never an instruction.</summary>
public sealed record BriefingRule(
    Guid Id,
    string Summary,
    long Version,
    string Priority,
    IReadOnlyList<LessonActivity> Activity,
    int? Recurrence = null,
    IReadOnlyList<LessonLinkView>? Contradicts = null,
    IReadOnlyList<BriefingChallenge>? Challenges = null,
    // Open challenges beyond the ones shown.
    int? MoreChallenges = null);

public sealed record BriefingChallenge(Guid Id, string Reason, IReadOnlyList<string> Evidence);

public sealed record LessonRow(
    Guid Id,
    string Summary,
    long Version,
    LessonRuleState State,
    string Priority,
    RecurrenceView Recurrence,
    DateTime CreatedAt,
    IReadOnlyList<LessonActivity>? Activity = null,
    CorrectionSourceKind? Source = null,
    RuleReason? RuleReason = null,
    DateTime? RuleSince = null,
    DateTime? LastServedAt = null,
    bool? Contradicted = null,
    int? OpenChallenges = null,
    IReadOnlyList<AddressedByView>? AddressedBy = null);

public sealed record LessonGroup(LessonActivity Activity, IReadOnlyList<LessonRow> Lessons);

/// <summary>A GitHub review or review comment on an item of the project that no lesson names as its source (source.ref is its URL or id).</summary>
public sealed record UnlearnedReview(Guid EvidenceId, string Item, GitHubEvidenceKind Kind, string AuthorLogin, string Source, string Priority, DateTime CreatedAt,
    string? Url = null, string? ExternalId = null, string? ReviewState = null, string? Body = null);

public sealed record LessonsResult(LessonsView View, IReadOnlyList<LessonRow>? Items = null, IReadOnlyList<LessonGroup>? Groups = null, string? NextCursor = null,
    IReadOnlyList<UnlearnedReview>? Reviews = null);

public static class ReviewKinds
{
    public const string Challenge = "challenge";
    public const string ProposedRule = "proposed_rule";
    public const string Contradiction = "contradiction";
    public const string AutonomousDecision = "autonomous_decision";
}

/// <summary>One entry of the review queue. Informational: nothing waits on it (E6).</summary>
public sealed record ReviewItem(
    string Kind,
    KnowledgeRow Entry,
    string Priority,
    int Recurrence,
    DateTime Since,
    KnowledgeRow? Other = null,
    IReadOnlyList<ChallengeView>? Challenges = null,
    int? MoreChallenges = null);

public sealed record ReviewQueueResult(IReadOnlyList<ReviewItem> Items, string? NextCursor = null);

public sealed record PromoteResult(KnowledgeView Entry, bool Changed);

public sealed record ChallengeResult(bool Created, ChallengeView Challenge, KnowledgeView Rule);

// ---- Similarity ---------------------------------------------------------------------------------------

/// <summary>
/// Deterministic lexical similarity of lessons (E5: no model call, no network): the Jaccard index of
/// their content words (lower case, apostrophes dropped, stop words and polarity words removed, a
/// plain suffix stem). Polarity is compared apart: one text negating (never, not, don't, avoid) and
/// the other not marks a possible contradiction, never a duplicate. It only lists candidates; the
/// recording agent decides same_as or contradicts.
/// </summary>
public static class LessonSimilarity
{
    public const double CandidateThreshold = 0.34;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "to", "of", "in", "on", "for", "and", "or", "is", "are", "be", "been", "with", "when", "while", "it", "its", "this",
        "that", "these", "those", "as", "at", "by", "from", "into", "than", "then", "so", "if", "you", "your", "we", "our", "i", "my", "any",
        "each", "every", "all", "should", "must", "always", "do", "does", "use", "make"
    };

    private static readonly HashSet<string> Negations = new(StringComparer.Ordinal)
    {
        "never", "not", "no", "dont", "doesnt", "cant", "cannot", "shouldnt", "mustnt", "wont", "avoid", "without", "nor", "stop", "isnt", "arent"
    };

    public static IReadOnlySet<string> Words(string text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Tokens(text))
        {
            if (StopWords.Contains(token) || Negations.Contains(token))
                continue;
            words.Add(Stem(token));
        }

        return words;
    }

    public static bool Negates(string text) => Tokens(text).Any(Negations.Contains);

    public static double Score(string a, string b)
    {
        var left = Words(a);
        var right = Words(b);
        if (left.Count == 0 || right.Count == 0)
            return 0;
        var shared = left.Count(right.Contains);
        return Math.Round((double)shared / (left.Count + right.Count - shared), 3);
    }

    private static IEnumerable<string> Tokens(string text)
    {
        var current = new StringBuilder();
        foreach (var raw in text.ToLowerInvariant())
        {
            if (raw is '\'' or '’')
                continue;
            if (char.IsLetterOrDigit(raw))
            {
                current.Append(raw);
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private static string Stem(string word)
    {
        foreach (var suffix in new[] { "ing", "ed", "es", "s" })
        {
            if (word.Length > suffix.Length + 3 && word.EndsWith(suffix, StringComparison.Ordinal))
                return word[..^suffix.Length];
        }

        return word;
    }

    public static string Priority(CorrectionSourceKind? kind) => kind switch
    {
        CorrectionSourceKind.Prompt => "highest",
        CorrectionSourceKind.Review => "high",
        _ => "normal"
    };

    public static int PriorityRank(string? priority) => priority switch
    {
        "highest" => 0,
        "high" => 1,
        _ => 2
    };
}
