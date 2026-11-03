using CodeGraph.Models.Memory;

namespace CodeGraph.Data;

/// <summary>
/// Scopes existing memory to a project by the repositories it names (CG-167), with migration 114's
/// rule: run when a project is created or gains a member repository, so memory written before it
/// lists under the project. Covers every user's memory; only adds missing (row, project) pairs with
/// origin backfill, so an existing scope (a write's) is never removed or downgraded and a replay
/// changes nothing.
/// </summary>
public interface IMemoryProjectScopeBackfill
{
    /// <summary>Scans all memory for the given repositories of one project (a project's create, a repository added, or a pending repair).</summary>
    /// <param name="projectKey">The project's key.</param>
    /// <param name="repositories">The project's member repositories (any role); names shorter than 3 characters are skipped.</param>
    Task BackfillAsync(string projectKey, IReadOnlyCollection<string> repositories, CancellationToken ct = default);

    /// <summary>
    /// The memory watermark (CG-172). Without <paramref name="since"/>, the current highest row ids, read
    /// without scanning. With it, the highest ids above it of rows written long enough ago that every
    /// write with a lower id has committed; a table with no such row keeps its mark from
    /// <paramref name="since"/>, so an unchanged memory answers <paramref name="since"/> itself.
    /// </summary>
    Task<MemoryScopeWatermark> GetWatermarkAsync(MemoryScopeWatermark? since, CancellationToken ct = default);

    /// <summary>
    /// Scopes the memory written after <paramref name="after"/> and up to <paramref name="upTo"/> (by row
    /// id, per table) to every project whose repositories it names, with <see cref="BackfillAsync"/>'s rule
    /// restricted to those rows (CG-172): no row at or below <paramref name="after"/> is scanned.
    /// </summary>
    Task BackfillRangeAsync(IReadOnlyList<ProjectRepositories> projects, MemoryScopeWatermark after, MemoryScopeWatermark upTo, CancellationToken ct = default);
}
