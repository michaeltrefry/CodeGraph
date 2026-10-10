using System.Text;
using CodeGraph.Data;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Overlays;

/// <summary>
/// The graph a read query answers from. Without a requested SHA it is the default-branch index
/// (<see cref="Sha"/> is null and each answer names its project's index commit). With a SHA it is
/// pinned: either the ready branch overlay for that commit layered over the default-branch index,
/// or, when the SHA is the default-branch index commit itself, that index. When the SHA cannot be
/// answered, <see cref="NotIndexed"/> says why and <see cref="Graph"/> must not be used.
/// </summary>
public sealed class CommitScope
{
    private CommitScope(IGraphReader graph, string? project, string? sha, OverlayGraphView? overlay, CommitNotIndexedResponse? notIndexed)
    {
        Graph = graph;
        Project = project;
        Sha = sha;
        Overlay = overlay;
        NotIndexed = notIndexed;
    }

    public IGraphReader Graph { get; }

    /// <summary>The repository the SHA belongs to (pinned scopes), or the requested project.</summary>
    public string? Project { get; }

    /// <summary>The commit every answer from this scope describes; null for the unpinned default branch.</summary>
    public string? Sha { get; }

    public OverlayGraphView? Overlay { get; }

    public CommitNotIndexedResponse? NotIndexed { get; }

    public bool IsPinned => Sha is not null;

    internal static CommitScope DefaultBranch(IGraphReader graph, string? project) => new(graph, project, null, null, null);

    internal static CommitScope PinnedToDefault(IGraphReader graph, string project, string sha) => new(graph, project, sha, null, null);

    internal static CommitScope PinnedToOverlay(OverlayGraphView overlay, string sha) => new(overlay, overlay.Project, sha, overlay, null);

    internal static CommitScope Unavailable(IGraphReader graph, CommitNotIndexedResponse notIndexed) => new(graph, notIndexed.Repo, null, null, notIndexed);
}

/// <summary>The commit(s) an answer describes.</summary>
public sealed record CommitDescription(string? CommitSha, IReadOnlyDictionary<string, string?> Commits)
{
    /// <summary>
    /// <c>Commit: &lt;sha&gt;</c>; <c>Commit: unknown</c> when no indexed commit is known; and for an
    /// unpinned answer spanning several repositories <c>Commit: mixed (repo sha, ...)</c>.
    /// </summary>
    public string HeaderLine
    {
        get
        {
            if (CommitSha is not null)
                return $"Commit: {CommitSha}";
            if (Commits.Count > 1)
                return "Commit: mixed (" + string.Join(", ", Commits
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => $"{pair.Key} {pair.Value ?? "unknown"}")) + ")";
            return "Commit: unknown";
        }
    }
}

public enum CommitSourceStatus
{
    Found,
    NotFound,

    /// <summary>The path was deleted or renamed away at the pinned commit.</summary>
    DeletedAtCommit,

    /// <summary>The pinned commit's objects are not in this host's repository checkout.</summary>
    CommitUnavailable,

    /// <summary>The file belongs to a repository other than the one the pinned SHA belongs to.</summary>
    OutsideScope
}

public sealed record CommitSourceResult(CommitSourceStatus Status, string? Content, string Message);

public interface ICommitScopeResolver
{
    /// <summary>
    /// Resolves the graph for <paramref name="sha"/> (a full 40/64-hex SHA; abbreviated SHAs are
    /// rejected) in <paramref name="project"/>. A null SHA is the default branch. With a SHA and no
    /// project, the repository is the one whose overlays carry the SHA, or else the one whose
    /// default-branch index is at the SHA.
    /// </summary>
    Task<CommitScope> ResolveAsync(string? project, string? sha, CancellationToken ct = default);

    /// <summary>The commit an answer from <paramref name="scope"/> about <paramref name="projects"/> describes.</summary>
    Task<CommitDescription> DescribeAsync(CommitScope scope, IEnumerable<string?> projects, CancellationToken ct = default);

