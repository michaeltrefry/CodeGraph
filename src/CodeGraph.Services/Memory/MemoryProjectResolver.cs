using CodeGraph.Data;
using CodeGraph.Models.Work;

namespace CodeGraph.Services.Memory;

/// <summary>
/// The work project a memory write is scoped to (CG-156): the explicit project when one is named,
/// else the project of the writing session. A session's project is the one start_session resolved:
/// the project it named, or the single project owning the session's repository.
/// </summary>
public interface IMemoryProjectResolver
{
    /// <returns>The project's key, or null when neither a project nor a session is given.</returns>
    /// <exception cref="MemoryProjectResolutionException">The project or session names nothing.</exception>
    Task<string?> ResolveAsync(string? project, string? sessionId, CancellationToken ct = default);
}

public sealed class MemoryProjectResolutionException(string message) : Exception(message);

public sealed class WorkMemoryProjectResolver(IWorkStore? store = null) : IMemoryProjectResolver
{
    public async Task<string?> ResolveAsync(string? project, string? sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(project) && string.IsNullOrWhiteSpace(sessionId))
            return null;
        if (store is null)
            throw new MemoryProjectResolutionException("Scoping memory to a project needs work tracking, which this host does not have.");

        if (!string.IsNullOrWhiteSpace(project))
        {
            var target = await store.ResolveAsync(project.Trim(), ct);
            var view = target is { Kind: WorkEntityKind.Project } ? await store.GetProjectAsync(target.Id, ct) : null;
            return view?.Key
                ?? throw new MemoryProjectResolutionException($"project '{project.Trim()}' is not the key (such as SW) or id of a project.");
        }

        var session = Guid.TryParse(sessionId!.Trim(), out var id) ? await store.GetSessionAsync(id, ct) : null;
        return session?.ProjectKey
            ?? throw new MemoryProjectResolutionException($"sessionId '{sessionId!.Trim()}' is not a session; pass the id start_session answered.");
    }
}
