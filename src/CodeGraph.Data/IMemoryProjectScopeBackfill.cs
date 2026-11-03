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
    /// <param name="projectKey">The project's key.</param>
    /// <param name="repositories">The project's member repositories (any role); names shorter than 3 characters are skipped.</param>
    Task BackfillAsync(string projectKey, IReadOnlyCollection<string> repositories, CancellationToken ct = default);
}
