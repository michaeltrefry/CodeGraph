using CodeGraph.Data;
using CodeGraph.Data.Migration;
using CodeGraph.Models;

namespace CodeGraph.Services.Migration;

public sealed class Neo4jToMariaDbMigrationService : INeo4jToMariaDbMigrationService
{
    private const string RepositoriesArea = "repositories";
    private const string GraphArea = "graph";
    private readonly Neo4jToMariaDbMigrationPlanner _planner;
    private readonly INeo4jToMariaDbGraphExporter? _graphExporter;
    private readonly IGraphStore? _targetStore;

    public Neo4jToMariaDbMigrationService(Neo4jToMariaDbMigrationPlanner planner)
        : this(planner, null, null)
    {
    }

    public Neo4jToMariaDbMigrationService(
        Neo4jToMariaDbMigrationPlanner planner,
        INeo4jToMariaDbGraphExporter? graphExporter,
        IGraphStore? targetStore)
    {
        _planner = planner;
        _graphExporter = graphExporter;
        _targetStore = targetStore;
    }

    public async Task<Neo4jToMariaDbMigrationPlanReport> CreateDryRunReportAsync(
        DateTime? generatedAtUtc = null,
        CancellationToken ct = default)
    {
        if (_graphExporter is null)
        {
            return _planner.CreateDryRunReport(generatedAtUtc: generatedAtUtc);
        }

        try
        {
            var counts = await _graphExporter.CountRepositoriesAndGraphAsync(ct)
                .WaitAsync(TimeSpan.FromSeconds(5), ct);
            return _planner.CreateDryRunReport(
                generatedAtUtc: generatedAtUtc,
                repositoriesAndGraphCounts: counts);
        }
        catch
        {
            return _planner.CreateDryRunReport(generatedAtUtc: generatedAtUtc);
        }
    }

    public async Task<Neo4jToMariaDbGraphImportResult> RunRepositoriesAndGraphMigrationAsync(
        string? requestedByUsername = null,
        CancellationToken ct = default)
    {
        if (_graphExporter is null || _targetStore is null)
        {
            return new Neo4jToMariaDbGraphImportResult(
                RunId: null,
                Status: "blocked",
                Exported: new Neo4jToMariaDbGraphCounts(0, 0, 0, 0),
                Imported: new Neo4jToMariaDbGraphCounts(0, 0, 0, 0),
                SkippedEdges: 0,
                SkippedCrossRepoEdges: 0,
                Checkpoints: [],
                Message: "Neo4j graph exporter and MariaDB target store must both be registered before migration can run.",
                Error: "Exporter or target store missing.");
        }

        var checkpoints = new List<Neo4jToMariaDbMigrationCheckpoint>();

        try
        {
            await AddCheckpointAsync(RepositoriesArea, "export", "running", 0, "Exporting repositories and graph from Neo4j.");
            var export = await _graphExporter.ExportRepositoriesAndGraphAsync(ct);
            await AddCheckpointAsync(RepositoriesArea, "export", "completed", export.Repositories.Count, "Exported repositories.");
            await AddCheckpointAsync(GraphArea, "export", "completed", export.Nodes.Count + export.Edges.Count + export.CrossRepoEdges.Count,
                "Exported graph nodes, edges, and cross-repo edges.");

            await AddCheckpointAsync(RepositoriesArea, "import", "running", export.Repositories.Count, "Importing repositories into MariaDB.");
            foreach (var repository in export.Repositories)
            {
                ct.ThrowIfCancellationRequested();
                await _targetStore.UpsertRepositoryAsync(repository);
            }

            await AddCheckpointAsync(RepositoriesArea, "import", "completed", export.Repositories.Count, "Imported repositories.");

            await AddCheckpointAsync(GraphArea, "import-nodes", "running", export.Nodes.Count, "Importing graph nodes into MariaDB.");
            var nodeIdMap = await ImportNodesAsync(export.Nodes, ct);
            await AddCheckpointAsync(GraphArea, "import-nodes", "completed", nodeIdMap.Count, "Imported graph nodes and built source-to-target ID map.");

            var remappedEdges = RemapEdges(export.Edges, nodeIdMap, out var skippedEdges);
            await AddCheckpointAsync(GraphArea, "import-edges", "running", remappedEdges.Count, "Importing graph edges into MariaDB.");
            await _targetStore.InsertEdgeBatchAsync(remappedEdges, ct);
            await AddCheckpointAsync(GraphArea, "import-edges", "completed", remappedEdges.Count, "Imported graph edges.");

            var remappedCrossRepoEdges = RemapCrossRepoEdges(export.CrossRepoEdges, nodeIdMap, out var skippedCrossRepoEdges);
            await AddCheckpointAsync(GraphArea, "import-cross-repo-edges", "running", remappedCrossRepoEdges.Count,
                "Importing cross-repo graph edges into MariaDB.");
            await _targetStore.InsertCrossRepoEdgeBatchAsync(remappedCrossRepoEdges, ct);
            await AddCheckpointAsync(GraphArea, "import-cross-repo-edges", "completed", remappedCrossRepoEdges.Count,
                "Imported cross-repo graph edges.");

            var imported = new Neo4jToMariaDbGraphCounts(
                Repositories: export.Repositories.Count,
                Nodes: nodeIdMap.Count,
                Edges: remappedEdges.Count,
                CrossRepoEdges: remappedCrossRepoEdges.Count);

            return new Neo4jToMariaDbGraphImportResult(
                RunId: null,
                Status: "completed",
                Exported: export.Counts,
                Imported: imported,
                SkippedEdges: skippedEdges,
                SkippedCrossRepoEdges: skippedCrossRepoEdges,
                Checkpoints: checkpoints,
                Message: "Completed Neo4j to MariaDB repositories/graph migration.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Neo4jToMariaDbGraphImportResult(
                RunId: null,
                Status: "failed",
                Exported: new Neo4jToMariaDbGraphCounts(0, 0, 0, 0),
                Imported: new Neo4jToMariaDbGraphCounts(0, 0, 0, 0),
                SkippedEdges: 0,
                SkippedCrossRepoEdges: 0,
                Checkpoints: checkpoints,
                Error: ex.Message);
        }

        Task AddCheckpointAsync(
            string area,
            string stage,
            string status,
            int itemCount,
            string? message = null)
        {
            var checkpoint = new Neo4jToMariaDbMigrationCheckpoint(
                area,
                stage,
                status,
                itemCount,
                DateTime.UtcNow,
                message);
            checkpoints.Add(checkpoint);
            return Task.CompletedTask;
        }
    }

