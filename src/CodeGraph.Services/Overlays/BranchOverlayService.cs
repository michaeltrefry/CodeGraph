using System.Data.Common;
using CodeGraph.Data;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Overlays;

public interface IBranchOverlayService
{
    /// <summary>Validates the request and records a <c>queued</c> overlay.</summary>
    Task<BranchOverlayEntity> CreateOverlayAsync(string username, string repo, string gitRef, CancellationToken ct = default);

    /// <summary>
    /// Idempotent request: resolves <paramref name="gitRef"/> to its current commit and returns the
    /// live overlay for that repository + commit, or records a new <c>queued</c> overlay pinned to
    /// the commit. Live means queued/indexing, or ready against the current default-branch index
    /// commit, and within the TTL. Failed, expired, and stale overlays are never returned.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The repository is not indexed.</exception>
    /// <exception cref="ArgumentException">The ref is invalid or does not exist.</exception>
    /// <exception cref="BranchOverlayTransientException">The ref could not be looked up on origin.</exception>
    Task<BranchOverlayRequestOutcome> RequestOverlayAsync(string username, string repo, string gitRef, CancellationToken ct = default);

    Task AttachRunAsync(long overlayId, long runId, CancellationToken ct = default);

    /// <summary>Marks an overlay whose indexer run could not be queued as failed, so it is never reused.</summary>
    Task MarkOverlayUnqueuedAsync(BranchOverlayEntity overlay, string error, CancellationToken ct = default);

    /// <summary>Current overlay status; an overlay past its TTL is expired (and its data deleted) first.</summary>
    Task<BranchOverlayResponse?> GetOverlayAsync(long overlayId, CancellationToken ct = default);

    /// <summary>Indexes the overlay's changed files. Runs inside the durable indexer worker.</summary>
    Task<string> IndexOverlayAsync(long overlayId, CancellationToken ct = default);

    /// <summary>Deletes every overlay older than the configured TTL and marks it expired.</summary>
    Task<IReadOnlyList<long>> ExpireOverlaysAsync(CancellationToken ct = default);

    BranchOverlayResponse Map(BranchOverlayEntity overlay);
}

/// <param name="Reused">True when <paramref name="Overlay"/> already existed.</param>
public sealed record BranchOverlayRequestOutcome(BranchOverlayEntity Overlay, bool Reused);

/// <summary>
/// Raised when an overlay cannot be indexed for a deterministic reason (unresolvable ref, missing
/// base commit, diff/archive/extraction errors), so the durable worker does not replay it.
/// Transient failures (lock timeouts, network and database errors) are not wrapped in this type.
/// </summary>
public sealed class BranchOverlayIndexingException(string message, Exception? inner = null)
    : Exception(message, inner)
{
    public const string FailureCode = "branch_overlay_failed";
}

