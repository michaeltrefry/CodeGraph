using CodeGraph.Models.Work;

namespace CodeGraph.Data;

/// <summary>
/// The import writes of the work tracker (sc-27876). Every write is keyed by the source object
/// (source, kind, external id), runs in one transaction with its change-feed entry (E7) attributed
/// to the importing actor (E3), and keeps the source's own times and comment authors. A write whose
/// mapped content is unchanged since the last run writes nothing.
/// </summary>
public interface IWorkImportStore
{
    Task<IReadOnlyDictionary<string, ImportRecord>> ListImportRecordsAsync(string source, string externalKind, CancellationToken ct = default);

    Task<ImportWrite> UpsertImportedFeatureAsync(ImportedFeatureSpec spec, WorkActor actor, CancellationToken ct = default);

    Task<ImportWrite> UpsertImportedItemAsync(ImportedItemSpec spec, WorkActor actor, CancellationToken ct = default);

    /// <summary>
    /// Brings the source's blockers of the item (<paramref name="blockers"/>) over as a three-way merge
    /// against the blockers of the last sync: nothing is done when they are unchanged; a blocker the source
    /// added gets an edge (cycle-checked under the dependency lock; a cycle is answered in
    /// <see cref="DependencySync.Cycles"/>); a blocker the source dropped loses its edge only when the
    /// import inserted it. Edges anyone else added are never removed.
    /// </summary>
    Task<DependencySync> SyncImportedDependenciesAsync(string source, string externalKind, string externalId, IReadOnlyList<Guid> blockers, WorkActor actor, CancellationToken ct = default);

    /// <summary>Adds comments keyed by their external id (a repeat changes nothing) to an item, as one fed write.</summary>
    Task<int> AddImportedCommentsAsync(Guid itemId, IReadOnlyList<ImportedComment> comments, WorkActor actor, CancellationToken ct = default);

    /// <summary>What the item's record of the attachment says (stored path, or why it was not stored), or null when there is none.</summary>
    Task<AttachmentState?> GetAttachmentStateAsync(Guid itemId, string externalId, CancellationToken ct = default);

    Task AddAttachmentAsync(Guid itemId, NewAttachmentSpec attachment, WorkActor actor, CancellationToken ct = default);

    Task<AttachmentRecord?> GetAttachmentAsync(Guid attachmentId, CancellationToken ct = default);

    Task<ImportSettings?> GetImportSettingsAsync(string source, CancellationToken ct = default);

    /// <summary>Stores the settings; with <paramref name="approvedBy"/> they are approved now, otherwise unapproved.</summary>
    Task<ImportSettings> SaveImportSettingsAsync(string source, ImportSettings settings, string? approvedBy, CancellationToken ct = default);

    /// <summary>Starts a run, refused (conflict) while another run of the source is alive (heartbeat within <paramref name="staleAfter"/>).</summary>
    Task<Guid> StartImportRunAsync(string source, ImportMode mode, DateTime? updatedSince, string startedBy, TimeSpan staleAfter, CancellationToken ct = default);

    /// <summary>Moves the run's heartbeat, and with a report saves its progress (never replaced by null). Only a running run.</summary>
    Task HeartbeatImportRunAsync(Guid runId, ImportReport? report, CancellationToken ct = default);

    /// <summary>Ends the run, only while it is still running; false when it was not (a later start declared it dead).</summary>
    Task<bool> CompleteImportRunAsync(Guid runId, string status, ImportReport? report, string? error, CancellationToken ct = default);

    Task<ImportRunView?> GetImportRunAsync(Guid runId, CancellationToken ct = default);

    Task<IReadOnlyList<ImportRunView>> ListImportRunsAsync(string source, int limit, CancellationToken ct = default);

    /// <summary>The newest completed import run of the source with its report (a delta continues from its start and retries its failures).</summary>
    Task<ImportRunView?> LastCompletedImportAsync(string source, CancellationToken ct = default);

    /// <summary>Every repository CodeGraph indexes, with its remote URL and the project that owns it (sc-27928).</summary>
    Task<IReadOnlyList<IndexedRepository>> ListIndexedRepositoriesAsync(CancellationToken ct = default);

