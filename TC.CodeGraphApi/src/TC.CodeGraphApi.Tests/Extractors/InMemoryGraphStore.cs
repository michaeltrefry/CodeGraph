using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Tests.Extractors;

/// <summary>
/// Complete in-memory IGraphStore implementation for unit testing.
/// </summary>
public class InMemoryGraphStore : IGraphStore, IExclusionStore
{
    private long _nextId = 1;
    private readonly List<GraphNode> _nodes = new();
    private readonly List<GraphEdge> _edges = new();
    private readonly List<CrossRepoEdge> _crossEdges = new();
    private readonly List<ProjectInfo> _projects = new();
    private readonly Dictionary<string, Dictionary<string, string>> _fileHashes = new();
    private readonly Dictionary<string, ProjectSummary> _summaries = new();

    public IReadOnlyList<GraphNode> Nodes => _nodes;
    public IReadOnlyList<GraphEdge> Edges => _edges;
    public IReadOnlyList<CrossRepoEdge> CrossEdges => _crossEdges;
    public IReadOnlyDictionary<string, ProjectSummary> Summaries => _summaries;

    public long AddNode(GraphNode node)
    {
        var withId = node with { Id = _nextId++ };
        _nodes.Add(withId);
        return withId.Id;
    }

    public void AddEdge(GraphEdge edge) => _edges.Add(edge);

    public void AddProject(string name, bool isFoundational = false) =>
        _projects.Add(new ProjectInfo(name, null, null, null, null, null, null, null, isFoundational, null));

    // ── IGraphStore implementation ──────────────────────────────────────

    public Task<IReadOnlyList<GraphNode>> FindAllNodesByLabelAsync(NodeLabel label, int limit = 50000) =>
        Task.FromResult<IReadOnlyList<GraphNode>>(
            _nodes.Where(n => n.Label == label).Take(limit).ToList());

    public Task<Dictionary<NodeLabel, int>> GetNodeCountsByLabelAsync() =>
        Task.FromResult(_nodes.GroupBy(n => n.Label).ToDictionary(g => g.Key, g => g.Count()));

    public Task<IReadOnlyList<GraphEdge>> FindAllEdgesByTypeAsync(EdgeType type) =>
        Task.FromResult<IReadOnlyList<GraphEdge>>(
            _edges.Where(e => e.Type == type).ToList());

