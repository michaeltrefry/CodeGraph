using CodeGraph.Models.Work;

namespace CodeGraph.Data;

/// <summary>
/// Persistence of the project knowledge store (sc-27869, design §5.3). Implemented by the work
/// store (MySqlWorkStore), so the save-point fields of update_work and end_session are written in
/// the same transaction as the work write (see <see cref="ItemPatchSpec.Knowledge"/>). Writes are
/// synchronous, return the stored entry, never edit an entry in place (each adds a revision), and
/// are refused with a <see cref="WorkException"/>. Specs arrive validated for shape; the store
/// checks scope (repositories are members of the entry's project, nodes exist) against the database.
/// </summary>
public interface IKnowledgeStore
{
    /// <summary>Stores a new entry (superseding <see cref="KnowledgeSpec.Supersedes"/>); a repeated key answers the first one, Created false.</summary>
    Task<RecordResult> RecordKnowledgeAsync(Guid projectId, KnowledgeSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default);

    /// <summary>Replaces an active entry's content with <paramref name="state"/> as a new revision and re-verifies it.</summary>
    Task<KnowledgeView> ReviseKnowledgeAsync(Guid entryId, long expectedVersion, KnowledgeSpec state, WorkActor actor, CancellationToken ct = default);

    Task<KnowledgeView> RetractKnowledgeAsync(Guid entryId, long expectedVersion, string reason, WorkActor actor, CancellationToken ct = default);

    /// <summary>Makes the current indexed content of the entry's files its baseline.</summary>
    Task<KnowledgeView> VerifyKnowledgeAsync(Guid entryId, long expectedVersion, string? commit, WorkActor actor, CancellationToken ct = default);

    Task<KnowledgeView?> GetKnowledgeAsync(Guid entryId, bool withRevisions, CancellationToken ct = default);

    /// <summary>Rows of the given entries, in the order given.</summary>
    Task<IReadOnlyList<KnowledgeRow>> GetKnowledgeRowsAsync(IReadOnlyList<Guid> entryIds, CancellationToken ct = default);

    Task<RecallPage> RecallAsync(RecallSpec spec, CancellationToken ct = default);

    /// <summary>
    /// The active entries relevant to a context (a briefing, an item brief), ranked: linked to one
    /// of its items, then scoped to one of its paths, then to its repository, then project-wide.
    /// An entry scoped to paths none of which match, or to other repositories only, is left out.
    /// </summary>
    Task<IReadOnlyList<KnowledgeRow>> ReadRelevantKnowledgeAsync(KnowledgeContext context, CancellationToken ct = default);

    /// <summary>
    /// The native-memory import (§10.3). Checks each candidate's scope and looks for duplicates
    /// (an active entry of the project of the same kind with the same normalized summary, or an
    /// earlier candidate of the batch). Without <paramref name="accept"/> it stores nothing. With it,
    /// it stores exactly the accepted candidates in one transaction, or refuses the call when any of
    /// them would not be stored now (nothing is stored then either). A confirm repeated with the same
    /// <paramref name="keyHash"/> stores nothing and answers the first confirm's result.
    /// </summary>
    Task<ImportKnowledgeResult> ImportKnowledgeAsync(Guid projectId, IReadOnlyList<ImportCandidateSpec> candidates, string previewToken,
        IReadOnlyList<int>? accept, string? keyHash, WorkActor actor, CancellationToken ct = default);

    // ---- Lessons, rules and challenges (S9) -------------------------------------------------------

    /// <summary>The active lessons of the project lexically similar to each of <paramref name="entryIds"/> (candidates only; nothing is decided).</summary>
    /// <remarks>With <paramref name="embedder"/> the candidates are found by cosine similarity of stored embeddings (lessons without one are embedded and stored);
    /// without it by the lexical score. Either way the polarity flag is lexical.</remarks>
    Task<IReadOnlyList<SimilarLessons>> FindSimilarLessonsAsync(Guid projectId, IReadOnlyList<Guid> entryIds, LessonEmbedder? embedder, CancellationToken ct = default);

    /// <summary>
    /// The rules to serve in a briefing: active, not addressed locally, with an applies-when activity
    /// among the context's and a location (repositories, paths, harness) that matches; with their
    /// contradictions and open challenges. Ranked by priority, then recurrence, then the newest.
    /// </summary>
    Task<IReadOnlyList<BriefingRule>> ReadRulesAsync(RuleContext context, CancellationToken ct = default);

    /// <summary>The activities the project's servable rules apply to.</summary>
    Task<IReadOnlyList<LessonActivity>> ReadRuleActivitiesAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>The served-rule log: the session's briefing served these rules now.</summary>
    Task LogServedRulesAsync(Guid sessionId, Guid projectId, IReadOnlyList<Guid> ruleIds, CancellationToken ct = default);

    /// <summary>The activities and the loaded skills a session declared (the rule filter of its briefings); a null list leaves that one unchanged.</summary>
    Task SetSessionBriefingContextAsync(Guid sessionId, IReadOnlyList<LessonActivity>? activity, IReadOnlyList<string>? skills, CancellationToken ct = default);

    /// <summary>record relate: merge a lesson into another (same_as) or link the two as contradicting.</summary>
    Task<KnowledgeView> RelateLessonAsync(Guid entryId, long expectedVersion, Guid? sameAs, Guid? contradicts, WorkActor actor, CancellationToken ct = default);

