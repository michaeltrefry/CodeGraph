using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services;

/// <summary>
/// Shared git clone/fetch helpers for repo providers that cache repos locally. Credentials are
/// supplied per git command (<see cref="GitHttpCredential"/>) and never stored in the checkout's
/// remote URL, so a rotated token takes effect on the next fetch.
/// </summary>
public abstract class RepoProviderBase(string reposCachePath, ILogger logger, GitHttpCredential? credential = null)
{
    protected GitHttpCredential? Credential { get; } = credential;

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
        logger.LogInformation("Cloning {Repo} from {Url} into {Path}", repoName, Redact(cloneUrl), cachedPath);
        await CloneAsync(cloneUrl, cachedPath, ct);

        return cachedPath;
    }

    /// <summary>
    /// Normalizes the origin URL (<see cref="ToOriginUrl"/>), then git fetch + reset --hard to the
    /// remote's default branch. Normalizing on every fetch migrates checkouts whose origin still
    /// carries a token embedded by an earlier version.
    /// </summary>
    protected virtual async Task FetchAsync(string repoPath, CancellationToken ct)
    {
        var originUrl = await NormalizeOriginUrlAsync(repoPath, ct);
        await EnsureOriginFetchRefspecAsync(repoPath, ct);
        await RunGitAsync(repoPath, "fetch origin", ct, originUrl);
        await ResetToFetchedHeadAsync(repoPath, ct);
    }

    /// <summary>
    /// The credential-free origin URL a checkout should have. Default: <paramref name="currentUrl"/>
    /// without user-info.
    /// </summary>
    protected virtual string ToOriginUrl(string currentUrl) => GitRemoteUrl.StripUserInfo(currentUrl);

    /// <summary>Rewrites origin to <see cref="ToOriginUrl"/> when it differs; returns the origin URL.</summary>
    protected async Task<string> NormalizeOriginUrlAsync(string repoPath, CancellationToken ct)
    {
        var currentUrl = (await RunGitOutputAsync(repoPath, "remote get-url origin", ct)).Trim();
        if (string.IsNullOrEmpty(currentUrl))
            return currentUrl;

        var originUrl = ToOriginUrl(currentUrl);
        if (!string.Equals(originUrl, currentUrl, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Rewriting origin of {RepoPath} from {OldUrl} to {NewUrl}",
                repoPath, Redact(currentUrl), Redact(originUrl));
            await RunGitAsync(repoPath, $"remote set-url origin \"{originUrl}\"", ct);
        }

        return originUrl;
    }

    protected async Task CloneAsync(string cloneUrl, string targetPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await RunGitAsync(null, $"clone --single-branch \"{cloneUrl}\" \"{targetPath}\"", ct, cloneUrl);
    }

    protected string Redact(string text) => GitHttpCredential.Redact(text, Credential);

    /// <summary>
    /// Process settings for one git command: no interactive prompts, and the credential for
    /// <paramref name="remoteUrl"/> passed through the environment rather than the arguments.
    /// </summary>
    internal ProcessStartInfo CreateGitStartInfo(string? workingDir, string arguments, string? remoteUrl)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (Credential is not null && remoteUrl is not null)
        {
            foreach (var (key, value) in Credential.EnvironmentFor(remoteUrl))
                psi.Environment[key] = value;
        }

        return psi;
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

    protected async Task RunGitAsync(string? workingDir, string arguments, CancellationToken ct, string? remoteUrl = null)
    {
        var psi = CreateGitStartInfo(workingDir, arguments, remoteUrl);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {Redact(arguments)}");

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            var safeArguments = Redact(arguments);
            var safeStderr = Redact(stderr);
            logger.LogError("git {Args} failed (exit {Code}): {StdErr}", safeArguments, proc.ExitCode, safeStderr);
            throw new InvalidOperationException($"git {safeArguments} failed with exit code {proc.ExitCode}: {safeStderr}");
        }

        if (!string.IsNullOrWhiteSpace(stderr))
            logger.LogDebug("git {Args} stderr: {StdErr}", Redact(arguments), Redact(stderr));
    }

    protected async Task ResetToFetchedHeadAsync(string repoPath, CancellationToken ct)
    {
        var remoteHeadRef = (await RunGitOutputAsync(
            repoPath,
            "symbolic-ref --quiet --short refs/remotes/origin/HEAD",
            ct)).Trim();

        if (!string.IsNullOrWhiteSpace(remoteHeadRef))
        {
            await RunGitAsync(repoPath, $"reset --hard {remoteHeadRef}", ct);
            return;
        }

        var currentBranch = (await RunGitOutputAsync(
            repoPath,
            "branch --show-current",
            ct)).Trim();

        if (!string.IsNullOrWhiteSpace(currentBranch))
        {
            var remoteBranchRef = $"origin/{currentBranch}";
            if (await RefExistsAsync(repoPath, remoteBranchRef, ct))
            {
                await RunGitAsync(repoPath, $"reset --hard {remoteBranchRef}", ct);
                return;
            }
        }

        foreach (var candidate in new[] { "origin/main", "origin/master" })
        {
            if (await RefExistsAsync(repoPath, candidate, ct))
            {
                await RunGitAsync(repoPath, $"reset --hard {candidate}", ct);
                return;
            }
        }

        if (!await RepositoryHasAnyCommitsAsync(repoPath, ct))
        {
            logger.LogInformation(
                "Repository at {RepoPath} has no fetched commits yet; skipping reset to fetched HEAD",
                repoPath);
            return;
        }

        throw new InvalidOperationException(
            $"Unable to determine fetched HEAD for repository at '{repoPath}'.");
    }

    private async Task<bool> RepositoryHasAnyCommitsAsync(string repoPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", "rev-parse --verify HEAD")
        {
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git rev-parse --verify HEAD");

        await proc.WaitForExitAsync(ct);
        return proc.ExitCode == 0;
    }

    protected async Task EnsureOriginFetchRefspecAsync(string repoPath, CancellationToken ct)
    {
        var existing = (await RunGitOutputAsync(
            repoPath,
            "config --get-all remote.origin.fetch",
            ct)).Trim();

        if (!string.IsNullOrWhiteSpace(existing))
            return;

        const string defaultRefspec = "+refs/heads/*:refs/remotes/origin/*";
        logger.LogInformation(
            "Repository at {RepoPath} is missing remote.origin.fetch; restoring default fetch refspec",
            repoPath);
        await RunGitAsync(repoPath, $"config remote.origin.fetch \"{defaultRefspec}\"", ct);
    }

    private async Task<bool> RefExistsAsync(string repoPath, string gitRef, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", $"rev-parse --verify {gitRef}")
        {
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git rev-parse --verify {gitRef}");

        await proc.WaitForExitAsync(ct);
        return proc.ExitCode == 0;
    }
}
