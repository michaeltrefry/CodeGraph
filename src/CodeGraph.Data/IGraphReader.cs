using CodeGraph.Models;

namespace CodeGraph.Data;

/// <summary>
/// Read-only node, edge and traversal queries over a code graph. <see cref="IGraphStore"/>
/// answers them from the default-branch index; a branch overlay view answers the same queries
/// for a specific commit by layering the overlay's changed files over that index.
/// </summary>
public interface IGraphReader
{
    Task<GraphNode?> FindNodeByIdAsync(long id);
    Task<GraphNode?> FindNodeByQualifiedNameAsync(string project, string qualifiedName);

    /// <summary>Nodes of <paramref name="project"/> whose qualified name is one of <paramref name="qualifiedNames"/>.</summary>
    Task<IReadOnlyList<GraphNode>> FindNodesByQualifiedNamesAsync(
        string project,
        IReadOnlyCollection<string> qualifiedNames);

    Task<IReadOnlyList<GraphNode>> FindNodesByNameAsync(string project, string name, int limit = 1000);
    Task<IReadOnlyList<GraphNode>> FindNodesByFileAsync(string project, string filePath, int limit = 5000);
    Task<IReadOnlyList<GraphNode>> SearchNodesAsync(string? project, string namePattern,
        NodeLabel? label = null, string? filePattern = null,
        int limit = 50, int offset = 0, string? dotnetProject = null);
    Task<int> SearchNodesCountAsync(string? project, string namePattern,
        NodeLabel? label = null, string? filePattern = null, string? dotnetProject = null);
    Task<Dictionary<long, GraphNode>> FindNodesByIdBatchAsync(IReadOnlyList<long> ids);

    Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceAsync(long sourceId, EdgeType? type = null);
    Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceBatchAsync(IReadOnlyList<long> sourceIds, EdgeType[]? types = null);
    Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetAsync(long targetId, EdgeType? type = null);
    Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetBatchAsync(IReadOnlyList<long> targetIds, EdgeType[]? types = null);

    Task<IReadOnlyList<CrossRepoEdge>> FindCrossRepoEdgesAsync(
        string project, EdgeType? type = null);

    Task<IReadOnlyList<TraversalEntry>> TraverseAsync(long startNodeId,
        TraceDirection direction, int maxDepth,
        EdgeType[]? edgeFilter = null, double minConfidence = 0);
}
