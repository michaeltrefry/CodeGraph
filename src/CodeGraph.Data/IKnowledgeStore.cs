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
    /// them would not be stored now (nothing is stored then either).
    /// </summary>
    Task<ImportKnowledgeResult> ImportKnowledgeAsync(Guid projectId, IReadOnlyList<ImportCandidateSpec> candidates, string previewToken,
        IReadOnlyList<int>? accept, WorkActor actor, CancellationToken ct = default);
}

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
    SupersedeSpec? Supersedes = null)
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
    KnowledgeCursor? After);

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
    int Limit);

/// <summary>A candidate of an import: its spec, or (when its shape is invalid) the reason.</summary>
public sealed record ImportCandidateSpec(int Index, KnowledgeSpec? Spec, ImportVerification Verification, string? InvalidMessage = null);
