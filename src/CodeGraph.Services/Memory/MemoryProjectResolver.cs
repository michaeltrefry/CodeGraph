using System.Security.Claims;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;

namespace CodeGraph.Services.Memory;

/// <summary>
/// The work project a memory write is scoped to (CG-156): the explicit project when one is named,
/// else the project of the writing session. A session's project is the one start_session resolved:
/// the project it named, or the single project owning the session's repository. A write naming
/// neither takes the project of the caller's live sessions when they all share one (CG-166); with no
/// live session, or live sessions in several projects, it stays unscoped.
/// </summary>
public interface IMemoryProjectResolver
{
    /// <param name="caller">The identity making the write (see <see cref="MemoryWriteCaller.Of"/>), or null when unknown.</param>
    /// <returns>The project's key, or null when neither a project nor a session is given and the caller's live sessions are not all in one project.</returns>
    /// <exception cref="MemoryProjectResolutionException">The project or session names nothing.</exception>
    Task<string?> ResolveAsync(string? project, string? sessionId, WorkActor? caller = null, CancellationToken ct = default);
}

public sealed class MemoryProjectResolutionException(string message) : Exception(message);

/// <summary>The work identity of a memory write's caller, as the work tools attribute their writes.</summary>
public static class MemoryWriteCaller
{
    /// <returns>The caller's identity, or null for a request that names no user.</returns>
    public static WorkActor? Of(ClaimsPrincipal? user)
    {
        var actor = WorkService.ActorFor(user);
        return actor.Name == "anonymous" ? null : actor;
    }
}

public sealed class WorkMemoryProjectResolver(IWorkStore? store = null) : IMemoryProjectResolver
{
    public async Task<string?> ResolveAsync(string? project, string? sessionId, WorkActor? caller = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(project) && string.IsNullOrWhiteSpace(sessionId))
        {
            // Inferred, never refused: without work tracking or a known caller the write stays unscoped.
            if (store is null || caller is null)
                return null;
            try
            {
                var projects = (await store.ListLiveSessionsOfIdentityAsync(caller, limit: 1000, ct))
                    .Select(session => session.ProjectKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return projects.Count == 1 ? projects[0] : null;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The work store is unreachable (or refuses, as in a dream scope): the write stays unscoped.
                return null;
            }
        }
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
