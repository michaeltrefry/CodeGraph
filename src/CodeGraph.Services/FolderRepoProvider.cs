using System.Diagnostics;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services;

/// <summary>
/// Discovers repos by scanning a local directory for subdirectories that contain .git folders.
/// No cloning or fetching — repos are used in place. Optionally fetches if the repo has a remote.
/// </summary>
public class FolderRepoProvider(
    IOptions<RepositorySourceOptions> sourceOptionsAccessor,
    IExclusionService exclusionService,
    ILogger<FolderRepoProvider> logger)
    : IRepoProvider
{
    private readonly FolderSourceOptions _folder = sourceOptionsAccessor.Value.Folder;

    public async Task<List<DiscoveredProject>> DiscoverProjectsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_folder.RootPath) || !Directory.Exists(_folder.RootPath))
            throw new InvalidOperationException(
                $"RepositorySource:Folder:RootPath is not configured or does not exist: '{_folder.RootPath}'");

        var results = new List<DiscoveredProject>();
        var id = 0;

        foreach (var dir in EnumerateRepositoryDirectories(ct))
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(dir);
            var sourceGroup = GetFolderGroup(dir);

            var exclusionType = await exclusionService.GetExclusionTypeAsync(name, sourceGroup);
            if (exclusionType == "complete")
            {
                logger.LogDebug("Skipping {Project} (excluded: complete)", name);
                continue;
            }

            var lastWrite = Directory.GetLastWriteTimeUtc(dir);
            var defaultBranch = await GetDefaultBranchAsync(dir, ct);

            results.Add(new DiscoveredProject(
                ++id,
                name,
                sourceGroup is not null ? $"{sourceGroup}/{name}" : name,
                dir, // Use local path as the "URL" for folder provider
                defaultBranch,
                lastWrite));
        }

        logger.LogInformation("Discovered {Count} projects from folder {Path}", results.Count, _folder.RootPath);
        return results;
    }

    public async Task<List<DiscoveredProject>> SearchProjectsAsync(string searchTerm, CancellationToken ct = default)
    {
        var all = await DiscoverProjectsAsync(ct);
        return all
            .Where(p => p.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public Task<string> EnsureLocalAsync(string repoName, string? localPath, string? repoUrl, CancellationToken ct = default)
    {
        // If explicit local path provided and exists, use it
        if (!string.IsNullOrWhiteSpace(localPath) && Directory.Exists(localPath))
            return Task.FromResult(localPath);

        // Try the folder root
        var candidatePath = Path.Combine(_folder.RootPath, repoName);
        if (Directory.Exists(Path.Combine(candidatePath, ".git")))
            return Task.FromResult(candidatePath);

        var nestedMatch = TryFindRepositoryByName(repoName);
        if (nestedMatch is not null)
            return Task.FromResult(nestedMatch);

        // repoUrl may be a local path from discovery
        if (!string.IsNullOrWhiteSpace(repoUrl) && Directory.Exists(repoUrl))
            return Task.FromResult(repoUrl);

        throw new InvalidOperationException(
            $"Repository '{repoName}' not found in folder root '{_folder.RootPath}'.");
    }

    /// <summary>
    /// Extracts a group name from the folder structure.
    /// If repos are nested (e.g., /repos/group/repo), returns the intermediate path.
    /// For flat structures, returns null.
    /// </summary>
    private string? GetFolderGroup(string repoDir)
    {
        var relative = Path.GetRelativePath(_folder.RootPath, Path.GetDirectoryName(repoDir)!);
        return relative == "." ? null : relative.Replace('\\', '/');
    }

    private IEnumerable<string> EnumerateRepositoryDirectories(CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(_folder.RootPath);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var current = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();

                if (Directory.Exists(Path.Combine(child, ".git")))
                {
                    yield return child;
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private string? TryFindRepositoryByName(string repoName)
    {
        var matches = EnumerateRepositoryDirectories(CancellationToken.None)
            .Where(path => Path.GetFileName(path).Equals(repoName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Repository '{repoName}' is ambiguous under '{_folder.RootPath}'. Pass an explicit path instead.")
        };
    }

    private async Task<string> GetDefaultBranchAsync(string repoPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "symbolic-ref --short HEAD")
            {
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return "main";

            var branch = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var trimmed = branch.Trim();
            return proc.ExitCode == 0 && !string.IsNullOrEmpty(trimmed) ? trimmed : "main";
        }
        catch
        {
            return "main";
        }
    }
}
