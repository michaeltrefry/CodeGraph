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
/// <param name="Archived">true once the source object was archived after it was imported (sc-27929): the item is closed (a finished one stays complete), the feature is flagged.</param>
public sealed record ImportedView(string Source, string ExternalId, string? Url = null, ImportHistory? History = null, bool? Archived = null);

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
/// <param name="Repositories">
/// Repositories CodeGraph indexes that the import makes owned repositories of <see cref="ProjectKey"/> before it writes
/// any item (sc-27928), so an item whose story names one gets it. A repository is owned by at most one project: one owned
/// by another project is refused when the table is saved. Null or empty: none is attached (the dry run proposes them, in
/// the team's row of its report, from the repositories the stories name).
/// </param>
public sealed record ImportTeamMapping(string TeamKey, string ProjectKey, string? TeamName = null, string? ProjectName = null, IReadOnlyList<string>? Repositories = null);

/// <summary>How a source account is attributed: <see cref="Kind"/> is <c>user</c> or <c>agent</c>.</summary>
public sealed record ImportMemberMapping(string MemberId, string Name, string Kind);

/// <summary>
/// The owner-approved settings of a source. Shortcut: the team → project table, the holding
/// project for unmapped work and the member map. KanbanBoard: <see cref="KanbanEpicId"/> and its
/// target project (<see cref="HoldingProjectKey"/>).
/// </summary>
/// <param name="RepositoryAliases">
/// Shortcut: retired repositories (<c>mlx-gen</c> or <c>owner/mlx-gen</c>) and the indexed repository their code
/// now lives in (<c>inference</c>). A story naming one is read as naming the other (sc-27928).
/// </param>
public sealed record ImportSettings(
    string HoldingProjectKey,
    IReadOnlyList<ImportTeamMapping> Teams,
    IReadOnlyList<ImportMemberMapping>? Members = null,
    string? KanbanEpicId = null,
    string? ApprovedBy = null,
    DateTime? ApprovedAt = null,
    IReadOnlyDictionary<string, string>? RepositoryAliases = null);

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
    /// <summary>
    /// The repositories the stories name (by their PRs, branches, commits, links and description), most named first
    /// (sc-27928): which CodeGraph repository each one is, and whether another project owns it. The first 200.
    /// </summary>
    public List<ImportRepositoryMention> RepositoryMentions { get; set; } = [];
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
    /// <summary>The source objects this run failed on (sc-N, epic-N): the next delta sync reads them again.</summary>
    public List<string> FailedIds { get; set; } = [];

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
    /// <summary>
    /// Where the team's epics and stories actually go, by project key (sc-27931): a story follows its epic's project,
    /// and work imported before stays where it is, so this can differ from <see cref="ProjectKey"/>.
    /// </summary>
    public SortedDictionary<string, int> EpicsByProject { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> StoriesByProject { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The repositories of the table's row (null: none set).</summary>
    public List<string>? Repositories { get; set; }
    /// <summary>
    /// Indexed repositories this team's stories name that the row's project would own, most named first (sc-27928):
    /// put them in the row's repositories (edited as you like) before approving. A repository another project owns,
    /// or one more stories of another project name, is not proposed here.
    /// </summary>
    public List<string> ProposedRepositories { get; set; } = [];
    /// <summary>The other teams of the table mapped to the same project key (sc-27931): confirm they belong together before approving.</summary>
    public List<string> SharesProjectWith { get; set; } = [];
}

/// <param name="Name">As the stories name it: <c>owner/repo</c>.</param>
/// <param name="Repository">The CodeGraph repository it is (matched by URL or name), or null when CodeGraph indexes none.</param>
/// <param name="OwnedBy">The project that owns that repository, if any.</param>
/// <param name="ProposedFor">The project whose stories name it most (it would own it), when it is indexed and no other project owns it.</param>
public sealed record ImportRepositoryMention(string Name, int Stories, string? Repository = null, string? OwnedBy = null, string? ProposedFor = null);

public sealed class ImportProjectCounts
{
    /// <summary>false: the import creates the project (sc-27931).</summary>
    public bool Exists { get; set; }
    /// <summary>The owned repositories the import's items choose from: the project's own and the table's (sc-27928).</summary>
    public List<string> Repositories { get; set; } = [];
    public int Features { get; set; }
    public int Items { get; set; }
    public SortedDictionary<string, int> ByStatus { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> ByType { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ImportSourceCounts
{
    public int Seen { get; set; }
    /// <summary>Archived in the source and never imported: skipped.</summary>
    public int ArchivedSkipped { get; set; }
    /// <summary>
    /// Imported before the source archived them (sc-27929): an item is closed (a finished one stays complete) and a
    /// feature flagged archived, with its open reconcile task closed. On a dry run, what the import would do.
    /// </summary>
    public int ArchivedClosed { get; set; }
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
    /// <summary>The table's repositories made owned repositories of their projects (sc-27928).</summary>
    public int RepositoriesAttached { get; set; }
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
    /// <summary>Retired repository → the indexed repository its code now lives in (Shortcut).</summary>
    public Dictionary<string, string>? RepositoryAliases { get; init; }
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
    /// <summary>
    /// The repository (as CodeGraph indexes it) whose checkout the path pre-check reads; left out, every
    /// candidate pre-checks as unverifiable. Only checkouts CodeGraph knows are read, never a caller's path.
    /// </summary>
    public string? Repository { get; init; }
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
    /// <summary>
    /// Where downloaded attachments are stored, relative to the host's working directory (/app in the
    /// containers). Both the API and the Jobs host read and write it, so it must be shared storage: the
    /// codegraph-uploads volume at /app/uploads in docker-compose.
    /// </summary>
    public string? AttachmentStoragePath { get; set; } = "uploads/work-items";
    /// <summary>The size bound per file; a larger file is recorded with its link and not downloaded.</summary>
    public long MaxAttachmentBytes { get; set; } = 50L * 1024 * 1024;
}
