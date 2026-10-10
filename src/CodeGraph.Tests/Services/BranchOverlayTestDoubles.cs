using System.Diagnostics;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Extractors;

namespace CodeGraph.Tests.Services;

/// <summary>In-memory <see cref="IBranchOverlayStore"/> mirroring the MariaDB store's semantics.</summary>
internal sealed class InMemoryBranchOverlayStore : IBranchOverlayStore
{
    private readonly object _sync = new();
    private readonly Dictionary<long, BranchOverlayEntity> _overlays = new();
    private readonly Dictionary<long, List<BranchOverlayFileEntity>> _files = new();
    private readonly Dictionary<long, List<GraphNode>> _nodes = new();
    private readonly Dictionary<long, List<PendingEdge>> _edges = new();
    private long _nextId = 1;

    public Task<long> CreateOverlayAsync(BranchOverlayEntity overlay, CancellationToken ct = default)
    {
        lock (_sync)
        {
            overlay.Id = _nextId++;
            _overlays[overlay.Id] = Clone(overlay);
            return Task.FromResult(overlay.Id);
        }
    }

    public Task<BranchOverlayEntity?> GetOverlayAsync(long overlayId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_overlays.TryGetValue(overlayId, out var overlay) ? Clone(overlay) : null);
    }

    public Task SetOverlayRunAsync(long overlayId, long indexerRunId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_overlays.TryGetValue(overlayId, out var overlay))
                overlay.IndexerRunId = indexerRunId;
        }

        return Task.CompletedTask;
    }

    public Task<bool> MarkOverlayIndexingAsync(long overlayId, DateTime startedAt, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!_overlays.TryGetValue(overlayId, out var overlay) || overlay.Status == BranchOverlayStatuses.Expired)
                return Task.FromResult(false);
            overlay.Status = BranchOverlayStatuses.Indexing;
            overlay.StartedAt = startedAt;
            overlay.CompletedAt = null;
            overlay.Error = null;
            return Task.FromResult(true);
        }
    }

    public Task<bool> CompleteOverlayAsync(
        long overlayId,
        string headSha,
        string baseSha,
        IReadOnlyList<BranchOverlayFileEntity> files,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        DateTime completedAt,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!_overlays.TryGetValue(overlayId, out var overlay) || overlay.Status == BranchOverlayStatuses.Expired)
                return Task.FromResult(false);
            _files[overlayId] = files.ToList();
            _nodes[overlayId] = nodes.ToList();
            _edges[overlayId] = edges.ToList();
            overlay.Status = BranchOverlayStatuses.Ready;
            overlay.HeadSha = headSha;
            overlay.BaseSha = baseSha;
            overlay.Error = null;
            overlay.ChangedFileCount = files.Count;
            overlay.NodeCount = nodes.Count;
            overlay.EdgeCount = edges.Count;
            overlay.CompletedAt = completedAt;
            return Task.FromResult(true);
        }
    }

    public Task MarkOverlayFailedAsync(
        long overlayId,
        string error,
        string? headSha,
        string? baseSha,
        DateTime completedAt,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_overlays.TryGetValue(overlayId, out var overlay) && overlay.Status != BranchOverlayStatuses.Expired)
            {
                overlay.Status = BranchOverlayStatuses.Failed;
                overlay.Error = error;
                overlay.HeadSha = headSha ?? overlay.HeadSha;
                overlay.BaseSha = baseSha ?? overlay.BaseSha;
                overlay.CompletedAt = completedAt;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BranchOverlayFileEntity>> GetOverlayFilesAsync(long overlayId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<BranchOverlayFileEntity>>(_files.GetValueOrDefault(overlayId)?.ToList() ?? []);
    }

    public Task<IReadOnlyList<GraphNode>> GetOverlayNodesAsync(long overlayId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<GraphNode>>(_nodes.GetValueOrDefault(overlayId)?.ToList() ?? []);
    }

    public Task<IReadOnlyList<PendingEdge>> GetOverlayEdgesAsync(long overlayId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<PendingEdge>>(_edges.GetValueOrDefault(overlayId)?.ToList() ?? []);
    }

    public Task<IReadOnlyList<long>> ExpireOverlaysAsync(DateTime createdAtOrBefore, DateTime expiredAt, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var ids = _overlays.Values
                .Where(overlay => overlay.Status != BranchOverlayStatuses.Expired && overlay.CreatedAt <= createdAtOrBefore)
                .Select(overlay => overlay.Id)
                .OrderBy(id => id)
                .ToList();
            foreach (var id in ids)
            {
                _files.Remove(id);
                _nodes.Remove(id);
                _edges.Remove(id);
                _overlays[id].Status = BranchOverlayStatuses.Expired;
                _overlays[id].ExpiredAt = expiredAt;
            }

            return Task.FromResult<IReadOnlyList<long>>(ids);
        }
    }

    private static BranchOverlayEntity Clone(BranchOverlayEntity overlay) => new()
    {
        Id = overlay.Id,
        Project = overlay.Project,
        RequestedRef = overlay.RequestedRef,
        HeadSha = overlay.HeadSha,
        BaseSha = overlay.BaseSha,
        Status = overlay.Status,
        Error = overlay.Error,
        RequestedByUsername = overlay.RequestedByUsername,
        IndexerRunId = overlay.IndexerRunId,
        ChangedFileCount = overlay.ChangedFileCount,
        NodeCount = overlay.NodeCount,
        EdgeCount = overlay.EdgeCount,
        CreatedAt = overlay.CreatedAt,
        StartedAt = overlay.StartedAt,
        CompletedAt = overlay.CompletedAt,
        ExpiredAt = overlay.ExpiredAt
    };
}

internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FixedPathRepoProvider(string localPath) : IRepoProvider
{
    public Task<ResolvedRepository> ResolveRepositoryAsync(string repoName, string? localPath, string? repoUrl, CancellationToken ct = default)
        => Task.FromResult(new ResolvedRepository(localPath ?? this.LocalPath, $"folder:{repoName}", repoUrl, null));

    public string LocalPath { get; } = localPath;

    public Task<string> EnsureLocalAsync(string repoName, string? localPath, string? repoUrl, CancellationToken ct = default)
        => Task.FromResult(LocalPath);

    public Task<List<DiscoveredProject>> DiscoverProjectsAsync(CancellationToken ct = default)
        => Task.FromResult(new List<DiscoveredProject>());

    public Task<List<DiscoveredProject>> SearchProjectsAsync(string searchTerm, CancellationToken ct = default)
        => Task.FromResult(new List<DiscoveredProject>());
}

/// <summary>
/// Deterministic extractor for .py files: one Function node per <c>def name</c> line and a bare
/// call for every <c>call name</c> line.
/// </summary>
internal sealed class LineFunctionExtractor : ICodeExtractor
{
    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>([".py"], StringComparer.OrdinalIgnoreCase);

    public Task<ExtractionResult> ExtractAsync(string filePath, string content, ExtractorContext context, CancellationToken ct = default)
    {
        var relativePath = Path.GetRelativePath(context.RootPath, filePath).Replace('\\', '/');
        var nodes = new List<GraphNode>();
        var calls = new List<UnresolvedCall>();
        string? current = null;
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2);
            if (parts.Length != 2)
                continue;
            if (parts[0] == "def")
            {
                current = $"{context.ProjectName}:{relativePath}:{parts[1]}";
                nodes.Add(new GraphNode
                {
                    Project = context.ProjectName,
                    Label = NodeLabel.Function,
                    Name = parts[1],
                    QualifiedName = current,
                    FilePath = relativePath
                });
            }
            else if (parts[0] == "call" && current is not null)
            {
                calls.Add(new UnresolvedCall(current, parts[1], null, 0.9, CallReceiverKind.Bare));
            }
        }

        return Task.FromResult(new ExtractionResult { Nodes = nodes, UnresolvedCalls = calls });
    }
}

internal static class GitFixture
{
    public static string Run(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.Environment["GIT_AUTHOR_NAME"] = "CodeGraph Test";
        psi.Environment["GIT_AUTHOR_EMAIL"] = "test@example.invalid";
        psi.Environment["GIT_COMMITTER_NAME"] = "CodeGraph Test";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "test@example.invalid";
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    public static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