    /// <summary>Reverses the journalled same_as merge of this superseded lesson, preserving later target corrections.</summary>
    Task<KnowledgeView> UnmergeLessonAsync(Guid entryId, long expectedVersion, WorkActor actor, CancellationToken ct = default);

    /// <summary>record acknowledge: an autonomous decision was reviewed (it leaves the review queue).</summary>
    Task<KnowledgeView> AcknowledgeKnowledgeAsync(Guid entryId, long expectedVersion, WorkActor actor, CancellationToken ct = default);

    Task<PromoteResult> PromoteLessonAsync(Guid entryId, long expectedVersion, PromoteAction action, string? reason, AddressedBySpec? addressedBy, WorkActor actor, CancellationToken ct = default);

    Task<ChallengeResult> OpenChallengeAsync(Guid entryId, string reason, IReadOnlyList<EvidenceView> evidence, string? keyHash, WorkActor actor, CancellationToken ct = default);

    /// <summary>Resolves an open challenge; <paramref name="amended"/> (outcome amend) is the rule's new content.</summary>
    Task<ChallengeResult> ResolveChallengeAsync(Guid challengeId, long expectedVersion, ChallengeOutcome outcome, string reason, KnowledgeSpec? amended, WorkActor actor, CancellationToken ct = default);

    Task<ChallengeView?> GetChallengeAsync(Guid challengeId, CancellationToken ct = default);

    Task<LessonsPage> ReadLessonsAsync(LessonsSpec spec, CancellationToken ct = default);

    Task<ReviewQueuePage> ReadReviewQueueAsync(Guid projectId, int offset, int limit, CancellationToken ct = default);
}

public sealed record RuleContext(
    Guid ProjectId,
    IReadOnlyList<LessonActivity> Activities,
    string? Repository,
    IReadOnlyList<string> Paths,
    string? Harness,
    int Limit,
    IReadOnlyList<string>? Skills = null);

/// <param name="Harness">Instruction and hook only: the harness that carries the fix.</param>
public sealed record AddressedBySpec(AddressedByKind Kind, string Name, string? Ref, string? Harness = null);

/// <summary>An embedding provider for lesson similarity (E5: embeddings, never a model call that decides).</summary>
public sealed record LessonEmbedder(string Model, Func<IReadOnlyList<string>, IReadOnlyList<float[]>> Embed, double Threshold);

public sealed record LessonsSpec(Guid ProjectId, LessonsView View, LessonActivity? Activity, CorrectionSourceKind? Source, int SinceDays, int Offset, int Limit);

public sealed record LessonsPage(IReadOnlyList<LessonRow> Items, bool More, IReadOnlyList<UnlearnedReview>? Reviews = null);

public sealed record ReviewQueuePage(IReadOnlyList<ReviewItem> Items, bool More);

/// <summary>
/// A knowledge entry as the store writes it. <see cref="Repositories"/> null takes the default (the
/// linked item's repository, else none: the whole project). Paths are normalized (forward slashes,
/// relative). <see cref="Id"/> is chosen by the caller so it can read the entry back after a work
/// write that stored it.
/// </summary>
public sealed record KnowledgeSpec(
    KnowledgeKind Kind,
    string Summary,
    string? Body,
    KnowledgeConfidence Confidence,
    bool? MadeAutonomously,
    KnowledgeDetails? Details,
    IReadOnlyList<string>? Repositories,
    IReadOnlyList<string> Paths,
    IReadOnlyList<long> Nodes,
    string? Commit,
    Guid? ItemId = null,
    Guid? FeatureId = null,
    string? SourceFile = null,
    SupersedeSpec? Supersedes = null,
    // Lessons (S9): the recording agent's decision about an existing lesson.
    Guid? SameAs = null,
    Guid? Contradicts = null)
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

public sealed record SupersedeSpec(Guid EntryId, long ExpectedVersion);

public sealed record KnowledgeCursor(DateTime CreatedAt, Guid Id);

public sealed record RecallSpec(
    Guid ProjectId,
    IReadOnlyList<KnowledgeKind>? Kinds,
    string? Repository,
    IReadOnlyList<string> Paths,
    IReadOnlyList<long> Nodes,
    Guid? ItemId,
    LessonActivity? Activity,
    CorrectionSourceKind? Source,
    IReadOnlyList<string> Words,
    IReadOnlyList<KnowledgeStatus> Statuses,
    bool? PossiblyStale,
    WorkDetail Detail,
    int Limit,
    KnowledgeCursor? After,
    IReadOnlyList<LessonRuleState>? RuleStates = null);

public sealed record RecallPage(IReadOnlyList<object> Items, KnowledgeCursor? Next);

/// <summary>
/// What a briefing or an item brief is about. <see cref="Paths"/> are the briefing's paths today;
/// once items carry <c>touches</c> (S4), an item brief passes them here too.
/// </summary>
public sealed record KnowledgeContext(
    Guid ProjectId,
    string? Repository,
    IReadOnlyList<string> Paths,
    IReadOnlyList<Guid> ItemIds,
    IReadOnlyList<KnowledgeKind> Kinds,
    int Limit,
    // A briefing serves rules in a section of their own: its knowledge section then takes lessons
    // only by item (task-scoped). An item brief takes a lesson beyond its item only when it is a rule.
    bool RulesServedApart = false);

/// <summary>A candidate of an import: its spec, or (when its shape is invalid) the reason.</summary>
public sealed record ImportCandidateSpec(int Index, KnowledgeSpec? Spec, ImportVerification Verification, string? InvalidMessage = null);
