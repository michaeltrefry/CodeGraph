using System.Diagnostics;
using System.Formats.Tar;
using System.Text;

namespace CodeGraph.Services.Overlays;

public enum OverlayGitChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed
}

/// <summary>One path-level change between the base and head commits.</summary>
public sealed record OverlayGitChange(OverlayGitChangeKind Kind, string Path, string? PreviousPath = null);

/// <summary>
/// Git plumbing for branch overlays. Every command reads objects only: the shared checkout's
/// working tree, index, and branches are never modified, so default-branch indexing of the
/// same checkout is unaffected. Arguments are passed as an argument vector (no shell).
/// </summary>
public class BranchOverlayGit
{
    private const int ArchiveBatchSize = 200;

    public static void ValidateRef(string gitRef)
    {
        if (string.IsNullOrWhiteSpace(gitRef))
            throw new ArgumentException("A branch, tag, or commit SHA is required.", nameof(gitRef));
        if (gitRef.Length > 255)
            throw new ArgumentException("Ref must be 255 characters or fewer.", nameof(gitRef));
        if (gitRef.StartsWith('-') || gitRef.Contains("..", StringComparison.Ordinal)
            || gitRef.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new ArgumentException($"Ref '{gitRef}' is not a valid branch, tag, or commit SHA.", nameof(gitRef));
        }
    }

    /// <summary>
    /// Resolves <paramref name="gitRef"/> to a commit SHA, fetching it from <c>origin</c> first when
    /// the checkout has a remote (provider checkouts are single-branch clones, so other branches
    /// such as <c>factory/*</c> are only reachable by an explicit fetch). Falls back to local refs.
    /// </summary>
    public virtual async Task<string> ResolveCommitAsync(string repoPath, string gitRef, CancellationToken ct)
    {
        ValidateRef(gitRef);
        string? fetchError = null;
        if (await HasOriginAsync(repoPath, ct))
        {
            var fetch = await RunAsync(repoPath, ["fetch", "--no-tags", "--no-write-fetch-head", "origin",
                $"+{gitRef}:{OverlayFetchRef}"], ct);
            if (fetch.ExitCode == 0)
            {
                var fetched = await TryRevParseCommitAsync(repoPath, OverlayFetchRef, ct);
                if (fetched is not null)
                    return fetched;
            }

            fetchError = fetch.StdErrText.Trim();
        }

        foreach (var candidate in new[] { gitRef, $"origin/{gitRef}" })
        {
            var sha = await TryRevParseCommitAsync(repoPath, candidate, ct);
            if (sha is not null)
                return sha;
        }

        throw new InvalidOperationException(
            $"Ref '{gitRef}' could not be resolved to a commit" +
            (string.IsNullOrWhiteSpace(fetchError) ? "." : $" (fetch failed: {fetchError})."));
    }

    /// <summary>Ensures <paramref name="sha"/> exists locally, fetching it from origin when missing.</summary>
    public virtual async Task EnsureCommitAsync(string repoPath, string sha, CancellationToken ct)
    {
        if (await TryRevParseCommitAsync(repoPath, sha, ct) is not null)
            return;

        if (await HasOriginAsync(repoPath, ct))
        {
            await RunAsync(repoPath, ["fetch", "--no-tags", "--no-write-fetch-head", "origin", sha], ct);
            if (await TryRevParseCommitAsync(repoPath, sha, ct) is not null)
                return;
        }

        throw new InvalidOperationException(
            $"Base commit '{sha}' of the default-branch index is not available in the repository checkout.");
    }

    /// <summary>Path-level changes from <paramref name="baseSha"/> to <paramref name="headSha"/> with rename detection.</summary>
    public virtual async Task<IReadOnlyList<OverlayGitChange>> DiffAsync(
        string repoPath,
        string baseSha,
        string headSha,
        CancellationToken ct)
    {
        var result = await RunCheckedAsync(repoPath,
            ["diff", "--no-color", "--no-ext-diff", "--name-status", "-z", "-M", baseSha, headSha, "--"], ct);
        return ParseNameStatus(result.StdOut);
    }

