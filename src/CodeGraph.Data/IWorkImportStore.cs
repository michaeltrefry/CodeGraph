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
    /// Makes the item's imported blockers <paramref name="blockers"/>: adds the missing ones (cycle-checked
    /// under the dependency lock; a blocker that would close a cycle is answered in
    /// <see cref="DependencySync.Cycles"/> and not added) and removes only blockers an earlier run imported.
    /// Blockers added by anyone else are never touched.
    /// </summary>
    Task<DependencySync> SyncImportedDependenciesAsync(string source, string externalKind, string externalId, IReadOnlyList<Guid> blockers, WorkActor actor, CancellationToken ct = default);

    /// <summary>Adds comments keyed by their external id (a repeat changes nothing) to an item, as one fed write.</summary>
    Task<int> AddImportedCommentsAsync(Guid itemId, IReadOnlyList<ImportedComment> comments, WorkActor actor, CancellationToken ct = default);

    /// <summary>Whether the item already has the attachment of <paramref name="externalId"/> recorded.</summary>
    Task<bool> HasAttachmentAsync(Guid itemId, string externalId, CancellationToken ct = default);

    Task AddAttachmentAsync(Guid itemId, NewAttachmentSpec attachment, WorkActor actor, CancellationToken ct = default);

    Task<AttachmentRecord?> GetAttachmentAsync(Guid attachmentId, CancellationToken ct = default);

    Task<ImportSettings?> GetImportSettingsAsync(string source, CancellationToken ct = default);

    /// <summary>Stores the settings; with <paramref name="approvedBy"/> they are approved now, otherwise unapproved.</summary>
    Task<ImportSettings> SaveImportSettingsAsync(string source, ImportSettings settings, string? approvedBy, CancellationToken ct = default);

    /// <summary>Starts a run, refused (conflict) while another run of the source is alive (heartbeat within <paramref name="staleAfter"/>).</summary>
    Task<Guid> StartImportRunAsync(string source, ImportMode mode, DateTime? updatedSince, string startedBy, TimeSpan staleAfter, CancellationToken ct = default);

    Task HeartbeatImportRunAsync(Guid runId, ImportReport report, CancellationToken ct = default);

    Task CompleteImportRunAsync(Guid runId, string status, ImportReport report, string? error, CancellationToken ct = default);

    Task<ImportRunView?> GetImportRunAsync(Guid runId, CancellationToken ct = default);

    Task<IReadOnlyList<ImportRunView>> ListImportRunsAsync(string source, int limit, CancellationToken ct = default);

    /// <summary>The start of the newest completed import run of the source (the default updated_since of a delta sync).</summary>
    Task<DateTime?> LastCompletedImportStartAsync(string source, CancellationToken ct = default);
}

public sealed record ImportRecord(string ExternalId, string EntityKind, Guid EntityId, Guid ProjectId, string ContentHash, IReadOnlyList<Guid> Edges, DateTime? SourceUpdatedAt);

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
    string ContentHash);

public sealed record NewAttachmentSpec(string ExternalId, string Filename, string ContentType, long SizeBytes, string? Sha256, string? StoragePath, string? SourceUrl, string? SkippedReason, DateTime CreatedAt);

public sealed record AttachmentRecord(Guid Id, Guid ItemId, string Filename, string ContentType, long SizeBytes, string? StoragePath);