    public Task<Dictionary<EdgeType, int>> GetEdgeCountsByTypeAsync() =>
        Task.FromResult(_edges.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count()));

    public Task<Dictionary<long, int>> GetCallFanInAsync(string project, int minFanIn) =>
        Task.FromResult(
            _edges.Where(e => e.Type == EdgeType.CALLS)
                .GroupBy(e => e.TargetId)
                .Where(g => g.Count() >= minFanIn)
                .ToDictionary(g => g.Key, g => g.Count()));

    public Task<IReadOnlyList<string>> FindProjectsWithNoCrossRepoEdgesAsync()
    {
        var withEdges = _crossEdges
            .SelectMany(e => new[] { e.SourceProject, e.TargetProject })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<string>>(
            _projects.Select(p => p.Name).Where(n => !withEdges.Contains(n)).ToList());
    }

    public Task InsertCrossRepoEdgeBatchAsync(IReadOnlyList<CrossRepoEdge> edges, CancellationToken ct = default)
    {
        _crossEdges.AddRange(edges);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectInfo>> ListRepositoriesAsync() =>
        Task.FromResult<IReadOnlyList<ProjectInfo>>(_projects);

    public Task<RepositorySearchResult> SearchRepositoriesAsync(string? search = null, string? group = null,
        int page = 1, int pageSize = 25)
    {
        IEnumerable<ProjectInfo> filtered = _projects;
        if (!string.IsNullOrWhiteSpace(group))
            filtered = filtered.Where(p => string.Equals(p.GitLabGroup, group, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(search))
            filtered = filtered.Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        var list = filtered.ToList();
        var items = list.OrderBy(p => p.Name).Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new RepositorySearchResult(items, list.Count));
    }

    public Task<IReadOnlyList<string>> GetDistinctGroupsAsync()
    {
        var groups = _projects
            .Select(p => p.GitLabGroup)
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(groups!);
    }

    public Task<ProjectInfo?> GetRepositoryByName(string name)
    {
        return Task.FromResult(_projects.FirstOrDefault(x => x.Name == name));
    }

    public Task<IReadOnlyList<TraversalEntry>> TraverseAsync(long startNodeId,
        TraceDirection direction, int maxDepth,
        EdgeType[]? edgeFilter = null, double minConfidence = 0)
    {
        var node = _nodes.FirstOrDefault(n => n.Id == startNodeId);
        if (node is null)
            return Task.FromResult<IReadOnlyList<TraversalEntry>>(Array.Empty<TraversalEntry>());

        return Task.FromResult<IReadOnlyList<TraversalEntry>>(
            new[] { new TraversalEntry(node, 0, EdgeType.CALLS, null, null) });
    }

    public Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceAsync(long sourceId, EdgeType? type = null) =>
        Task.FromResult<IReadOnlyList<GraphEdge>>(
            _edges.Where(e => e.SourceId == sourceId && (type == null || e.Type == type)).ToList());

    public Task<GraphNode?> FindNodeByIdAsync(long id) =>
        Task.FromResult(_nodes.FirstOrDefault(n => n.Id == id));

    public Task<Dictionary<long, GraphNode>> FindNodesByIdBatchAsync(IReadOnlyList<long> ids)
    {
        var idSet = ids.ToHashSet();
        var result = _nodes.Where(n => idSet.Contains(n.Id))
            .ToDictionary(n => n.Id);
        return Task.FromResult(result);
    }

    public Task<Dictionary<string, int>> GetNodeCountsByLabelForProjectAsync(string project) =>
        Task.FromResult(
            _nodes.Where(n => n.Project.Equals(project, StringComparison.OrdinalIgnoreCase))
                .GroupBy(n => n.Label.ToString())
                .ToDictionary(g => g.Key, g => g.Count()));

    public Task<Dictionary<string, Dictionary<string, int>>> GetNodeCountsByDotnetProjectAsync(string project)
    {
        var result = _nodes
            .Where(n => n.Project.Equals(project, StringComparison.OrdinalIgnoreCase) && n.DotnetProject is not null)
            .GroupBy(n => n.DotnetProject!)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(n => n.Label.ToString()).ToDictionary(lg => lg.Key, lg => lg.Count()));
        return Task.FromResult(result);
    }

    // ── Project operations ──────────────────────────────────────────────

    public Task UpsertRepositoryAsync(RepositoryEntity repository)
    {
        var existing = _projects.FirstOrDefault(p => p.Name.Equals(repository.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            _projects.Remove(existing);
        _projects.Add(new ProjectInfo(repository.Name, repository.RepoUrl, repository.GitLabGroup,
            repository.LocalPath, null, null, repository.Language, repository.Framework, repository.IsFoundational, null));
        return Task.CompletedTask;
    }

    public Task DeleteRepositoryAsync(string project)
    {
        _projects.RemoveAll(p => p.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
        _nodes.RemoveAll(n => n.Project.Equals(project, StringComparison.OrdinalIgnoreCase));
        _edges.RemoveAll(e =>
        {
            var source = _nodes.FirstOrDefault(n => n.Id == e.SourceId);
            var target = _nodes.FirstOrDefault(n => n.Id == e.TargetId);
            return (source?.Project.Equals(project, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (target?.Project.Equals(project, StringComparison.OrdinalIgnoreCase) ?? false);
        });
        _crossEdges.RemoveAll(e =>
            e.SourceProject.Equals(project, StringComparison.OrdinalIgnoreCase) ||
            e.TargetProject.Equals(project, StringComparison.OrdinalIgnoreCase));
        _fileHashes.Remove(project);
        _summaries.Remove(project);
        return Task.CompletedTask;
    }

    // ── Node operations ─────────────────────────────────────────────────

    public Task<long> UpsertNodeAsync(GraphNode node)
    {
        var existing = _nodes.FirstOrDefault(n =>
            n.Project.Equals(node.Project, StringComparison.OrdinalIgnoreCase) &&
            n.QualifiedName.Equals(node.QualifiedName, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            _nodes.Remove(existing);
            var updated = node with { Id = existing.Id };
            _nodes.Add(updated);
            return Task.FromResult(existing.Id);
        }

        var withId = node with { Id = _nextId++ };
        _nodes.Add(withId);
        return Task.FromResult(withId.Id);
    }

    public async Task<Dictionary<string, long>> UpsertNodeBatchAsync(IReadOnlyList<GraphNode> nodes, CancellationToken ct = default)
    {
        var result = new Dictionary<string, long>();
        foreach (var node in nodes)
        {
            var id = await UpsertNodeAsync(node);
            result[node.QualifiedName] = id;
        }
        return result;
    }

    public Task<GraphNode?> FindNodeByQualifiedNameAsync(string project, string qualifiedName) =>
        Task.FromResult(_nodes.FirstOrDefault(n =>
            n.Project.Equals(project, StringComparison.OrdinalIgnoreCase) &&
            n.QualifiedName.Equals(qualifiedName, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<GraphNode>> FindNodesByNameAsync(string project, string name, int limit = 1000) =>
        Task.FromResult<IReadOnlyList<GraphNode>>(
            _nodes.Where(n =>
                n.Project.Equals(project, StringComparison.OrdinalIgnoreCase) &&
                n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList());

    public Task<IReadOnlyList<GraphNode>> FindNodesByLabelAsync(string project, NodeLabel label, int limit = 10000) =>
        Task.FromResult<IReadOnlyList<GraphNode>>(
            _nodes.Where(n =>
                n.Project.Equals(project, StringComparison.OrdinalIgnoreCase) &&
                n.Label == label).Take(limit).ToList());

    public Task<IReadOnlyList<GraphNode>> FindNodesByFileAsync(string project, string filePath, int limit = 5000) =>
        Task.FromResult<IReadOnlyList<GraphNode>>(
            _nodes.Where(n =>
                n.Project.Equals(project, StringComparison.OrdinalIgnoreCase) &&
                n.FilePath != null &&
                n.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList());

    public Task<IReadOnlyList<GraphNode>> SearchNodesAsync(string? project, string namePattern,
        NodeLabel? label = null, string? filePattern = null, int limit = 50, int offset = 0,
        string? dotnetProject = null)
    {
        var query = _nodes.AsEnumerable();

        if (!string.IsNullOrEmpty(project))
            query = query.Where(n => n.Project.Equals(project, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(namePattern))
            query = query.Where(n => n.Name.Contains(namePattern, StringComparison.OrdinalIgnoreCase));

        if (label.HasValue)
            query = query.Where(n => n.Label == label.Value);

        if (!string.IsNullOrEmpty(filePattern))
            query = query.Where(n => n.FilePath != null &&
                n.FilePath.Contains(filePattern, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(dotnetProject))
            query = query.Where(n => n.DotnetProject == dotnetProject);

        return Task.FromResult<IReadOnlyList<GraphNode>>(
            query.Skip(offset).Take(limit).ToList());
    }

    public Task<int> SearchNodesCountAsync(string? project, string namePattern,
        NodeLabel? label = null, string? filePattern = null, string? dotnetProject = null)
    {
        var query = _nodes.AsEnumerable();
        if (!string.IsNullOrEmpty(project))
            query = query.Where(n => n.Project.Equals(project, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(namePattern))
            query = query.Where(n => n.Name.Contains(namePattern, StringComparison.OrdinalIgnoreCase));
        if (label.HasValue)
            query = query.Where(n => n.Label == label.Value);
        if (!string.IsNullOrEmpty(filePattern))
            query = query.Where(n => n.FilePath != null &&
                n.FilePath.Contains(filePattern, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(dotnetProject))
            query = query.Where(n => n.DotnetProject == dotnetProject);
        return Task.FromResult(query.Count());
    }

    // ── Edge operations ─────────────────────────────────────────────────

    public Task InsertEdgeAsync(GraphEdge edge)
    {
        _edges.Add(edge);
        return Task.CompletedTask;
    }

    public Task InsertEdgeBatchAsync(IReadOnlyList<GraphEdge> edges, CancellationToken ct = default)
    {
        _edges.AddRange(edges);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetAsync(long targetId, EdgeType? type = null) =>
        Task.FromResult<IReadOnlyList<GraphEdge>>(
            _edges.Where(e => e.TargetId == targetId && (type == null || e.Type == type)).ToList());

    public Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetBatchAsync(IReadOnlyList<long> targetIds, EdgeType[]? types = null)
    {
        var idSet = targetIds.ToHashSet();
        var query = _edges.Where(e => idSet.Contains(e.TargetId));
        if (types is { Length: > 0 })
            query = query.Where(e => types.Contains(e.Type));
        return Task.FromResult<IReadOnlyList<GraphEdge>>(query.ToList());
    }

    // ── Cross-repo edges ────────────────────────────────────────────────

    public Task InsertCrossRepoEdgeAsync(CrossRepoEdge edge)
    {
        _crossEdges.Add(edge);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CrossRepoEdge>> FindCrossRepoEdgesAsync(string project, EdgeType? type = null) =>
        Task.FromResult<IReadOnlyList<CrossRepoEdge>>(
            _crossEdges.Where(e =>
                (e.SourceProject.Equals(project, StringComparison.OrdinalIgnoreCase) ||
                 e.TargetProject.Equals(project, StringComparison.OrdinalIgnoreCase)) &&
                (type == null || e.Type == type)).ToList());

    public Task<IReadOnlyList<CrossRepoEdge>> GetAllCrossRepoEdgesAsync() =>
        Task.FromResult<IReadOnlyList<CrossRepoEdge>>(_crossEdges.ToList());

    public Task SetDoNotTrustAsync(long nodeId, bool doNotTrust)
    {
        var idx = _nodes.FindIndex(n => n.Id == nodeId);
        if (idx >= 0)
            _nodes[idx] = _nodes[idx] with { DoNotTrust = doNotTrust };
        return Task.CompletedTask;
    }

    // ── Bulk operations ─────────────────────────────────────────────────

    public Task DeleteNodesByFileAsync(string project, string filePath)
    {
        var toDelete = _nodes.Where(n =>
            n.Project.Equals(project, StringComparison.OrdinalIgnoreCase) &&
            n.FilePath != null &&
            n.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)).ToList();

        var nodeIds = toDelete.Select(n => n.Id).ToHashSet();
        _nodes.RemoveAll(n => nodeIds.Contains(n.Id));
        _edges.RemoveAll(e => nodeIds.Contains(e.SourceId) || nodeIds.Contains(e.TargetId));
        return Task.CompletedTask;
    }

    public Task DeleteNodesByProjectAsync(string project)
    {
        var nodeIds = _nodes.Where(n =>
            n.Project.Equals(project, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Id)
            .ToHashSet();

        _nodes.RemoveAll(n => nodeIds.Contains(n.Id));
        _edges.RemoveAll(e => nodeIds.Contains(e.SourceId) || nodeIds.Contains(e.TargetId));
        return Task.CompletedTask;
    }

    // ── File hashes ─────────────────────────────────────────────────────

    public Task<Dictionary<string, string>> GetFileHashesAsync(string project)
    {
        if (_fileHashes.TryGetValue(project, out var hashes))
            return Task.FromResult(new Dictionary<string, string>(hashes));
        return Task.FromResult(new Dictionary<string, string>());
    }

    public Task UpsertFileHashBatchAsync(string project, Dictionary<string, string> hashes, CancellationToken ct = default)
    {
        if (!_fileHashes.ContainsKey(project))
            _fileHashes[project] = new Dictionary<string, string>();

        foreach (var kvp in hashes)
            _fileHashes[project][kvp.Key] = kvp.Value;

        return Task.CompletedTask;
    }

    public Task DeleteFileHashesAsync(string project, IReadOnlyList<string> relPaths)
    {
        if (_fileHashes.TryGetValue(project, out var hashes))
        {
            foreach (var path in relPaths)
                hashes.Remove(path);
        }
        return Task.CompletedTask;
    }

    // ── Summaries ───────────────────────────────────────────────────────

    public Task UpsertRepositorySummaryAsync(string project, string summary,
        ConfidenceLevel confidence, string sourceHash, string? modelUsed = null)
    {
        _summaries[project] = new ProjectSummary(project, summary, confidence, sourceHash, modelUsed, DateTime.UtcNow, DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public Task<ProjectSummary?> GetRepositorySummaryAsync(string project)
    {
        _summaries.TryGetValue(project, out var summary);
        return Task.FromResult(summary);
    }

    // ── Per-project analyses ─────────────────────────────────────────────

    private readonly Dictionary<(string, string), StoredProjectAnalysis> _projectAnalyses = new();

    public Task UpsertProjectAnalysisAsync(string repo, StoredProjectAnalysis analysis)
    {
        _projectAnalyses[(repo, analysis.ProjectName)] = analysis;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredProjectAnalysis>> GetProjectAnalysesAsync(string repo)
    {
        var results = _projectAnalyses
            .Where(kv => kv.Key.Item1 == repo)
            .Select(kv => kv.Value)
            .ToList();
        return Task.FromResult<IReadOnlyList<StoredProjectAnalysis>>(results);
    }

    // ── Migrations ──────────────────────────────────────────────────────

    public Task ApplyMigrationsAsync(string migrationsPath)
    {
        // No-op for in-memory store
        return Task.CompletedTask;
    }

    // ── Sync state ──────────────────────────────────────────────────────

    private readonly Dictionary<string, SyncStateEntity> _syncStates = new();

    public Task<SyncStateEntity?> GetSyncStateAsync(string project) =>
        Task.FromResult(_syncStates.GetValueOrDefault(project));

    public Task UpsertSyncStateAsync(SyncStateEntity state)
    {
        _syncStates[state.Project] = state;
        return Task.CompletedTask;
    }

    // ── Graph context for batch analysis ────────────────────────────────

    public Task<IReadOnlyList<NodeEntity>> GetClassNodesWithEdgesAsync(string project) =>
        Task.FromResult<IReadOnlyList<NodeEntity>>([]);

    public Task<IReadOnlyList<NodeEntity>> GetChildNodesAsync(long parentNodeId) =>
        Task.FromResult<IReadOnlyList<NodeEntity>>([]);

    public Task<IReadOnlyList<EdgeEntity>> GetOutboundEdgesAsync(long nodeId) =>
        Task.FromResult<IReadOnlyList<EdgeEntity>>([]);

    public Task<IReadOnlyList<EdgeEntity>> GetInboundEdgesAsync(long nodeId) =>
        Task.FromResult<IReadOnlyList<EdgeEntity>>([]);

    public Task<IReadOnlyList<NodeEntity>> GetAllNodesByProjectAsync(string project) =>
        Task.FromResult<IReadOnlyList<NodeEntity>>([]);

    public Task<IReadOnlyList<EdgeEntity>> GetAllEdgesByProjectAsync(string project) =>
        Task.FromResult<IReadOnlyList<EdgeEntity>>([]);

    public Task<IReadOnlyList<EdgeEntity>> GetEdgesForNodesAsync(IReadOnlyList<long> nodeIds) =>
        Task.FromResult<IReadOnlyList<EdgeEntity>>([]);

    // ── Analysis batch tracking ──────────────────────────────────────────

    private readonly List<AnalysisBatchEntity> _batches = new();
    private readonly List<AnalysisBatchRequestEntity> _batchRequests = new();
    private long _nextBatchId = 1;

    public Task<long> CreateAnalysisBatchAsync(AnalysisBatchEntity batch)
    {
        batch.Id = _nextBatchId++;
        _batches.Add(batch);
        return Task.FromResult(batch.Id);
    }

    public Task CreateBatchRequestsAsync(IEnumerable<AnalysisBatchRequestEntity> requests)
    {
        _batchRequests.AddRange(requests);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredAnalysisBatch>> GetPendingBatchesAsync(string? repo = null) =>
        Task.FromResult<IReadOnlyList<StoredAnalysisBatch>>(
            _batches
                .Where(b => b.Status == "submitted" && (repo is null || b.Repo == repo))
                .Select(b => new StoredAnalysisBatch(b.Id, b.Repo, b.AnthropicBatchId, b.Status,
                    b.RequestCount, b.CompletedCount, b.SubmittedAt, b.CompletedAt))
                .ToList());

    public Task<StoredAnalysisBatch?> GetLatestBatchAsync(string repo)
    {
        var batch = _batches
            .Where(b => b.Repo == repo)
            .OrderByDescending(b => b.SubmittedAt)
            .FirstOrDefault();
        return Task.FromResult(batch is null
            ? null
            : new StoredAnalysisBatch(batch.Id, batch.Repo, batch.AnthropicBatchId, batch.Status,
                batch.RequestCount, batch.CompletedCount, batch.SubmittedAt, batch.CompletedAt));
    }

    public Task UpdateBatchStatusAsync(long batchId, string status, int completedCount, DateTime? completedAt)
    {
        var batch = _batches.FirstOrDefault(b => b.Id == batchId);
        if (batch is not null)
        {
            batch.Status = status;
            batch.CompletedCount = completedCount;
            batch.CompletedAt = completedAt;
        }
        return Task.CompletedTask;
    }

    public Task UpdateBatchRequestStatusAsync(string customId, string status, DateTime completedAt)
    {
        var req = _batchRequests.FirstOrDefault(r => r.CustomId == customId);
        if (req is not null)
        {
            req.Status = status;
            req.CompletedAt = completedAt;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AnalysisBatchRequestEntity>> GetBatchRequestsAsync(long batchId)
    {
        IReadOnlyList<AnalysisBatchRequestEntity> result = _batchRequests
            .Where(r => r.BatchId == batchId).ToList();
        return Task.FromResult(result);
    }

    // ── Node analysis results ────────────────────────────────────────────

    private readonly Dictionary<long, NodeAnalysisEntity> _nodeAnalyses = new();

    public Task UpsertNodeAnalysisAsync(NodeAnalysisEntity analysis)
    {
        _nodeAnalyses[analysis.NodeId] = analysis;
        return Task.CompletedTask;
    }

    public Task<StoredNodeAnalysis?> GetNodeAnalysisAsync(long nodeId)
    {
        if (!_nodeAnalyses.TryGetValue(nodeId, out var e))
            return Task.FromResult<StoredNodeAnalysis?>(null);
        return Task.FromResult<StoredNodeAnalysis?>(
            new StoredNodeAnalysis(e.NodeId, e.Description, e.Confidence, e.ModelUsed, e.CreatedAt, e.UpdatedAt));
    }

    public Task<Dictionary<long, StoredNodeAnalysis>> GetNodeAnalysesBatchAsync(IReadOnlyList<long> nodeIds)
    {
        var result = new Dictionary<long, StoredNodeAnalysis>();
        foreach (var id in nodeIds)
        {
            if (_nodeAnalyses.TryGetValue(id, out var e))
                result[id] = new StoredNodeAnalysis(e.NodeId, e.Description, e.Confidence, e.ModelUsed, e.CreatedAt, e.UpdatedAt);
        }
        return Task.FromResult(result);
    }

    // ── File metrics (vitals) ────────────────────────────────────────────

    private readonly List<FileMetricsEntity> _fileMetrics = new();
    private readonly List<ProjectHealthSummaryEntity> _healthSummaries = new();

    public Task UpsertFileMetricsBatchAsync(string project, IReadOnlyList<FileMetricsEntity> metrics)
    {
        _fileMetrics.RemoveAll(m => m.Project == project);
        _fileMetrics.AddRange(metrics);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FileMetricsEntity>> GetFileMetricsAsync(string project, string? dotnetProject = null)
    {
        var results = _fileMetrics
            .Where(m => m.Project == project && (dotnetProject is null || m.DotnetProject == dotnetProject))
            .ToList();
        return Task.FromResult<IReadOnlyList<FileMetricsEntity>>(results);
    }

    public Task<IReadOnlyList<FileMetricsEntity>> GetHotspotsAsync(string project, int top = 10) =>
        Task.FromResult<IReadOnlyList<FileMetricsEntity>>(
            _fileMetrics.Where(m => m.Project == project)
                .OrderByDescending(m => m.RiskScore).Take(top).ToList());

    public Task DeleteFileMetricsAsync(string project)
    {
        _fileMetrics.RemoveAll(m => m.Project == project);
        return Task.CompletedTask;
    }

    public Task UpsertProjectHealthSummaryAsync(ProjectHealthSummaryEntity summary)
    {
        _healthSummaries.RemoveAll(s =>
            s.Project == summary.Project && s.DotnetProject == summary.DotnetProject);
        _healthSummaries.Add(summary);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetProjectHealthSummariesAsync(string project) =>
        Task.FromResult<IReadOnlyList<ProjectHealthSummaryEntity>>(
            _healthSummaries.Where(s => s.Project == project).ToList());

    public Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetAllRepoHealthSummariesAsync() =>
        Task.FromResult<IReadOnlyList<ProjectHealthSummaryEntity>>(
            _healthSummaries.Where(s => string.IsNullOrEmpty(s.DotnetProject)).OrderBy(s => s.OverallHealth).ToList());

    // ── Project health analyses (Claude-generated) ───────────────────────

    private readonly List<ProjectHealthAnalysisEntity> _healthAnalyses = new();

    public Task UpsertProjectHealthAnalysisAsync(ProjectHealthAnalysisEntity analysis)
    {
        _healthAnalyses.RemoveAll(a =>
            a.Project == analysis.Project && a.DotnetProject == analysis.DotnetProject);
        _healthAnalyses.Add(analysis);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectHealthAnalysisEntity>> GetProjectHealthAnalysesAsync(string project) =>
        Task.FromResult<IReadOnlyList<ProjectHealthAnalysisEntity>>(
            _healthAnalyses.Where(a => a.Project == project).ToList());

    // ── Security findings ──────────────────────────────────────────────

    private readonly List<SecurityFindingEntity> _securityFindings = new();
    private readonly Dictionary<string, ProjectSecuritySummaryEntity> _securitySummaries = new();

    public Task DeleteSecurityFindingsAsync(string project)
    {
        _securityFindings.RemoveAll(f => f.Project == project);
        return Task.CompletedTask;
    }

    public Task UpsertSecurityFindingsBatchAsync(string project, IReadOnlyList<SecurityFindingEntity> findings)
    {
        _securityFindings.AddRange(findings);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SecurityFindingEntity>> GetSecurityFindingsAsync(string project) =>
        Task.FromResult<IReadOnlyList<SecurityFindingEntity>>(
            _securityFindings.Where(f => f.Project == project).ToList());

    public Task UpsertProjectSecuritySummaryAsync(ProjectSecuritySummaryEntity summary)
    {
        _securitySummaries[summary.Project] = summary;
        return Task.CompletedTask;
    }

    public Task<ProjectSecuritySummaryEntity?> GetProjectSecuritySummaryAsync(string project) =>
        Task.FromResult(_securitySummaries.GetValueOrDefault(project));

    // ── Cleanup operations ───────────────────────────────────────────────

    public Task DeleteSyncStateAsync(string project)
    {
        _syncStates.Remove(project);
        return Task.CompletedTask;
    }

    public Task DeleteAllEdgesForProjectAsync(string project)
    {
        _edges.RemoveAll(e =>
        {
            var sourceNode = _nodes.FirstOrDefault(n => n.Id == e.SourceId);
            return sourceNode?.Project.Equals(project, StringComparison.OrdinalIgnoreCase) == true;
        });
        return Task.CompletedTask;
    }

    public Task DeleteCrossRepoEdgesForProjectAsync(string project)
    {
        _crossEdges.RemoveAll(e =>
            e.SourceProject.Equals(project, StringComparison.OrdinalIgnoreCase) ||
            e.TargetProject.Equals(project, StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }

    public Task DeleteAnalysisDataForProjectAsync(string project)
    {
        _batches.RemoveAll(b => b.Repo == project);
        _batchRequests.RemoveAll(r => _batches.All(b => b.Id != r.BatchId));
        _projectAnalyses.Keys.Where(k => k.Item1 == project).ToList()
            .ForEach(k => _projectAnalyses.Remove(k));
        _summaries.Remove(project);
        _healthSummaries.RemoveAll(s => s.Project == project);
        _healthAnalyses.RemoveAll(a => a.Project == project);
        return Task.CompletedTask;
    }

    // ── Clusters (community detection) ────────────────────────────────

    private readonly List<RepoCluster> _repoClusters = new();

    public Task ReplaceRepoClustersAsync(IReadOnlyList<RepoCluster> clusters)
    {
        _repoClusters.Clear();
        _repoClusters.AddRange(clusters);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RepoCluster>> GetRepoClustersAsync(int level = 0) =>
        Task.FromResult<IReadOnlyList<RepoCluster>>(
            _repoClusters.Where(c => c.Level == level).OrderBy(c => c.ClusterId).ThenBy(c => c.ProjectName).ToList());

    public Task<IReadOnlyList<RepoCluster>> GetRepoClusterMembersAsync(int clusterId, int level = 0) =>
        Task.FromResult<IReadOnlyList<RepoCluster>>(
            _repoClusters.Where(c => c.ClusterId == clusterId && c.Level == level)
                .OrderByDescending(c => c.BetweennessCentrality).ToList());

    // ── Exclusion rules ──────────────────────────────────────────────

    private readonly List<ExclusionRuleEntity> _exclusionRules = new();
    private long _nextExclusionId = 1;

    public Task<IReadOnlyList<ExclusionRuleEntity>> ListExclusionRulesAsync() =>
        Task.FromResult<IReadOnlyList<ExclusionRuleEntity>>(_exclusionRules.ToList());

    public Task<ExclusionRuleEntity?> GetExclusionRuleAsync(long id) =>
        Task.FromResult(_exclusionRules.FirstOrDefault(r => r.Id == id));

    public Task<ExclusionRuleEntity> CreateExclusionRuleAsync(ExclusionRuleEntity rule)
    {
        rule.Id = _nextExclusionId++;
        _exclusionRules.Add(rule);
        return Task.FromResult(rule);
    }

    public Task<ExclusionRuleEntity?> UpdateExclusionRuleAsync(long id, string exclusionType, string? reason)
    {
        var rule = _exclusionRules.FirstOrDefault(r => r.Id == id);
        if (rule is null) return Task.FromResult<ExclusionRuleEntity?>(null);
        rule.ExclusionType = exclusionType;
        rule.Reason = reason;
        rule.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult<ExclusionRuleEntity?>(rule);
    }

    public Task<bool> DeleteExclusionRuleAsync(long id)
    {
        var removed = _exclusionRules.RemoveAll(r => r.Id == id);
        return Task.FromResult(removed > 0);
    }

    public Task<HashSet<string>> GetSecretFilePathsAsync(string project)
    {
        var paths = _securityFindings
            .Where(f => f.Project == project && f.Category == "secret" && f.FilePath is not null)
            .Select(f => f.FilePath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(paths);
    }
}
