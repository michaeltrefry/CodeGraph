namespace CodeGraph.Models.Work;

// Work imported from Shortcut and KanbanBoard before the 2026-11-02 cutover (sc-27876, design §10), and
// the native-memory candidate batches for import_knowledge. The importers were retired after the cutover;
// imported features and items keep what they came from. Serialized with WorkJson.Options (snake_case).

/// <summary>What an imported feature or item came from, and the source's history fields kept as history (not claims).</summary>
/// <param name="Archived">true once the source object was archived after it was imported (sc-27929).</param>
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

public sealed class MemoryFileInput
{
    public required string Path { get; init; }
    public required string Content { get; init; }
}

/// <summary>Where files stored with imported work items live (the codegraph-uploads volume in docker-compose).</summary>
public sealed class WorkAttachmentOptions
{
    public const string Section = "WorkAttachments";

    /// <summary>Relative to the host's working directory (/app in the container). Null or empty: next to the wiki's attachments.</summary>
    public string? StoragePath { get; set; } = "uploads/work-items";
}
