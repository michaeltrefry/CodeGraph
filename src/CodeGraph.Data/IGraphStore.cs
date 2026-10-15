using CodeGraph.Models;

namespace CodeGraph.Data;

/// <summary>
/// Composite storage interface for the full code graph.
/// Extends focused sub-interfaces for analysis, metrics, and migrations.
/// Consumers that only need a subset should depend on the narrower interface.
/// </summary>
public interface IGraphStore : IGraphReader, IAnalysisStore, IMetricsStore, IReviewStore, IMigrationRunner
{
    Task<IAsyncDisposable> AcquireProjectIndexingLockAsync(
        string project,
        CancellationToken ct = default);

    // Repositories
    Task UpsertRepositoryAsync(RepositoryEntity repository);
    /// <summary>
    /// Creates the repository row when it is missing (so graph writes can reference it).
    /// An existing row only has its identity fields refreshed; its index results
    /// (indexed_at, language, framework, properties) stay as the last completed run left them.
    /// </summary>
    Task EnsureRepositoryAsync(RepositoryEntity repository);
    Task<IReadOnlyList<ProjectInfo>> ListRepositoriesAsync();
    Task<RepositorySearchResult> SearchRepositoriesAsync(string? search = null, string? group = null,
        int page = 1, int pageSize = 25);
    Task<SchemaRepositorySearchResult> SearchSchemaRepositoriesAsync(
        string? search = null,
        string? server = null,
        string? database = null,
        int page = 1,
        int pageSize = 25);
    Task<IReadOnlyList<string>> GetDistinctGroupsAsync();
    Task<ProjectInfo?> GetRepositoryByName(string name);
    Task UpdateRepositoryCommitShaAsync(string name, string? commitSha);
    Task DeleteRepositoryAsync(string project);

    // Nodes
    Task<long> UpsertNodeAsync(GraphNode node);
    Task<Dictionary<string, long>> UpsertNodeBatchAsync(IReadOnlyList<GraphNode> nodes, CancellationToken ct = default);
    Task<IReadOnlyList<GraphNode>> FindNodesByLabelAsync(string project, NodeLabel label, int limit = 10000);
    Task<IReadOnlyList<GraphNode>> FindAllNodesByLabelAsync(NodeLabel label, int limit = 50000);
    Task<Dictionary<NodeLabel, int>> GetNodeCountsByLabelAsync();
    Task<Dictionary<string, Dictionary<string, int>>> GetNodeCountsByDotnetProjectAsync(string project);
    Task<Dictionary<string, int>> GetNodeCountsByLabelForProjectAsync(string project);
    Task SetDoNotTrustAsync(long nodeId, bool doNotTrust);

    // Edges
    Task InsertEdgeAsync(GraphEdge edge);
    Task InsertEdgeBatchAsync(IReadOnlyList<GraphEdge> edges, CancellationToken ct = default);
    Task<IReadOnlyList<GraphEdge>> FindAllEdgesByTypeAsync(EdgeType type);
    Task<Dictionary<EdgeType, int>> GetEdgeCountsByTypeAsync();

    // Cross-repo link projections: stream only the linking keys, never whole rows of every project.
    IAsyncEnumerable<LinkNodeRow> StreamLinkNodesAsync(LinkNodeQuery query, CancellationToken ct = default)
        => CrossRepoLinkProjection.StreamNodesAsync(this, query, ct);
    IAsyncEnumerable<LinkEdgeRow> StreamLinkEdgesAsync(LinkEdgeQuery query, CancellationToken ct = default)
        => CrossRepoLinkProjection.StreamEdgesAsync(this, query, ct);

    // Cross-repo edges
    Task InsertCrossRepoEdgeAsync(CrossRepoEdge edge);
    Task InsertCrossRepoEdgeBatchAsync(IReadOnlyList<CrossRepoEdge> edges, CancellationToken ct = default);
    Task<IReadOnlyList<string>> FindProjectsWithNoCrossRepoEdgesAsync();
    Task<IReadOnlyList<CrossRepoEdge>> GetAllCrossRepoEdgesAsync();

