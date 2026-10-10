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

public enum CommitFileReadStatus
{
    Found,

    /// <summary>The commit exists but has no file at the path.</summary>
    PathMissing,

    /// <summary>The commit is not present in the repository checkout.</summary>
    CommitUnavailable
}

public sealed record CommitFileReadResult(CommitFileReadStatus Status, byte[]? Content);

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

    /// <summary>Upper bound on <c>git ls-remote</c> when resolving a requested ref.</summary>
    internal TimeSpan RemoteRefTimeout { get; init; } = TimeSpan.FromSeconds(30);

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

    /// <summary>
    /// The commit <paramref name="gitRef"/> points at right now, without fetching objects or
    /// writing any ref: a full SHA is returned as is (lowercase); otherwise <c>git ls-remote origin</c>
    /// is asked for the ref and the first match in git's DWIM order (<c>ref</c>, <c>refs/ref</c>,
    /// <c>refs/tags/ref</c>, <c>refs/heads/ref</c>, <c>refs/remotes/ref</c>, <c>refs/remotes/ref/HEAD</c>)
    /// is used, which is the ref <see cref="ResolveCommitAsync"/> would fetch. An annotated tag
    /// resolves to its peeled commit. Without an <c>origin</c> remote the local ref is used.
    /// Returns null when the ref does not exist.
    /// </summary>
    /// <exception cref="BranchOverlayTransientException">ls-remote failed, for example on the network.</exception>
    public virtual async Task<string?> ResolveRemoteRefAsync(string repoPath, string gitRef, CancellationToken ct)
    {
        ValidateRef(gitRef);
        if (IsFullSha(gitRef))
            return gitRef.ToLowerInvariant();

        if (!await HasOriginAsync(repoPath, ct))
            return await TryRevParseCommitAsync(repoPath, gitRef, ct);

        // The second pattern brings in the peeled entry of an annotated tag. ls-remote runs on the
        // request path, so an unresponsive origin is bounded rather than holding the request open.
        GitResult result;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(RemoteRefTimeout);
            try
            {
                result = await RunAsync(repoPath, ["ls-remote", "origin", gitRef, gitRef + "^{}"], timeout.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new BranchOverlayTransientException(
                    $"Listing ref '{gitRef}' on origin timed out after {RemoteRefTimeout.TotalSeconds:0} seconds.", ex);
            }
        }

        if (result.ExitCode != 0)
        {
            throw new BranchOverlayTransientException(
                $"Listing ref '{gitRef}' on origin failed: {result.StdErrText.Trim()}");
        }

        var advertised = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Encoding.UTF8.GetString(result.StdOut).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t', 2);
            if (parts.Length == 2 && IsFullSha(parts[0]))
                advertised[parts[1]] = parts[0].ToLowerInvariant();
        }

        string[] candidates =
        [
            gitRef,
            $"refs/{gitRef}",
            $"refs/tags/{gitRef}",
            $"refs/heads/{gitRef}",
            $"refs/remotes/{gitRef}",
            $"refs/remotes/{gitRef}/HEAD"
        ];
        foreach (var candidate in candidates)
        {
            if (advertised.TryGetValue(candidate + "^{}", out var peeled))
                return peeled;
            if (advertised.TryGetValue(candidate, out var sha))
                return sha;
        }

        return null;
    }

    /// <summary>
    /// Makes <paramref name="pinnedSha"/> (the head <paramref name="gitRef"/> had when the overlay
    /// was requested) available locally and returns it. The requested ref is fetched first, which
    /// brings the commit in even when the branch has moved on since; when the ref is gone the
    /// commit itself is fetched.
    /// </summary>
    /// <exception cref="InvalidOperationException">The commit is not available (deterministic).</exception>
    /// <exception cref="BranchOverlayTransientException">A fetch failed for another reason, such as the network.</exception>
    public virtual async Task<string> ResolvePinnedCommitAsync(string repoPath, string gitRef, string pinnedSha, CancellationToken ct)
    {
        if (await TryRevParseCommitAsync(repoPath, pinnedSha, ct) is { } local)
            return local;

        try
        {
            await ResolveCommitAsync(repoPath, gitRef, ct);
        }
        catch (InvalidOperationException)
        {
            // The ref no longer exists on origin; try the commit itself below.
        }

        if (await TryRevParseCommitAsync(repoPath, pinnedSha, ct) is { } fetched)
            return fetched;

        try
        {
            return await ResolveCommitAsync(repoPath, pinnedSha, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Commit {pinnedSha}, the head of '{gitRef}' when the overlay was requested, is no longer available: {ex.Message}", ex);
        }
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

    /// <summary>
    /// Reads the content of <paramref name="relativePath"/> as committed at <paramref name="sha"/>
    /// (<c>cat-file blob sha:path</c>). The working tree and index are never read. Nothing is
    /// fetched: a commit that is not in the checkout reports <see cref="CommitFileReadStatus.CommitUnavailable"/>.
    /// </summary>
    public virtual async Task<CommitFileReadResult> ReadFileAtCommitAsync(
        string repoPath,
        string sha,
        string relativePath,
        CancellationToken ct)
    {
        if (!IsFullSha(sha))
            throw new ArgumentException("A full commit SHA is required.", nameof(sha));

        var path = relativePath.Replace('\\', '/');
        if (await TryRevParseCommitAsync(repoPath, sha, ct) is null)
            return new CommitFileReadResult(CommitFileReadStatus.CommitUnavailable, null);

        var result = await RunAsync(repoPath, ["cat-file", "blob", $"{sha}:{path}"], ct);
        return result.ExitCode == 0
            ? new CommitFileReadResult(CommitFileReadStatus.Found, result.StdOut)
            : new CommitFileReadResult(CommitFileReadStatus.PathMissing, null);
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