    /// <summary>All file paths in the head tree.</summary>
    public virtual async Task<IReadOnlyList<string>> ListFilesAsync(string repoPath, string headSha, CancellationToken ct)
    {
        var result = await RunCheckedAsync(repoPath, ["ls-tree", "-r", "-z", "--name-only", headSha], ct);
        return SplitNul(result.StdOut);
    }

    /// <summary>Writes the head-commit content of <paramref name="paths"/> under <paramref name="targetRoot"/>.</summary>
    public virtual async Task MaterializeAsync(
        string repoPath,
        string headSha,
        IReadOnlyCollection<string> paths,
        string targetRoot,
        CancellationToken ct)
    {
        Directory.CreateDirectory(targetRoot);
        foreach (var batch in paths.Chunk(ArchiveBatchSize))
        {
            var args = new List<string> { "--literal-pathspecs", "archive", "--format=tar", headSha, "--" };
            args.AddRange(batch);
            var result = await RunCheckedAsync(repoPath, args, ct);
            using var tar = new MemoryStream(result.StdOut);
            await TarFile.ExtractToDirectoryAsync(tar, targetRoot, overwriteFiles: true, ct);
        }
    }

    internal static IReadOnlyList<OverlayGitChange> ParseNameStatus(byte[] output)
    {
        var tokens = SplitNul(output);
        var changes = new List<OverlayGitChange>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var status = tokens[i];
            if (status.Length == 0)
                continue;

            switch (status[0])
            {
                case 'R':
                    changes.Add(new OverlayGitChange(OverlayGitChangeKind.Renamed, tokens[i + 2], tokens[i + 1]));
                    i += 2;
                    break;
                case 'C':
                    changes.Add(new OverlayGitChange(OverlayGitChangeKind.Added, tokens[i + 2]));
                    i += 2;
                    break;
                case 'A':
                    changes.Add(new OverlayGitChange(OverlayGitChangeKind.Added, tokens[++i]));
                    break;
                case 'D':
                    changes.Add(new OverlayGitChange(OverlayGitChangeKind.Deleted, tokens[++i]));
                    break;
                default:
                    // M (modified), T (type change), U/X (unexpected) are all content changes.
                    changes.Add(new OverlayGitChange(OverlayGitChangeKind.Modified, tokens[++i]));
                    break;
            }
        }

        return changes;
    }

    private const string OverlayFetchRef = "refs/codegraph/overlay-head";

    private static List<string> SplitNul(byte[] output)
        => Encoding.UTF8.GetString(output)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    private async Task<bool> HasOriginAsync(string repoPath, CancellationToken ct)
        => (await RunAsync(repoPath, ["remote", "get-url", "origin"], ct)).ExitCode == 0;

    private async Task<string?> TryRevParseCommitAsync(string repoPath, string rev, CancellationToken ct)
    {
        var result = await RunAsync(repoPath, ["rev-parse", "--verify", "--quiet", "--end-of-options", $"{rev}^{{commit}}"], ct);
        if (result.ExitCode != 0)
            return null;

        var sha = Encoding.UTF8.GetString(result.StdOut).Trim();
        return sha.Length == 40 ? sha : null;
    }

    private async Task<GitResult> RunCheckedAsync(string repoPath, IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await RunAsync(repoPath, args, ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {args.FirstOrDefault(arg => !arg.StartsWith('-'))} failed with exit code {result.ExitCode}: {result.StdErrText.Trim()}");
        }

        return result;
    }

    protected virtual async Task<GitResult> RunAsync(string repoPath, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git.");
        using var stdout = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        return new GitResult(process.ExitCode, stdout.ToArray(), await stderrTask);
    }

    protected sealed record GitResult(int ExitCode, byte[] StdOut, string StdErrText);
}
