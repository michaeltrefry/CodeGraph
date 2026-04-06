namespace CodeGraph.Services;

/// <summary>
/// A discovered project from a repository source provider.
/// </summary>
public record DiscoveredProject(
    int Id,
    string Name,
    string PathWithNamespace,
    string HttpUrlToRepo,
    string DefaultBranch,
    DateTime LastActivityAt);

/// <summary>
/// Resolves a local working directory for a repository, cloning from a remote source if necessary.
/// </summary>
public interface IRepoProvider
{
    /// <summary>
    /// Ensures a repo is available locally. If <paramref name="localPath"/> is set and exists, uses it directly.
    /// If <paramref name="repoUrl"/> is set, clones or fetches into the configured cache directory.
    /// </summary>
    /// <returns>The local filesystem path to the repo root.</returns>
    Task<string> EnsureLocalAsync(string repoName, string? localPath, string? repoUrl, CancellationToken ct = default);

    /// <summary>
    /// Discovers all projects visible to the configured credentials, excluding configured groups.
    /// </summary>
    Task<List<DiscoveredProject>> DiscoverProjectsAsync(CancellationToken ct = default);

    Task<List<DiscoveredProject>> SearchProjectsAsync(string searchTerm, CancellationToken ct = default);
}
