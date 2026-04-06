using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services;

/// <summary>
/// Shared git clone/fetch helpers for repo providers that cache repos locally.
/// </summary>
public abstract class RepoProviderBase(string reposCachePath, ILogger logger)
{
    /// <summary>
    /// Ensures a repo exists in the cache. Fetches if already cloned, otherwise clones.
    /// </summary>
    protected async Task<string> EnsureCachedAsync(string repoName, string? repoUrl,
        Func<string, string> toCloneUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reposCachePath))
            throw new InvalidOperationException(
                "RepositorySource:ReposCachePath is not configured.");

        var cachedPath = Path.Combine(reposCachePath, repoName);

        if (Directory.Exists(Path.Combine(cachedPath, ".git")))
        {
            logger.LogInformation("Fetching latest for {Repo} in {Path}", repoName, cachedPath);
            await FetchAsync(cachedPath, ct);
            return cachedPath;
        }

        if (string.IsNullOrWhiteSpace(repoUrl))
            throw new InvalidOperationException(
                $"No local path, cached repo, or remote URL available for '{repoName}'.");

        var cloneUrl = toCloneUrl(repoUrl);
        logger.LogInformation("Cloning {Repo} from {Url} into {Path}", repoName, cloneUrl, cachedPath);
        await CloneAsync(cloneUrl, cachedPath, ct);

        return cachedPath;
    }

    /// <summary>
    /// Provider-specific fetch logic (e.g., token URL rewriting).
    /// Default: git fetch + reset --hard origin/HEAD.
    /// </summary>
    protected virtual async Task FetchAsync(string repoPath, CancellationToken ct)
    {
        await RunGitAsync(repoPath, "fetch origin", ct);
        await RunGitAsync(repoPath, "reset --hard origin/HEAD", ct);
    }

    protected async Task CloneAsync(string cloneUrl, string targetPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await RunGitAsync(null, $"clone --single-branch \"{cloneUrl}\" \"{targetPath}\"", ct);
    }

    protected async Task<string> RunGitOutputAsync(string? workingDir, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {arguments}");

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return stdout;
    }

    protected async Task RunGitAsync(string? workingDir, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {arguments}");

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            logger.LogError("git {Args} failed (exit {Code}): {StdErr}", arguments, proc.ExitCode, stderr);
            throw new InvalidOperationException($"git {arguments} failed with exit code {proc.ExitCode}: {stderr}");
        }

        if (!string.IsNullOrWhiteSpace(stderr))
            logger.LogDebug("git {Args} stderr: {StdErr}", arguments, stderr);
    }
}
