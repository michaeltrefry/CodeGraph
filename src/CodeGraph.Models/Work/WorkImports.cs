namespace CodeGraph.Models.Work;

// Imports into the work tracker (sc-27876, design §10): the Shortcut workspace, the KanbanBoard
// "SceneWorks Code Review 2026-10-27" epic and the native-memory candidate batches for S5's
// import_knowledge. Sources are read only. Serialized with WorkJson.Options (snake_case).

public static class ImportSources
{
    public const string Shortcut = "shortcut";
    public const string Kanban = "kanban";

    /// <summary>The system actor an import's writes are attributed to (E3): <c>import:shortcut</c>.</summary>
    public static WorkActor Actor(string source) => new($"import:{source}", WorkActor.SystemKind);
}

/// <summary>What an imported feature or item came from, and the source's history fields kept as history (not claims).</summary>
public sealed record ImportedView(string Source, string ExternalId, string? Url = null, ImportHistory? History = null);

/// <param name="Owners">Owners in the source, by name: history, never converted into claims.</param>
/// <param name="State">The source workflow state the status was mapped from.</param>
/// <param name="Estimate">Always null: estimates are dropped (design §10.1).</param>
public sealed record ImportHistory(
    IReadOnlyList<string>? Owners = null,
    string? Requester = null,
    DateTime? StartedAt = null,
    DateTime? CompletedAt = null,
    string? State = null,
    string? Team = null,
    string? Estimate = null);

public sealed record AttachmentView(Guid Id, string Filename, string ContentType, long SizeBytes, bool Stored, DateTime CreatedAt, string? SkippedReason = null);

public enum ImportMode
{
    /// <summary>Reads everything, writes nothing but the run's report.</summary>
    DryRun,
    /// <summary>Writes through the work store; with updated_since (or after a completed import) a delta sync.</summary>
    Import
}

public static class ImportRunStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>One row of the team → project table. <see cref="TeamKey"/> is the Shortcut team id, or <c>none</c> for work without a team.</summary>
public sealed record ImportTeamMapping(string TeamKey, string ProjectKey, string? TeamName = null, string? ProjectName = null);

/// <summary>How a source account is attributed: <see cref="Kind"/> is <c>user</c> or <c>agent</c>.</summary>
public sealed record ImportMemberMapping(string MemberId, string Name, string Kind);

/// <summary>
/// The owner-approved settings of a source. Shortcut: the team → project table, the holding
/// project for unmapped work and the member map. KanbanBoard: <see cref="KanbanEpicId"/> and its
/// target project (<see cref="HoldingProjectKey"/>).
/// </summary>
public sealed record ImportSettings(
    string HoldingProjectKey,
    IReadOnlyList<ImportTeamMapping> Teams,
    IReadOnlyList<ImportMemberMapping>? Members = null,
    string? KanbanEpicId = null,
    string? ApprovedBy = null,
    DateTime? ApprovedAt = null);

public sealed record ImportRunView(
    Guid Id,
    string Source,
    ImportMode Mode,
    string Status,
    string StartedBy,
    DateTime StartedAt,
    DateTime? CompletedAt,
    DateTime? UpdatedSince,
    ImportReport? Report,
    string? Error);

/// <summary>
/// The mapping report of a run (design §10.1 step 1): the team → project table, counts per project
/// and status, items without an inferable repository, unmapped relations and attachment volume; on
/// an import also what was written. Counts of a dry run and of the import that follows it match.
/// </summary>
public sealed class ImportReport
{
    public string Source { get; set; } = "";
    public ImportMode Mode { get; set; }
    public DateTime? UpdatedSince { get; set; }
    public bool SettingsApproved { get; set; }
    public string HoldingProject { get; set; } = "";
    public List<ImportTeamRow> Teams { get; set; } = [];
    public SortedDictionary<string, ImportProjectCounts> Projects { get; set; } = new(StringComparer.Ordinal);
    public ImportSourceCounts Epics { get; set; } = new();
    public ImportSourceCounts Stories { get; set; } = new();
    public int NoRepositoryCount { get; set; }
    /// <summary>The first 500 of them (sc-NNNN).</summary>
    public List<string> NoRepository { get; set; } = [];
    public List<ImportRelationIssue> UnmappedRelations { get; set; } = [];
    public ImportAttachmentCounts Attachments { get; set; } = new();
    public int Documents { get; set; }
    public int Comments { get; set; }
    public int Links { get; set; }
    public int Relations { get; set; }
    public int Subtasks { get; set; }
    public int AcceptanceCriteria { get; set; }
    public int BlockedWithoutEdges { get; set; }
    public int ReconcileTasks { get; set; }
    public ImportWriteCounts Written { get; set; } = new();
    public List<string> Warnings { get; set; } = [];