    // Aggregates
    /// <summary>
    /// The nodes most pointed at by <paramref name="project"/>'s edges of <paramref name="edgeTypes"/>,
    /// computed over the whole graph: each node with its number of such incoming edges (at least
    /// <paramref name="minInDegree"/>), optionally restricted to <paramref name="targetLabels"/>,
    /// ordered by count descending then node id, at most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<NodeInDegree>> GetTopInDegreeNodesAsync(
        string project,
        IReadOnlyCollection<EdgeType> edgeTypes,
        IReadOnlyCollection<NodeLabel>? targetLabels,
        int minInDegree,
        int limit);

    // Bulk operations
    Task DeleteNodesByFileAsync(string project, string filePath);
    Task DeleteNodesByProjectAsync(string project);
    Task<int> ReplaceProjectGraphAsync(
        string project,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, string> fileHashes,
        RepositoryEntity repository,
        SyncStateEntity? syncState,
        IReadOnlyDictionary<string, string>? extractionVersions = null,
        CancellationToken ct = default);
    /// <summary>
    /// Atomically replaces the graph slice of <paramref name="filePaths"/>. When
    /// <paramref name="retractions"/> is given, its edges (matched by source, target and type) are
    /// deleted first and never preserved, and each of its orphan candidates without a file path is
    /// deleted once the write leaves it without edges.
    /// </summary>
    Task<int> ReplaceProjectFilesAsync(
        string project,
        IReadOnlyList<string> filePaths,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, string> fileHashes,
        IReadOnlyDictionary<string, string>? extractionVersions = null,
        GraphSliceRetractions? retractions = null,
        CancellationToken ct = default);

    /// <summary>Nodes of <paramref name="project"/> in any of <paramref name="filePaths"/>.</summary>
    Task<IReadOnlyList<GraphNode>> FindNodesByFilesAsync(
        string project, IReadOnlyCollection<string> filePaths, CancellationToken ct = default);

    /// <summary>Distinct non-empty file paths of the nodes of <paramref name="project"/>.</summary>
    Task<IReadOnlyList<string>> GetNodeFilePathsAsync(string project, CancellationToken ct = default);

    /// <summary>Nodes of <paramref name="project"/> whose name is one of <paramref name="names"/>.</summary>
    Task<IReadOnlyList<GraphNode>> FindNodesByNamesAsync(
        string project, IReadOnlyCollection<string> names, CancellationToken ct = default);

    /// <summary>
    /// Edges of <paramref name="project"/> of one of <paramref name="types"/> whose string property
    /// <paramref name="propertyKey"/> equals one of <paramref name="values"/>, with their endpoints'
    /// qualified names and files. The type predicate lets the store narrow the scan by edge type
    /// before it inspects edge properties.
    /// </summary>
    Task<IReadOnlyList<StoredEdge>> FindEdgesByPropertyAsync(
        string project,
        IReadOnlyCollection<EdgeType> types,
        string propertyKey,
        IReadOnlyCollection<string> values,
        CancellationToken ct = default);
    Task DeleteFilesFromProjectGraphAsync(
        string project,
        IReadOnlyList<string> filePaths,
        CancellationToken ct = default);

    // File hashes (incremental indexing)
    Task<Dictionary<string, string>> GetFileHashesAsync(string project);

    /// <summary>Content hash and extraction version of every indexed file, keyed by relative path.</summary>
    Task<Dictionary<string, FileHashRecord>> GetFileHashRecordsAsync(string project);
    Task UpsertFileHashBatchAsync(string project, Dictionary<string, string> hashes, CancellationToken ct = default);
    Task DeleteFileHashesAsync(string project, IReadOnlyList<string> relPaths);

    // Sync state
    Task<SyncStateEntity?> GetSyncStateAsync(string project);
    Task<IReadOnlyDictionary<string, SyncStateEntity>> GetSyncStatesAsync(IReadOnlyList<string> projects);
    Task UpsertSyncStateAsync(SyncStateEntity state);

    /// <summary>
    /// Records the vitals version a completed vitals computation used and clears the vitals
    /// failure record. No-op when the project has no sync state.
    /// </summary>
    Task SetVitalsVersionAsync(string project, int vitalsVersion);

    /// <summary>
    /// Counts a failed vitals computation of <paramref name="commitSha"/> at
    /// <paramref name="vitalsVersion"/>: consecutive with the stored record when both match,
    /// otherwise the first. Returns the consecutive count (0 when the project has no sync state).
    /// </summary>
    Task<int> RecordVitalsFailureAsync(string project, string? commitSha, int vitalsVersion);
    Task DeleteSyncStateAsync(string project);

    // Clusters (community detection)
    Task ReplaceRepoClustersAsync(IReadOnlyList<RepoCluster> clusters);
    Task<IReadOnlyList<RepoCluster>> GetRepoClustersAsync(int level = 0);
    Task<IReadOnlyList<RepoCluster>> GetRepoClusterMembersAsync(int clusterId, int level = 0);

    // Project cleanup (cascading delete)
    Task DeleteAllEdgesForProjectAsync(string project);
    Task DeleteCrossRepoEdgesForProjectAsync(string project);
    Task DeleteAnalysisDataForProjectAsync(string project);
}