    /// <summary>
    /// The repositories an import's table has applied to a project (sc-27928): attached by it, or owned already when
    /// the table named them. A repository is applied once, so one the owner takes away later is not attached again.
    /// </summary>
    Task<IReadOnlySet<string>> ListAppliedRepositoriesAsync(string source, Guid projectId, CancellationToken ct = default);

    /// <summary>Records repositories as applied to the project (a repeat changes nothing).</summary>
    Task RecordAppliedRepositoriesAsync(string source, Guid projectId, IReadOnlyCollection<string> repositories, CancellationToken ct = default);
}

/// <param name="Name">The repository as CodeGraph names it (repositories.name).</param>
/// <param name="OwnedBy">The key of the project that owns it, or null.</param>
public sealed record IndexedRepository(string Name, string? RepoUrl, string? OwnedBy);

/// <param name="Edges">The blockers whose edges the import inserted (the only ones it may remove).</param>
/// <param name="HasRelations">Whether a dependency sync recorded the source's blockers.</param>
public sealed record ImportRecord(string ExternalId, string EntityKind, Guid EntityId, Guid ProjectId, string ContentHash, IReadOnlyList<Guid> Edges, DateTime? SourceUpdatedAt,
    bool HasRelations = false);

public enum ImportOutcome
{
    Created,
    Updated,
    Unchanged
}

/// <param name="Warnings">What the write could not carry over (an alias another item holds, a repository the project does not own).</param>
public sealed record ImportWrite(Guid Id, string Key, Guid ProjectId, ImportOutcome Outcome, IReadOnlyList<string>? Warnings = null);

public sealed record DependencySync(int Added, int Removed, IReadOnlyList<Guid> Cycles, IReadOnlyList<Guid> OtherProject);

/// <param name="ExternalId">Unique within the item (Shortcut comment id, <c>rel:LINK</c>, ...).</param>
/// <param name="AuthorKind"><c>user</c>, <c>agent</c> or <c>system</c>.</param>
public sealed record ImportedComment(string ExternalId, string Author, string AuthorKind, string Body, DateTime CreatedAt);

public sealed record ImportedFeatureSpec(
    string Source,
    string ExternalId,
    Guid ProjectId,
    string Title,
    string? Description,
    IReadOnlyList<DocumentSpec> Documents,
    bool RequirementsUnreconciled,
    ImportedView Imported,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string ContentHash);

/// <param name="ExternalKind"><c>story</c>, <c>item</c> (KanbanBoard) or <c>reconcile</c> (the reconcile task of a feature).</param>
/// <param name="Repository">An owned repository of the project, or null.</param>
public sealed record ImportedItemSpec(
    string Source,
    string ExternalKind,
    string ExternalId,
    Guid ProjectId,
    Guid? FeatureId,
    string Title,
    WorkItemType Type,
    WorkStatus Status,
    WorkPriority Priority,
    string? Description,
    IReadOnlyList<string> Labels,
    string? Repository,
    IReadOnlyList<CriterionSpec> AcceptanceCriteria,
    IReadOnlyList<ImportedComment> Comments,
    IReadOnlyList<LinkSpec> Links,
    IReadOnlyList<string> Aliases,
    ImportedView Imported,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int Order,
    string ContentHash,
    // PR links of PRs already merged or closed in the source: GitHub events on them never move the item.
    IReadOnlyList<string>? HistoricalLinks = null);

public sealed record NewAttachmentSpec(string ExternalId, string Filename, string ContentType, long SizeBytes, string? Sha256, string? StoragePath, string? SourceUrl, string? SkippedReason, DateTime CreatedAt);

public sealed record AttachmentState(string? StoragePath, string? SkippedReason)
{
    /// <summary>The reason of a file over the size bound: it is linked, not downloaded, and never retried.</summary>
    public const string OverLimitPrefix = "Over the size bound";

    public bool OverLimit => SkippedReason?.StartsWith(OverLimitPrefix, StringComparison.Ordinal) == true;
}

public sealed record AttachmentRecord(Guid Id, Guid ItemId, string Filename, string ContentType, long SizeBytes, string? StoragePath);
