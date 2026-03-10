using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Data;

public interface IGraphStore
{
    // Projects
    Task UpsertProjectAsync(string name, string? localPath = null,
        string? repoUrl = null, bool isFoundational = false);
    Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync();
    Task DeleteProjectAsync(string project);

    // Nodes
    Task<long> UpsertNodeAsync(GraphNode node);
    Task<Dictionary<string, long>> UpsertNodeBatchAsync(IReadOnlyList<GraphNode> nodes);
    Task<GraphNode?> FindNodeByQualifiedNameAsync(string project, string qualifiedName);
    Task<IReadOnlyList<GraphNode>> FindNodesByNameAsync(string project, string name);
    Task<IReadOnlyList<GraphNode>> FindNodesByLabelAsync(string project, NodeLabel label);
    Task<IReadOnlyList<GraphNode>> FindNodesByFileAsync(string project, string filePath);
    Task<IReadOnlyList<GraphNode>> SearchNodesAsync(string? project, string namePattern,
        NodeLabel? label = null, string? filePattern = null,
        int limit = 50, int offset = 0);
    Task<IReadOnlyList<GraphNode>> FindAllNodesByLabelAsync(NodeLabel label);

    // Edges
    Task InsertEdgeAsync(GraphEdge edge);
    Task InsertEdgeBatchAsync(IReadOnlyList<GraphEdge> edges);
    Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceAsync(long sourceId, EdgeType? type = null);
    Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetAsync(long targetId, EdgeType? type = null);
    Task<IReadOnlyList<GraphEdge>> FindAllEdgesByTypeAsync(EdgeType type);

    // Cross-repo edges
    Task InsertCrossRepoEdgeAsync(CrossRepoEdge edge);
    Task InsertCrossRepoEdgeBatchAsync(IReadOnlyList<CrossRepoEdge> edges);
    Task<IReadOnlyList<CrossRepoEdge>> FindCrossRepoEdgesAsync(
        string project, EdgeType? type = null);

    // Traversal
    Task<IReadOnlyList<TraversalEntry>> TraverseAsync(long startNodeId,
        TraceDirection direction, int maxDepth,
        EdgeType[]? edgeFilter = null, double minConfidence = 0);

    // Bulk operations
    Task DeleteNodesByFileAsync(string project, string filePath);
    Task DeleteNodesByProjectAsync(string project);

    // File hashes (incremental indexing)
    Task<Dictionary<string, string>> GetFileHashesAsync(string project);
    Task UpsertFileHashBatchAsync(string project, Dictionary<string, string> hashes);
    Task DeleteFileHashesAsync(string project, IReadOnlyList<string> relPaths);

    // Summaries
    Task UpsertProjectSummaryAsync(string project, string summary,
        ConfidenceLevel confidence, string sourceHash, string? modelUsed = null);
    Task<ProjectSummary?> GetProjectSummaryAsync(string project);

    // Migrations
    Task ApplyMigrationsAsync(string migrationsPath);
}
