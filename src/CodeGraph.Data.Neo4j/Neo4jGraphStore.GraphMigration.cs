using CodeGraph.Data.Migration;
using CodeGraph.Models;
using Neo4j.Driver;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore : INeo4jToMariaDbGraphExporter
{
    public async Task<Neo4jToMariaDbGraphCounts> CountRepositoriesAndGraphAsync(CancellationToken ct = default)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync($$"""
                MATCH (r:{{RepositoryMetadataLabel}})
                WITH count(r) AS repositories
                MATCH (n:CodeNode)
                WITH repositories, count(n) AS nodes
                MATCH (:CodeNode)-[edge]->(:CodeNode)
                WITH repositories, nodes, count(edge) AS edges
                MATCH (crossRepoEdge:CrossRepoEdge)
                RETURN repositories, nodes, edges, count(crossRepoEdge) AS crossRepoEdges
                """);

            await cursor.FetchAsync();
            ct.ThrowIfCancellationRequested();

            return new Neo4jToMariaDbGraphCounts(
                cursor.Current["repositories"].As<int>(),
                cursor.Current["nodes"].As<int>(),
                cursor.Current["edges"].As<int>(),
                cursor.Current["crossRepoEdges"].As<int>());
        });
    }

    public async Task<Neo4jToMariaDbGraphExport> ExportRepositoriesAndGraphAsync(CancellationToken ct = default)
    {
        var repositories = (await ListRepositoriesAsync())
            .Select(ToRepositoryEntity)
            .ToList();
        var nodes = await ExportCodeNodesAsync(ct);
        var edges = await ExportCodeEdgesAsync(ct);
        var crossRepoEdges = await GetAllCrossRepoEdgesAsync();

        ct.ThrowIfCancellationRequested();

        return new Neo4jToMariaDbGraphExport(
            repositories,
            nodes,
            edges,
            crossRepoEdges);
    }

    private async Task<IReadOnlyList<GraphNode>> ExportCodeNodesAsync(CancellationToken ct)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (n:CodeNode)
                RETURN n
                ORDER BY n.project, n.qualifiedName
                """);

            var results = new List<GraphNode>();
            await foreach (var record in cursor)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(MapCodeNode(record["n"].As<INode>()));
            }

            return results;
        });
    }

    private async Task<IReadOnlyList<GraphEdge>> ExportCodeEdgesAsync(CancellationToken ct)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (source:CodeNode)-[e]->(target:CodeNode)
                RETURN elementId(e) AS elementId,
                       coalesce(e.project, source.project) AS project,
                       source.appId AS sourceId,
                       target.appId AS targetId,
                       type(e) AS type,
                       e.properties AS properties
                ORDER BY project, sourceId, targetId, type
                """);

            var results = new List<GraphEdge>();
            await foreach (var record in cursor)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(MapEdgeRecord(record));
            }

            return results;
        });
    }

    private static RepositoryEntity ToRepositoryEntity(ProjectInfo repository) => new()
    {
        Name = repository.Name,
        RepoUrl = repository.RepoUrl,
        SourceGroup = repository.SourceGroup,
        LocalPath = repository.LocalPath,
        LastCommitSha = repository.LastCommitSha,
        IndexedAt = repository.IndexedAt,
        Language = repository.Language,
        Framework = repository.Framework,
        IsFoundational = repository.IsFoundational,
        Properties = repository.Properties is null ? null : SerializeJson(repository.Properties)
    };
}