    public ImportProjectCounts Project(string key) =>
        Projects.TryGetValue(key, out var counts) ? counts : Projects[key] = new ImportProjectCounts();

    public void AddWarning(string warning)
    {
        if (Warnings.Count < 500)
            Warnings.Add(warning);
    }
}

public sealed class ImportTeamRow
{
    public string TeamKey { get; set; } = "";
    public string? TeamName { get; set; }
    public string ProjectKey { get; set; } = "";
    /// <summary>approved (in the approved table), proposed (dry run before approval) or holding (not in the approved table).</summary>
    public string Mapping { get; set; } = "";
    public bool ProjectExists { get; set; }
    public int Epics { get; set; }
    public int Stories { get; set; }
}

public sealed class ImportProjectCounts
{
    public int Features { get; set; }
    public int Items { get; set; }
    public SortedDictionary<string, int> ByStatus { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> ByType { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ImportSourceCounts
{
    public int Seen { get; set; }
    public int ArchivedSkipped { get; set; }
    public int Open { get; set; }
    public int Done { get; set; }
    public int Mapped { get; set; }
}

public sealed record ImportRelationIssue(string From, string Verb, string To, string Reason);

public sealed class ImportAttachmentCounts
{
    public int Files { get; set; }
    public long TotalBytes { get; set; }
    public int OverLimit { get; set; }
    public long OverLimitBytes { get; set; }
    public int LinkedFiles { get; set; }
    public int Stored { get; set; }
    public int Failed { get; set; }
}

public sealed class ImportWriteCounts
{
    public int ProjectsCreated { get; set; }
    public int FeaturesCreated { get; set; }
    public int FeaturesUpdated { get; set; }
    public int FeaturesUnchanged { get; set; }
    public int ItemsCreated { get; set; }
    public int ItemsUpdated { get; set; }
    public int ItemsUnchanged { get; set; }
    public int ReconcileTasksCreated { get; set; }
    public int DependenciesAdded { get; set; }
    public int DependenciesRemoved { get; set; }
    public int RelationComments { get; set; }
    public int AttachmentsStored { get; set; }
    public int Failures { get; set; }
}

// ---- REST requests (api/work/imports) -------------------------------------------------------------

public sealed class ImportSettingsRequest
{
    /// <summary>Where work of a team missing from the table goes (Shortcut), or the target project (KanbanBoard).</summary>
    public required string HoldingProjectKey { get; init; }
    public List<ImportTeamMapping>? Teams { get; init; }
    public List<ImportMemberMapping>? Members { get; init; }
    public string? KanbanEpicId { get; init; }
    /// <summary>true approves the table now: imports are refused until it is approved.</summary>
    public bool? Approve { get; init; }
}

public sealed class ImportRunRequest
{
    /// <summary>dry_run (default), import or delta.</summary>
    public string? Mode { get; init; }
    public DateTime? UpdatedSince { get; init; }
}

public sealed class MemoryFileInput
{
    public required string Path { get; init; }
    public required string Content { get; init; }
}

public sealed class MemoryCandidatesRequest
{
    /// <summary>claude (memory/*.md files with frontmatter) or codex (MEMORY.md task groups).</summary>
    public required string Harness { get; init; }
    public required List<MemoryFileInput> Files { get; init; }
    /// <summary>A checkout of the repository readable by the server, for the path pre-check; left out, every candidate pre-checks as unverifiable.</summary>
    public string? RepositoryRoot { get; init; }
    /// <summary>Codex: only task groups whose scope or applies_to names this (a repository name).</summary>
    public string? CwdFilter { get; init; }
}

public sealed class WorkImportOptions
{
    public const string Section = "WorkImport";

    /// <summary>Requests per minute to Shortcut's REST API; Shortcut allows 200.</summary>
    public int ShortcutRequestsPerMinute { get; set; } = 180;
    public string ShortcutBaseUrl { get; set; } = "https://api.app.shortcut.com/api/v3/";
    public string KanbanBaseUrl { get; set; } = "https://kanban.trefry.net/";
    /// <summary>Where downloaded attachments are stored. Default: the wiki attachment root's work-items folder.</summary>
    public string? AttachmentStoragePath { get; set; }
    /// <summary>The size bound per file; a larger file is recorded with its link and not downloaded.</summary>
    public long MaxAttachmentBytes { get; set; } = 50L * 1024 * 1024;
}
