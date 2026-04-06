using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Services.Configuration;

namespace TC.CodeGraphApi.Services;

/// <summary>
/// Resolves a file path within a repository, checking the GitLab cache first, then the local path.
/// </summary>
public static class RepoFileResolver
{
    /// <summary>
    /// Resolves the full filesystem path for a file in a repository.
    /// Checks cache first, then project local path.
    /// Returns null if the file cannot be found.
    /// </summary>
    public static string? Resolve(
        string repoName,
        string relativeFilePath,
        string? cachePath,
        string? localPath)
    {
        var normalized = relativeFilePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        // Cache first
        if (!string.IsNullOrWhiteSpace(cachePath))
        {
            var cached = Path.Combine(cachePath, repoName, normalized);
            if (File.Exists(cached))
                return cached;
        }

        // Fall back to local path
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            var local = Path.Combine(localPath, normalized);
            if (File.Exists(local))
                return local;
        }

        return null;
    }

    /// <summary>
    /// Resolves a file using project info from the store.
    /// </summary>
    public static async Task<string?> ResolveAsync(
        string repoName,
        string relativeFilePath,
        GitLabOptions gitLabOptions,
        IGraphStore store)
    {
        var repos = await store.ListRepositoriesAsync();
        var project = repos.FirstOrDefault(r =>
            r.Name.Equals(repoName, StringComparison.OrdinalIgnoreCase));

        return Resolve(
            repoName,
            relativeFilePath,
            gitLabOptions.ReposCachePath,
            project?.LocalPath);
    }
}
