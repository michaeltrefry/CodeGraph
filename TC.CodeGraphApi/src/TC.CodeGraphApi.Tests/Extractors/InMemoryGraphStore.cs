using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Tests.Extractors;

/// <summary>
/// Minimal in-memory IGraphStore for unit testing CrossRepoLinker.
/// Only the methods used by the linker are implemented.
/// </summary>
public class InMemoryGraphStore : IGraphStore
{
    private long _nextId = 1;
    private readonly List<GraphNode> _nodes = new();
    private readonly List<GraphEdge> _edges = new();
    private readonly List<CrossRepoEdge> _crossEdges = new();
    private readonly List<ProjectInfo> _projects = new();

    public IReadOnlyList<GraphNode> Nodes => _nodes;
    public IReadOnlyList<GraphEdge> Edges => _edges;
    public IReadOnlyList<CrossRepoEdge> CrossEdges => _crossEdges;

    public long AddNode(GraphNode node)
    {
        var withId = node with { Id = _nextId++ };
        _nodes.Add(withId);
        return withId.Id;
    }

    public void AddEdge(GraphEdge edge) => _edges.Add(edge);

    public void AddProject(string name) =>
        _projects.Add(new ProjectInfo(name, null, null, null, null, null, null, false, null));

    // ── IGraphStore implementation ──────────────────────────────────────

    public Task<IReadOnlyList<GraphNode>> FindAllNodesByLabelAsync(NodeLabel label) =>
        Task.FromResult<IReadOnlyList<GraphNode>>(
            _nodes.Where(n => n.Label == label).ToList());

    public Task<IReadOnlyList<GraphEdge>> FindAllEdgesByTypeAsync(EdgeType type) =>
        Task.FromResult<IReadOnlyList<GraphEdge>>(
            _edges.Where(e => e.Type == type).ToList());

    public Task InsertCrossRepoEdgeBatchAsync(IReadOnlyList<CrossRepoEdge> edges)
    {
        _crossEdges.AddRange(edges);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync() =>
        Task.FromResult<IReadOnlyList<ProjectInfo>>(_projects);

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

    // ── Unused methods (throw NotImplementedException) ───────────────────

    public Task UpsertProjectAsync(string name, string? localPath = null,
        string? repoUrl = null, bool isFoundational = false) => throw new NotImplementedException();
    public Task DeleteProjectAsync(string project) => throw new NotImplementedException();
    public Task<long> UpsertNodeAsync(GraphNode node) => throw new NotImplementedException();
    public Task<Dictionary<string, long>> UpsertNodeBatchAsync(IReadOnlyList<GraphNode> nodes) => throw new NotImplementedException();
    public Task<GraphNode?> FindNodeByQualifiedNameAsync(string project, string qualifiedName) => throw new NotImplementedException();
    public Task<IReadOnlyList<GraphNode>> FindNodesByNameAsync(string project, string name) => throw new NotImplementedException();
    public Task<IReadOnlyList<GraphNode>> FindNodesByLabelAsync(string project, NodeLabel label) => throw new NotImplementedException();
    public Task<IReadOnlyList<GraphNode>> FindNodesByFileAsync(string project, string filePath) => throw new NotImplementedException();
    public Task<IReadOnlyList<GraphNode>> SearchNodesAsync(string? project, string namePattern,
        NodeLabel? label = null, string? filePattern = null, int limit = 50, int offset = 0) => throw new NotImplementedException();
    public Task InsertEdgeAsync(GraphEdge edge) => throw new NotImplementedException();
    public Task InsertEdgeBatchAsync(IReadOnlyList<GraphEdge> edges) => throw new NotImplementedException();
    public Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetAsync(long targetId, EdgeType? type = null) => throw new NotImplementedException();
    public Task InsertCrossRepoEdgeAsync(CrossRepoEdge edge) => throw new NotImplementedException();
    public Task<IReadOnlyList<CrossRepoEdge>> FindCrossRepoEdgesAsync(string project, EdgeType? type = null) => throw new NotImplementedException();
    public Task DeleteNodesByFileAsync(string project, string filePath) => throw new NotImplementedException();
    public Task DeleteNodesByProjectAsync(string project) => throw new NotImplementedException();
    public Task<Dictionary<string, string>> GetFileHashesAsync(string project) => throw new NotImplementedException();
    public Task UpsertFileHashBatchAsync(string project, Dictionary<string, string> hashes) => throw new NotImplementedException();
    public Task DeleteFileHashesAsync(string project, IReadOnlyList<string> relPaths) => throw new NotImplementedException();
    public Task UpsertProjectSummaryAsync(string project, string summary, ConfidenceLevel confidence, string sourceHash, string? modelUsed = null) => throw new NotImplementedException();
    public Task<ProjectSummary?> GetProjectSummaryAsync(string project) => throw new NotImplementedException();
    public Task ApplyMigrationsAsync(string migrationsPath) => throw new NotImplementedException();
}
