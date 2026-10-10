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
/// A git operation failed for a reason that may not repeat (network, remote unavailable), so
/// the durable indexer worker is allowed to retry the overlay run.
/// </summary>
public sealed class BranchOverlayTransientException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Git plumbing for branch overlays. These commands never modify the shared checkout's working
/// tree, index, or branches: they only fetch objects into <c>refs/codegraph/overlay-head</c> and
/// read commits/trees, so the overlay head always comes from that ref (or a commit SHA), never
/// from whatever the working tree currently holds. Note that the repo provider's sync step,
/// which <see cref="BranchOverlayService"/> runs before these commands, does fetch and reset the
/// checkout to the remote default branch; that runs under the project indexing lock and is the
/// same resync default-branch indexing performs. Arguments are passed as an argument vector (no shell).
/// </summary>
public class BranchOverlayGit
{
    private const int ArchiveBatchSize = 200;

    /// <summary>
    /// Accepts a full commit SHA (40 or 64 hex) or a ref name that satisfies
    /// <c>git check-ref-format --allow-onelevel</c>. Revision syntax (<c>^ ~ : @{ * ? [ \</c>),
    /// ranges, option-like values, whitespace, and control characters are rejected.
    /// </summary>
    public static void ValidateRef(string gitRef)
    {
        if (string.IsNullOrWhiteSpace(gitRef))
            throw new ArgumentException("A branch, tag, or commit SHA is required.", nameof(gitRef));
        if (gitRef.Length > 255)
            throw new ArgumentException("Ref must be 255 characters or fewer.", nameof(gitRef));
        if (IsFullSha(gitRef))
            return;
        if (!IsValidRefName(gitRef))
            throw new ArgumentException($"Ref '{gitRef}' is not a valid branch, tag, or commit SHA.", nameof(gitRef));
    }

    public static bool IsFullSha(string value)
        => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);

    private static bool IsValidRefName(string name)
    {
        if (name == "@" || name.StartsWith('-') || name.EndsWith('.')
            || name.Contains("..", StringComparison.Ordinal) || name.Contains("@{", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)
                || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
            {
                return false;
            }
        }

        // Empty components reject a leading or trailing '/' and '//'.
        return name.Split('/').All(component =>
            component.Length > 0
            && !component.StartsWith('.')
            && !component.EndsWith(".lock", StringComparison.Ordinal));
    }

    /// <summary>
    /// Resolves <paramref name="gitRef"/> to a commit SHA. A full SHA already present locally is
    /// used as is. Otherwise, when the checkout has an <c>origin</c> remote, the ref is fetched
    /// into <c>refs/codegraph/overlay-head</c> (provider checkouts are single-branch clones, so
    /// branches such as <c>factory/*</c> are only reachable by an explicit fetch) and a failed
    /// fetch fails the resolution: local refs could be stale. Local refs are used only when the
    /// checkout has no <c>origin</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The ref does not exist (deterministic).</exception>
    /// <exception cref="BranchOverlayTransientException">The fetch failed for another reason, such as the network.</exception>
    public virtual async Task<string> ResolveCommitAsync(string repoPath, string gitRef, CancellationToken ct)
    {
        ValidateRef(gitRef);
        if (IsFullSha(gitRef) && await TryRevParseCommitAsync(repoPath, gitRef, ct) is { } localSha)
            return localSha;

        if (await HasOriginAsync(repoPath, ct))
        {
            var fetch = await RunAsync(repoPath, ["fetch", "--no-tags", "--no-write-fetch-head", "origin",
                $"+{gitRef}:{OverlayFetchRef}"], ct);
            if (fetch.ExitCode == 0
                && await TryRevParseCommitAsync(repoPath, OverlayFetchRef, ct) is { } fetched)
            {
                return fetched;
            }

            var fetchError = fetch.StdErrText.Trim();
            if (fetch.ExitCode != 0 && !IsMissingRemoteObject(fetchError))
            {
                throw new BranchOverlayTransientException(
                    $"Fetching ref '{gitRef}' from origin failed: {fetchError}");
            }

            throw new InvalidOperationException(
                $"Ref '{gitRef}' could not be resolved to a commit" +
                (string.IsNullOrWhiteSpace(fetchError) ? "." : $" (fetch failed: {fetchError})."));
        }

        return await TryRevParseCommitAsync(repoPath, gitRef, ct)
               ?? throw new InvalidOperationException($"Ref '{gitRef}' could not be resolved to a commit.");
    }

    /// <summary>Ensures <paramref name="sha"/> exists locally, fetching it from origin when missing.</summary>
    /// <exception cref="InvalidOperationException">The commit is not available (deterministic).</exception>
    /// <exception cref="BranchOverlayTransientException">The fetch failed for another reason, such as the network.</exception>
    public virtual async Task EnsureCommitAsync(string repoPath, string sha, CancellationToken ct)
    {
        if (await TryRevParseCommitAsync(repoPath, sha, ct) is not null)
            return;

        if (await HasOriginAsync(repoPath, ct))
        {
            var fetch = await RunAsync(repoPath, ["fetch", "--no-tags", "--no-write-fetch-head", "origin", sha], ct);
            if (await TryRevParseCommitAsync(repoPath, sha, ct) is not null)
                return;

            var fetchError = fetch.StdErrText.Trim();
            if (fetch.ExitCode != 0 && !IsMissingRemoteObject(fetchError))
            {
                throw new BranchOverlayTransientException(
                    $"Fetching base commit '{sha}' from origin failed: {fetchError}");
            }
        }

        throw new InvalidOperationException(
            $"Base commit '{sha}' of the default-branch index is not available in the repository checkout.");
    }

    /// <summary>
    /// True when git reports that the remote does not have the requested ref/object, which is a
    /// property of the request rather than of the connection.
    /// </summary>
    private static bool IsMissingRemoteObject(string fetchError)
        => fetchError.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase)
           || fetchError.Contains("not our ref", StringComparison.OrdinalIgnoreCase)
           || fetchError.Contains("unadvertised object", StringComparison.OrdinalIgnoreCase)
           || fetchError.Contains("no such remote ref", StringComparison.OrdinalIgnoreCase);

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
