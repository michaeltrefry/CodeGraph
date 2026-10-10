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

    Task AttachRunAsync(long overlayId, long runId, CancellationToken ct = default);

    /// <summary>Current overlay status; an overlay past its TTL is expired (and its data deleted) first.</summary>
    Task<BranchOverlayResponse?> GetOverlayAsync(long overlayId, CancellationToken ct = default);

    /// <summary>Indexes the overlay's changed files. Runs inside the durable indexer worker.</summary>
    Task<string> IndexOverlayAsync(long overlayId, CancellationToken ct = default);

    /// <summary>Deletes every overlay older than the configured TTL and marks it expired.</summary>
    Task<IReadOnlyList<long>> ExpireOverlaysAsync(CancellationToken ct = default);

    BranchOverlayResponse Map(BranchOverlayEntity overlay);
}

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
    TimeProvider? timeProvider = null) : IBranchOverlayService
{
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

    public Task AttachRunAsync(long overlayId, long runId, CancellationToken ct = default)
        => overlayStore.SetOverlayRunAsync(overlayId, runId, ct);

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

                headSha = await git.ResolveCommitAsync(resolved.LocalPath, overlay.RequestedRef, ct);
                await git.EnsureCommitAsync(resolved.LocalPath, baseSha, ct);

                changes = (await git.DiffAsync(resolved.LocalPath, baseSha, headSha, ct))
                    .Select(change => FilterIndexable(change))
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
                csprojPaths = (await git.ListFilesAsync(resolved.LocalPath, headSha, ct))
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
    /// indexable degrades to a deletion of the old path, and vice versa to an addition.
    /// </summary>
    private OverlayGitChange? FilterIndexable(OverlayGitChange change)
    {
        if (change.Kind != OverlayGitChangeKind.Renamed)
            return pipeline.IsIndexablePath(change.Path) ? change : null;

        var newIndexable = pipeline.IsIndexablePath(change.Path);
        var oldIndexable = change.PreviousPath is not null && pipeline.IsIndexablePath(change.PreviousPath);
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