    /// <summary>
    /// Source of <paramref name="filePath"/> in <paramref name="project"/>: the blob at the pinned
    /// commit (never the working tree), or for the unpinned default branch the blob at the
    /// default-branch index commit, falling back to the working tree only when that commit is not
    /// in git on this host (e.g. a non-git folder source).
    /// </summary>
    Task<CommitSourceResult> ReadSourceAsync(CommitScope scope, string project, string filePath, CancellationToken ct = default);

    /// <summary>The commit of <paramref name="project"/>'s default-branch index.</summary>
    Task<string?> GetDefaultCommitAsync(string project, CancellationToken ct = default);
}

public sealed class CommitScopeResolver(
    IGraphStore store,
    IOptions<RepositorySourceOptions> sourceOptions,
    IBranchOverlayStore? overlayStore = null,
    BranchOverlayGit? git = null,
    IOptions<BranchOverlayOptions>? overlayOptions = null,
    TimeProvider? timeProvider = null) : ICommitScopeResolver
{
    public const string NotIndexedCode = "commit_not_indexed";
    public const string InvalidShaCode = "invalid_sha";
    public const string AmbiguousShaCode = "ambiguous_sha";
    public const string StaleStatus = "stale";
    public const string UnknownStatus = "unknown";

    private readonly BranchOverlayGit _git = git ?? new BranchOverlayGit();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private TimeSpan Ttl => (overlayOptions?.Value ?? new BranchOverlayOptions()).Ttl;

    public async Task<CommitScope> ResolveAsync(string? project, string? sha, CancellationToken ct = default)
    {
        string? canonicalProject = null;
        if (!string.IsNullOrWhiteSpace(project))
            canonicalProject = (await store.GetRepositoryByName(project.Trim()))?.Name ?? project.Trim();

        if (string.IsNullOrWhiteSpace(sha))
            return CommitScope.DefaultBranch(store, canonicalProject);

        var requested = sha.Trim().ToLowerInvariant();
        if (!BranchOverlayGit.IsFullSha(requested))
        {
            return Unavailable(InvalidShaCode, canonicalProject, sha.Trim(), "invalid",
                "sha must be a full 40- or 64-hex commit SHA; abbreviated SHAs are not accepted.");
        }

        var overlays = overlayStore is null
            ? []
            : await overlayStore.FindOverlaysByShaAsync(requested, canonicalProject, ct);

        if (canonicalProject is null)
        {
            var projects = overlays.Select(overlay => overlay.Project).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (projects.Count > 1)
            {
                return Unavailable(AmbiguousShaCode, null, requested, UnknownStatus,
                    $"Overlays for {requested} exist in several repositories ({string.Join(", ", projects.Order())}); pass the repository.");
            }

            if (projects.Count == 0)
            {
                // No overlay carries the SHA: it may still be a repository's default-branch index commit.
                var indexed = await FindProjectsIndexedAtAsync(requested, ct);
                if (indexed.Count == 1)
                    return CommitScope.PinnedToDefault(store, indexed[0], requested);
                if (indexed.Count > 1)
                {
                    return Unavailable(AmbiguousShaCode, null, requested, UnknownStatus,
                        $"Commit {requested} is the default-branch index of several repositories ({string.Join(", ", indexed.Order())}); pass the repository.");
                }

                return Unavailable(NotIndexedCode, null, requested, UnknownStatus,
                    $"No overlay index exists for commit {requested}. Request one with POST /api/indexer/overlays.");
            }

            canonicalProject = projects[0];
        }

        var defaultCommit = await GetDefaultCommitAsync(canonicalProject, ct);
        if (defaultCommit is not null && string.Equals(defaultCommit, requested, StringComparison.OrdinalIgnoreCase))
            return CommitScope.PinnedToDefault(store, canonicalProject, requested);

        var now = _time.GetUtcNow().UtcDateTime;
        var effective = overlays
            .Select(overlay => (Overlay: overlay, Status: EffectiveStatus(overlay, now)))
            .ToList();
        var ready = effective.Where(entry => entry.Status == BranchOverlayStatuses.Ready).Select(entry => entry.Overlay).ToList();
        var match = ready.FirstOrDefault(overlay =>
            defaultCommit is not null && string.Equals(overlay.BaseSha, defaultCommit, StringComparison.OrdinalIgnoreCase));

        if (match is not null && overlayStore is not null)
        {
            var view = await OverlayGraphView.CreateAsync(
                store,
                match,
                await overlayStore.GetOverlayFilesAsync(match.Id, ct),
                await overlayStore.GetOverlayNodesAsync(match.Id, ct),
                await overlayStore.GetOverlayEdgesAsync(match.Id, ct));
            return CommitScope.PinnedToOverlay(view, requested);
        }

        // A newer overlay for the SHA is already being built: report its progress, not an older
        // overlay's staleness.
        if (effective.Count > 0 && effective[0].Status is BranchOverlayStatuses.Queued or BranchOverlayStatuses.Indexing)
        {
            var (pending, status) = effective[0];
            return Unavailable(NotIndexedCode, canonicalProject, requested, status,
                $"Overlay {pending.Id} for {canonicalProject}@{requested} is not ready yet.", pending.Id, pending.BaseSha);
        }

        if (ready.Count > 0)
        {
            var newest = ready[0];
            return Unavailable(NotIndexedCode, canonicalProject, requested, StaleStatus,
                $"Overlay {newest.Id} for {requested} was built against default-branch commit {newest.BaseSha}, " +
                $"but the default-branch index is now at {defaultCommit ?? "an unknown commit"}. " +
                "Its answers would mix two default-branch commits, so it is not used; request a new overlay.",
                newest.Id, newest.BaseSha);
        }

        if (effective.Count > 0)
        {
            var (newest, status) = effective[0];
            var detail = status switch
            {
                BranchOverlayStatuses.Queued or BranchOverlayStatuses.Indexing => "is not ready yet",
                BranchOverlayStatuses.Failed => $"failed{(string.IsNullOrWhiteSpace(newest.Error) ? "" : $": {newest.Error}")}",
                BranchOverlayStatuses.Expired => "expired; request a new overlay",
                _ => $"is {status}"
            };
            return Unavailable(NotIndexedCode, canonicalProject, requested, status,
                $"Overlay {newest.Id} for {canonicalProject}@{requested} {detail}.", newest.Id, newest.BaseSha);
        }

        return Unavailable(NotIndexedCode, canonicalProject, requested, UnknownStatus,
            $"No overlay index exists for {canonicalProject}@{requested}. Request one with POST /api/indexer/overlays.");
    }

    public async Task<CommitDescription> DescribeAsync(
        CommitScope scope,
        IEnumerable<string?> projects,
        CancellationToken ct = default)
    {
        var distinct = projects
            .Append(scope.Project)
            .Where(project => !string.IsNullOrWhiteSpace(project))
            .Select(project => project!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var commits = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in distinct)
        {
            // A pinned answer describes the SHA for its own repository; any other repository in the
            // answer (cross-repo neighbours) is read from that repository's default-branch index.
            commits[project] = scope.Sha is not null && string.Equals(project, scope.Project, StringComparison.OrdinalIgnoreCase)
                ? scope.Sha
                : await GetDefaultCommitAsync(project, ct);
        }

        if (scope.Sha is not null)
            return new CommitDescription(scope.Sha, commits);

        return new CommitDescription(commits.Count == 1 ? commits.Values.Single() : null, commits);
    }

    public async Task<CommitSourceResult> ReadSourceAsync(
        CommitScope scope,
        string project,
        string filePath,
        CancellationToken ct = default)
    {
        var repository = await store.GetRepositoryByName(project);
        if (repository is null || !RepoFileResolver.IsSafeRelativePath(filePath))
            return new CommitSourceResult(CommitSourceStatus.NotFound, null, $"File not found: {project}/{filePath}.");

        if (scope.Sha is null)
        {
            // The working tree can be ahead of the index (the shared checkout is reset to the remote
            // head when an overlay is built; folder sources can carry uncommitted edits), so read the
            // blob at the default-branch index commit the answer is labelled with.
            var defaultSha = (await GetDefaultCommitAsync(repository.Name, ct))?.ToLowerInvariant();
            var defaultRoot = RepoFileResolver.ResolveRepositoryRoot(repository.Name, sourceOptions.Value.ReposCachePath, repository.LocalPath);
            if (defaultSha is not null && BranchOverlayGit.IsFullSha(defaultSha) && defaultRoot is not null)
            {
                var committed = await _git.ReadFileAtCommitAsync(defaultRoot, defaultSha, filePath, ct);
                if (committed.Status == CommitFileReadStatus.Found)
                    return new CommitSourceResult(CommitSourceStatus.Found, Decode(committed.Content!), "");
                if (committed.Status == CommitFileReadStatus.PathMissing)
                {
                    return new CommitSourceResult(CommitSourceStatus.NotFound, null,
                        $"File not found: {project}/{filePath} does not exist at commit {defaultSha}.");
                }
            }

            // The index commit is not in git here (e.g. a non-git folder source): read the working tree.
            var text = await RepoFileResolver.ReadAllTextAsync(
                repository.Name, filePath, sourceOptions.Value.ReposCachePath, repository.LocalPath, ct);
            return text is null
                ? new CommitSourceResult(CommitSourceStatus.NotFound, null, $"File not found: {project}/{filePath}.")
                : new CommitSourceResult(CommitSourceStatus.Found, text, "");
        }

        if (!string.Equals(repository.Name, scope.Project, StringComparison.OrdinalIgnoreCase))
        {
            return new CommitSourceResult(CommitSourceStatus.OutsideScope, null,
                $"{project}/{filePath} is not in {scope.Project}, the repository of commit {scope.Sha}.");
        }

        if (scope.Overlay is { } overlay && overlay.IsRemovedPath(filePath))
        {
            return new CommitSourceResult(CommitSourceStatus.DeletedAtCommit, null,
                $"{project}/{filePath} was deleted (or renamed away) at commit {scope.Sha}.");
        }

        var root = RepoFileResolver.ResolveRepositoryRoot(repository.Name, sourceOptions.Value.ReposCachePath, repository.LocalPath);
        if (root is null)
        {
            return new CommitSourceResult(CommitSourceStatus.CommitUnavailable, null,
                $"The {project} checkout is not available on this host, so commit {scope.Sha} cannot be read.");
        }

        var read = await _git.ReadFileAtCommitAsync(root, scope.Sha, filePath, ct);
        return read.Status switch
        {
            CommitFileReadStatus.Found => new CommitSourceResult(CommitSourceStatus.Found, Decode(read.Content!), ""),
            CommitFileReadStatus.PathMissing => new CommitSourceResult(CommitSourceStatus.NotFound, null,
                $"File not found: {project}/{filePath} does not exist at commit {scope.Sha}."),
            _ => new CommitSourceResult(CommitSourceStatus.CommitUnavailable, null,
                $"Commit {scope.Sha} is not present in the {project} checkout on this host.")
        };
    }

    public async Task<string?> GetDefaultCommitAsync(string project, CancellationToken ct = default)
    {
        var syncState = await store.GetSyncStateAsync(project);
        if (!string.IsNullOrWhiteSpace(syncState?.LastCommitSha))
            return syncState.LastCommitSha;

        var repository = await store.GetRepositoryByName(project);
        return string.IsNullOrWhiteSpace(repository?.LastCommitSha) ? null : repository.LastCommitSha;
    }

    private async Task<IReadOnlyList<string>> FindProjectsIndexedAtAsync(string sha, CancellationToken ct)
    {
        var matches = new List<string>();
        foreach (var repository in await store.ListRepositoriesAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(await GetDefaultCommitAsync(repository.Name, ct), sha, StringComparison.OrdinalIgnoreCase))
                matches.Add(repository.Name);
        }

        return matches;
    }

    private string EffectiveStatus(BranchOverlayEntity overlay, DateTime now)
        => overlay.Status != BranchOverlayStatuses.Expired && overlay.CreatedAt <= now - Ttl
            ? BranchOverlayStatuses.Expired
            : overlay.Status;

    private CommitScope Unavailable(
        string code,
        string? project,
        string sha,
        string status,
        string message,
        long? overlayId = null,
        string? baseSha = null)
        => CommitScope.Unavailable(store, new CommitNotIndexedResponse(code, project, sha, status, message, overlayId, baseSha));

    private static string Decode(byte[] content)
    {
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