/// <summary>
/// Branch overlay indexing. An overlay holds the graph slice for files that differ between a
/// branch/SHA and the commit of the current default-branch index (sync_state.last_commit_sha).
/// Overlay indexing performs no LLM calls and never writes the default-branch graph.
/// </summary>
public sealed class BranchOverlayService(
    IBranchOverlayStore overlayStore,
    IGraphStore graphStore,
    IRepoProvider repoProvider,
    IndexingPipeline pipeline,
    BranchOverlayGit git,
    IOptions<BranchOverlayOptions> optionsAccessor,
    ILogger<BranchOverlayService> logger,
    TimeProvider? timeProvider = null,
    IOptions<RepositorySourceOptions>? sourceOptions = null) : IBranchOverlayService
{
    /// <summary>How long a queued overlay may exist without an indexer run before it counts as orphaned.</summary>
    internal static readonly TimeSpan UnqueuedOverlayGrace = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private BranchOverlayOptions Options => optionsAccessor.Value;
    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    public async Task<BranchOverlayEntity> CreateOverlayAsync(
        string username,
        string repo,
        string gitRef,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repo is required.", nameof(repo));
        gitRef = gitRef?.Trim() ?? "";
        BranchOverlayGit.ValidateRef(gitRef);

        repo = repo.Trim();
        var repository = await graphStore.GetRepositoryByName(repo)
            ?? throw new KeyNotFoundException($"Repository '{repo}' was not found.");

        var overlay = new BranchOverlayEntity
        {
            Project = repository.Name,
            RequestedRef = gitRef,
            Status = BranchOverlayStatuses.Queued,
            RequestedByUsername = string.IsNullOrWhiteSpace(username) ? "unknown" : username.Trim().ToLowerInvariant(),
            CreatedAt = UtcNow
        };
        overlay.Id = await overlayStore.CreateOverlayAsync(overlay, ct);
        return overlay;
    }

    public async Task<BranchOverlayRequestOutcome> RequestOverlayAsync(
        string username,
        string repo,
        string gitRef,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repo is required.", nameof(repo));
        gitRef = gitRef?.Trim() ?? "";
        BranchOverlayGit.ValidateRef(gitRef);

        repo = repo.Trim();
        var repository = await graphStore.GetRepositoryByName(repo)
            ?? throw new KeyNotFoundException($"Repository '{repo}' was not found.");

        var headSha = await ResolveRequestedHeadAsync(repository, gitRef, ct);
        var syncState = await graphStore.GetSyncStateAsync(repository.Name);
        var baseSha = FirstNonEmpty(syncState?.LastCommitSha, repository.LastCommitSha)?.ToLowerInvariant();

        // Find-or-create under a per-repository lock so concurrent identical requests share one overlay.
        await using (await overlayStore.AcquireOverlayRequestLockAsync(repository.Name, ct))
        {
            var now = UtcNow;
            var live = (await overlayStore.FindOverlaysByShaAsync(headSha, repository.Name, ct))
                .FirstOrDefault(overlay => IsLive(overlay, baseSha, now));
            if (live is not null)
                return new BranchOverlayRequestOutcome(live, Reused: true);

            var overlay = new BranchOverlayEntity
            {
                Project = repository.Name,
                RequestedRef = gitRef,
                // Pinned: the worker indexes this commit even if the branch moves before it runs,
                // so the head this request answers with is the head the overlay describes.
                HeadSha = headSha,
                // Expected base; the worker records the base it actually diffs against.
                BaseSha = baseSha,
                Status = BranchOverlayStatuses.Queued,
                RequestedByUsername = string.IsNullOrWhiteSpace(username) ? "unknown" : username.Trim().ToLowerInvariant(),
                CreatedAt = now
            };
            overlay.Id = await overlayStore.CreateOverlayAsync(overlay, ct);
            return new BranchOverlayRequestOutcome(overlay, Reused: false);
        }
    }

    /// <summary>
    /// The commit the requested ref points at now. Branch and tag names are looked up on origin
    /// from the repository checkout (read-only: no fetch, no ref writes, so no project lock).
    /// </summary>
    private async Task<string> ResolveRequestedHeadAsync(ProjectInfo repository, string gitRef, CancellationToken ct)
    {
        if (BranchOverlayGit.IsFullSha(gitRef))
            return gitRef.ToLowerInvariant();

        var root = RepoFileResolver.ResolveRepositoryRoot(
            repository.Name, sourceOptions?.Value.ReposCachePath, repository.LocalPath);
        if (root is null)
        {
            throw new BranchOverlayTransientException(
                $"The '{repository.Name}' checkout is not available on this host, so ref '{gitRef}' cannot be resolved; " +
                "request the overlay by full commit SHA or retry after the repository is indexed here.");
        }

        return await git.ResolveRemoteRefAsync(root, gitRef, ct)
            ?? throw new ArgumentException($"Ref '{gitRef}' does not exist in repository '{repository.Name}'.", nameof(gitRef));
    }

    /// <summary>
    /// Whether a request for the overlay's commit may be answered with it. Mirrors the query side
    /// (<see cref="CommitScopeResolver"/>): a ready overlay built against an older default-branch
    /// commit is stale and is never returned; a failed or expired one is replaced by a new request.
    /// </summary>
    private bool IsLive(BranchOverlayEntity overlay, string? currentBaseSha, DateTime now)
    {
        if (overlay.Status == BranchOverlayStatuses.Expired || overlay.CreatedAt <= now - Options.Ttl)
            return false;

        // A queued overlay whose run was never attached (the request died between recording the
        // overlay and queuing its run) has nothing behind it; past a short grace period for the
        // request that is still queuing it, it is orphaned and not reused.
        if (overlay.Status == BranchOverlayStatuses.Queued
            && overlay.IndexerRunId is null
            && overlay.CreatedAt < now - UnqueuedOverlayGrace)
        {
            return false;
        }

        var baseMatches = overlay.BaseSha is null
            || string.Equals(overlay.BaseSha, currentBaseSha, StringComparison.OrdinalIgnoreCase);
        return overlay.Status switch
        {
            BranchOverlayStatuses.Queued or BranchOverlayStatuses.Indexing => baseMatches,
            BranchOverlayStatuses.Ready => overlay.BaseSha is not null && baseMatches,
            _ => false
        };
    }

    public Task AttachRunAsync(long overlayId, long runId, CancellationToken ct = default)
        => overlayStore.SetOverlayRunAsync(overlayId, runId, ct);

    public async Task MarkOverlayUnqueuedAsync(BranchOverlayEntity overlay, string error, CancellationToken ct = default)
    {
        var now = UtcNow;
        await overlayStore.MarkOverlayFailedAsync(overlay.Id, error, overlay.HeadSha, overlay.BaseSha, now, ct);
        overlay.Status = BranchOverlayStatuses.Failed;
        overlay.Error = error;
        overlay.CompletedAt = now;
    }

    public async Task<BranchOverlayResponse?> GetOverlayAsync(long overlayId, CancellationToken ct = default)
    {
        var overlay = await overlayStore.GetOverlayAsync(overlayId, ct);
        if (overlay is null)
            return null;

        if (overlay.Status != BranchOverlayStatuses.Expired && IsPastTtl(overlay))
        {
            await ExpireOverlaysAsync(ct);
            overlay = await overlayStore.GetOverlayAsync(overlayId, ct) ?? overlay;
        }

        return Map(overlay);
    }

    public async Task<string> IndexOverlayAsync(long overlayId, CancellationToken ct = default)
    {
        var overlay = await overlayStore.GetOverlayAsync(overlayId, ct)
            ?? throw new BranchOverlayIndexingException($"Branch overlay {overlayId} was not found.");

        if (overlay.Status == BranchOverlayStatuses.Expired || IsPastTtl(overlay))
        {
            await ExpireOverlaysAsync(ct);
            return $"Branch overlay {overlayId} expired before indexing; skipped.";
        }

        if (!await overlayStore.MarkOverlayIndexingAsync(overlayId, UtcNow, ct))
            return $"Branch overlay {overlayId} expired before indexing; skipped.";

        string? headSha = null;
        string? baseSha = null;
        var workRoot = Path.Combine(Path.GetTempPath(), "codegraph-overlays", $"{overlayId}-{Guid.NewGuid():N}");
        try
        {
            var repository = await graphStore.GetRepositoryByName(overlay.Project)
                ?? throw new InvalidOperationException($"Repository '{overlay.Project}' was not found.");
            var syncState = await graphStore.GetSyncStateAsync(overlay.Project);
            baseSha = FirstNonEmpty(syncState?.LastCommitSha, repository.LastCommitSha)
                ?? throw new InvalidOperationException(
                    $"Repository '{overlay.Project}' has no default-branch index commit to diff against.");

            IReadOnlyList<OverlayGitChange> changes;
            List<string> extractPaths;
            List<string> removedPaths;
            List<string> csprojPaths;

            // Serialize git access with default-branch indexing of the same checkout. The lock is
            // released before extraction, which runs against a private materialized copy.
            await using (await graphStore.AcquireProjectIndexingLockAsync(overlay.Project, ct))
            {
                // For GitHub/GitLab providers this syncs the shared checkout (fetch origin and reset
                // to the remote default branch), the same resync default-branch indexing performs,
                // which is why it runs under the project lock. The overlay never reads the working
                // tree: the head comes from the fetched overlay ref or a commit SHA.
                ResolvedRepository resolved;
                try
                {
                    resolved = await repoProvider.ResolveRepositoryAsync(overlay.Project, null, repository.RepoUrl, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !IsTransient(ex))
                {
                    throw new BranchOverlayTransientException(
                        $"Syncing the '{overlay.Project}' checkout failed: {ex.Message}", ex);
                }

                // An overlay requested with reuse is pinned to the head its ref had at request time.
                headSha = overlay.HeadSha is { } pinned && !BranchOverlayGit.IsFullSha(overlay.RequestedRef)
                    ? await git.ResolvePinnedCommitAsync(resolved.LocalPath, overlay.RequestedRef, pinned, ct)
                    : await git.ResolveCommitAsync(resolved.LocalPath, overlay.RequestedRef, ct);
                await git.EnsureCommitAsync(resolved.LocalPath, baseSha, ct);

                // Indexability of a path can depend on its siblings (a Podfile next to Pods/), so
                // new paths are judged against the head tree and old paths against the base tree.
                var headFiles = await git.ListFilesAsync(resolved.LocalPath, headSha, ct);
                var headFileSet = headFiles.ToHashSet(StringComparer.Ordinal);
                var baseFileSet = (await git.ListFilesAsync(resolved.LocalPath, baseSha, ct))
                    .ToHashSet(StringComparer.Ordinal);
                changes = (await git.DiffAsync(resolved.LocalPath, baseSha, headSha, ct))
                    .Select(change => FilterIndexable(change, headFileSet.Contains, baseFileSet.Contains))
                    .OfType<OverlayGitChange>()
                    .ToList();
                extractPaths = changes
                    .Where(change => change.Kind != OverlayGitChangeKind.Deleted)
                    .Select(change => change.Path)
                    .ToList();
                removedPaths = changes
                    .Where(change => change.Kind == OverlayGitChangeKind.Deleted)
                    .Select(change => change.Path)
                    .Concat(changes
                        .Where(change => change.Kind == OverlayGitChangeKind.Renamed && change.PreviousPath is not null)
                        .Select(change => change.PreviousPath!))
                    .ToList();
                csprojPaths = headFiles
                    .Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (extractPaths.Count > 0)
                    await git.MaterializeAsync(resolved.LocalPath, headSha, extractPaths, workRoot, ct);
            }

            var slice = await pipeline.ExtractOverlayAsync(
                overlay.Project, workRoot, extractPaths, removedPaths, csprojPaths, ct);
            var files = BuildFileRows(overlayId, changes);

            if (!await overlayStore.CompleteOverlayAsync(
                    overlayId, headSha, baseSha, files, slice.Nodes, slice.Edges, UtcNow, ct))
            {
                return $"Branch overlay {overlayId} expired while indexing; results discarded.";
            }

            logger.LogInformation(
                "Branch overlay {OverlayId} for {Repo}@{Ref} ready: head {HeadSha}, base {BaseSha}, {FileCount} files, {NodeCount} nodes, {EdgeCount} edges",
                overlayId, overlay.Project, overlay.RequestedRef, headSha, baseSha, files.Count, slice.Nodes.Count, slice.Edges.Count);
            return $"Branch overlay {overlayId} for {overlay.Project}@{overlay.RequestedRef} is ready " +
                   $"({files.Count} changed files, {slice.Nodes.Count} nodes).";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await overlayStore.MarkOverlayFailedAsync(
                overlayId, "Overlay indexing was canceled.", headSha, baseSha, UtcNow, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Branch overlay {OverlayId} for {Repo}@{Ref} failed",
                overlayId, overlay.Project, overlay.RequestedRef);
            var transient = IsTransient(ex);
            await overlayStore.MarkOverlayFailedAsync(
                overlayId,
                transient ? $"Transient failure; the indexer run may retry: {ex.Message}" : ex.Message,
                headSha,
                baseSha,
                UtcNow,
                CancellationToken.None);

            // Transient failures propagate unwrapped so the durable worker's retry policy applies
            // (a retry moves the overlay back to indexing). Everything else is deterministic for
            // the requested ref and base commit and is not replayed.
            if (transient)
                throw;
            throw ex as BranchOverlayIndexingException
                  ?? new BranchOverlayIndexingException($"Branch overlay {overlayId} failed: {ex.Message}", ex);
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    public async Task<IReadOnlyList<long>> ExpireOverlaysAsync(CancellationToken ct = default)
    {
        var now = UtcNow;
        var expired = await overlayStore.ExpireOverlaysAsync(now - Options.Ttl, now, ct);
        if (expired.Count > 0)
            logger.LogInformation("Expired {Count} branch overlays older than {Ttl}", expired.Count, Options.Ttl);
        return expired;
    }

    public BranchOverlayResponse Map(BranchOverlayEntity overlay) => new(
        overlay.Id,
        overlay.Project,
        overlay.RequestedRef,
        overlay.Status,
        overlay.HeadSha,
        overlay.BaseSha,
        overlay.Error,
        overlay.IndexerRunId,
        overlay.ChangedFileCount,
        overlay.NodeCount,
        overlay.EdgeCount,
        overlay.CreatedAt,
        overlay.StartedAt,
        overlay.CompletedAt,
        overlay.CreatedAt + Options.Ttl,
        overlay.ExpiredAt,
        $"/api/indexer/overlays/{overlay.Id}");

    private bool IsPastTtl(BranchOverlayEntity overlay)
        => overlay.CreatedAt <= UtcNow - Options.Ttl;

    /// <summary>
    /// Failures that can succeed on a later attempt: lock waits (GET_LOCK timeout while a long
    /// default-branch reindex holds the project lock), network/remote errors, and database errors.
    /// </summary>
    internal static bool IsTransient(Exception ex) => ex
        is BranchOverlayTransientException
        or TimeoutException
        or HttpRequestException
        or DbException
        or OperationCanceledException;

    /// <summary>
    /// Restricts a change to the default index's file universe. A rename whose new path is not
    /// indexable degrades to a deletion of the old path, and vice versa to an addition. A deleted
    /// path and a rename's previous path are judged against the base tree, every other path
    /// against the head tree.
    /// </summary>
    private OverlayGitChange? FilterIndexable(
        OverlayGitChange change, Func<string, bool> headFileExists, Func<string, bool> baseFileExists)
    {
        if (change.Kind != OverlayGitChangeKind.Renamed)
        {
            var tree = change.Kind == OverlayGitChangeKind.Deleted ? baseFileExists : headFileExists;
            return pipeline.IsIndexablePath(change.Path, tree) ? change : null;
        }

        var newIndexable = pipeline.IsIndexablePath(change.Path, headFileExists);
        var oldIndexable = change.PreviousPath is not null && pipeline.IsIndexablePath(change.PreviousPath, baseFileExists);
        return (newIndexable, oldIndexable) switch
        {
            (true, true) => change,
            (true, false) => change with { Kind = OverlayGitChangeKind.Added, PreviousPath = null },
            (false, true) => new OverlayGitChange(OverlayGitChangeKind.Deleted, change.PreviousPath!),
            _ => null
        };
    }

    /// <summary>
    /// File rows for the overlay. A rename is recorded as a deletion tombstone for the old path
    /// plus a <c>renamed</c> row (with its previous path) for the new path.
    /// </summary>
    private static List<BranchOverlayFileEntity> BuildFileRows(long overlayId, IReadOnlyList<OverlayGitChange> changes)
    {
        var rows = new Dictionary<string, BranchOverlayFileEntity>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case OverlayGitChangeKind.Renamed:
                    rows.TryAdd(change.PreviousPath!, new BranchOverlayFileEntity
                    {
                        OverlayId = overlayId,
                        FilePath = change.PreviousPath!,
                        ChangeKind = BranchOverlayChangeKinds.Deleted
                    });
                    rows[change.Path] = new BranchOverlayFileEntity
                    {
                        OverlayId = overlayId,
                        FilePath = change.Path,
                        ChangeKind = BranchOverlayChangeKinds.Renamed,
                        PreviousPath = change.PreviousPath
                    };
                    break;
                default:
                    rows[change.Path] = new BranchOverlayFileEntity
                    {
                        OverlayId = overlayId,
                        FilePath = change.Path,
                        ChangeKind = change.Kind switch
                        {
                            OverlayGitChangeKind.Added => BranchOverlayChangeKinds.Added,
                            OverlayGitChangeKind.Deleted => BranchOverlayChangeKinds.Deleted,
                            _ => BranchOverlayChangeKinds.Modified
                        }
                    };
                    break;
            }
        }

        return rows.Values.OrderBy(row => row.FilePath, StringComparer.Ordinal).ToList();
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to delete branch overlay work directory {Path}", path);
        }
    }
}
