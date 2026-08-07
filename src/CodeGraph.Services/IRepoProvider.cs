namespace CodeGraph.Services;

public sealed record ResolvedRepository(
    string LocalPath,
    string CanonicalIdentity,
    string? RepoUrl,
    string? SourceGroup);

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
    /// Resolves the provider-owned checkout and returns the canonical identity derived from that
    /// resolution. Callers must use this identity for security decisions instead of message fields.
    /// </summary>
    Task<ResolvedRepository> ResolveRepositoryAsync(
        string repoName,
        string? localPath,
        string? repoUrl,
        CancellationToken ct = default);

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

internal static class RepositoryIdentity
{
    public static (string CanonicalIdentity, string? SourceGroup) FromRemote(
        string provider,
        string repoName,
        string repoUrl)
    {
        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"{provider} repository URL must be an absolute HTTP(S) URL to establish a trusted identity.");
        }

        var path = uri.AbsolutePath.Trim('/');
        var pathWithoutGit = path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? path[..^4]
            : path;
        var segments = pathWithoutGit.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !segments[^1].Equals(repoName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Repository name '{repoName}' is inconsistent with resolved {provider} URL '{repoUrl}'.");
        }

        var authority = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/').ToLowerInvariant();
        var canonicalUrl = $"{authority}/{string.Join('/', segments)}";
        var sourceGroup = string.Join('/', segments[..^1]);
        return ($"{provider.ToLowerInvariant()}:{canonicalUrl}", sourceGroup);
    }
}
