using CodeGraph.Models;

namespace CodeGraph.Data.Migration;

public interface INeo4jToMariaDbGraphExporter
{
    Task<Neo4jToMariaDbGraphCounts> CountRepositoriesAndGraphAsync(CancellationToken ct = default);

    Task<Neo4jToMariaDbGraphExport> ExportRepositoriesAndGraphAsync(CancellationToken ct = default);
}

public sealed record Neo4jToMariaDbGraphCounts(
    int Repositories,
    int Nodes,
    int Edges,
    int CrossRepoEdges);

public sealed record Neo4jToMariaDbGraphExport(
    IReadOnlyList<RepositoryEntity> Repositories,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<CrossRepoEdge> CrossRepoEdges)
{
    public Neo4jToMariaDbGraphCounts Counts => new(
        Repositories.Count,
        Nodes.Count,
        Edges.Count,
        CrossRepoEdges.Count);
}

public sealed record Neo4jToMariaDbMigrationCheckpoint(
    string Area,
    string Stage,
    string Status,
    int ItemCount,
    DateTime OccurredAtUtc,
    string? Message = null);

public sealed record Neo4jToMariaDbGraphImportResult(
    long? RunId,
    string Status,
    Neo4jToMariaDbGraphCounts Exported,
    Neo4jToMariaDbGraphCounts Imported,
    int SkippedEdges,
    int SkippedCrossRepoEdges,
    IReadOnlyList<Neo4jToMariaDbMigrationCheckpoint> Checkpoints,
    string? Message = null,
    string? Error = null);