    private async Task<Dictionary<long, long>> ImportNodesAsync(
        IReadOnlyList<GraphNode> nodes,
        CancellationToken ct)
    {
        if (_targetStore is null)
        {
            return new Dictionary<long, long>();
        }

        var idMap = new Dictionary<long, long>();
        foreach (var projectGroup in nodes.GroupBy(node => node.Project, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var projectNodes = projectGroup.ToList();
            var upsertedIds = await _targetStore.UpsertNodeBatchAsync(projectNodes, ct);

            foreach (var sourceNode in projectNodes)
            {
                var qualifiedName = Truncate(sourceNode.QualifiedName, 1000);
                if (!upsertedIds.TryGetValue(GraphNodeKey.Create(sourceNode.Project, qualifiedName), out var targetId))
                {
                    var targetNode = await _targetStore.FindNodeByQualifiedNameAsync(sourceNode.Project, qualifiedName);
                    if (targetNode is null)
                    {
                        continue;
                    }

                    targetId = targetNode.Id;
                }

                idMap[sourceNode.Id] = targetId;
            }
        }

        return idMap;
    }

    private static List<GraphEdge> RemapEdges(
        IReadOnlyList<GraphEdge> edges,
        IReadOnlyDictionary<long, long> nodeIdMap,
        out int skippedEdges)
    {
        var remapped = new List<GraphEdge>(edges.Count);
        skippedEdges = 0;

        foreach (var edge in edges)
        {
            if (!nodeIdMap.TryGetValue(edge.SourceId, out var sourceId)
                || !nodeIdMap.TryGetValue(edge.TargetId, out var targetId))
            {
                skippedEdges++;
                continue;
            }

            remapped.Add(edge with
            {
                Id = 0,
                SourceId = sourceId,
                TargetId = targetId
            });
        }

        return remapped;
    }

    private static List<CrossRepoEdge> RemapCrossRepoEdges(
        IReadOnlyList<CrossRepoEdge> edges,
        IReadOnlyDictionary<long, long> nodeIdMap,
        out int skippedEdges)
    {
        var remapped = new List<CrossRepoEdge>(edges.Count);
        skippedEdges = 0;

        foreach (var edge in edges)
        {
            if (!nodeIdMap.TryGetValue(edge.SourceNodeId, out var sourceNodeId)
                || !nodeIdMap.TryGetValue(edge.TargetNodeId, out var targetNodeId))
            {
                skippedEdges++;
                continue;
            }

            remapped.Add(edge with
            {
                Id = 0,
                SourceNodeId = sourceNodeId,
                TargetNodeId = targetNodeId
            });
        }

        return remapped;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
