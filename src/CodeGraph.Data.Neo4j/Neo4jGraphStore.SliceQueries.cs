using System.Text.Json;
using CodeGraph.Models;
using Neo4j.Driver;

namespace CodeGraph.Data.Neo4j;

/// <summary>Batched node/edge lookups used by incremental resolution.</summary>
public partial class Neo4jGraphStore
{
    public async Task<IReadOnlyList<GraphNode>> FindNodesByFilesAsync(
        string project, IReadOnlyCollection<string> filePaths, CancellationToken ct = default)
    {
        var paths = ExpandPathVariants(filePaths.ToList());
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var results = new List<GraphNode>();
            foreach (var batch in Chunk(paths, 1000))
            {
                ct.ThrowIfCancellationRequested();
                var cursor = await tx.RunAsync(
                    "MATCH (n:CodeNode {project: $project}) WHERE n.filePath IN $paths RETURN n",
                    new { project, paths = batch });
                await foreach (var record in cursor)
                    results.Add(MapCodeNode(record["n"].As<INode>()));
            }

            return (IReadOnlyList<GraphNode>)results.DistinctBy(node => node.Id).ToList();
        });
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByNamesAsync(
        string project, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        var distinct = names.Distinct(StringComparer.Ordinal).ToList();
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var results = new List<GraphNode>();
            foreach (var batch in Chunk(distinct, 1000))
            {
                ct.ThrowIfCancellationRequested();
                var cursor = await tx.RunAsync(
                    "MATCH (n:CodeNode {project: $project}) WHERE n.name IN $names RETURN n",
                    new { project, names = batch });
                await foreach (var record in cursor)
                    results.Add(MapCodeNode(record["n"].As<INode>()));
            }

            return (IReadOnlyList<GraphNode>)results;
        });
    }

    /// <summary>
    /// Edge properties are stored as a JSON string, so candidates are narrowed by a text match
    /// on the key and filtered exactly here.
    /// </summary>
    public async Task<IReadOnlyList<StoredEdge>> FindEdgesByPropertyAsync(
        string project, string propertyKey, IReadOnlyCollection<string> values, CancellationToken ct = default)
    {
        var wanted = values.ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
            return [];

        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (source:CodeNode {project: $project})-[edge]->(target:CodeNode)
                WHERE edge.properties CONTAINS $key
                RETURN source.qualifiedName AS sourceQN, source.filePath AS sourceFile,
                       target.qualifiedName AS targetQN, target.filePath AS targetFile,
                       type(edge) AS type, edge.properties AS properties
                """, new { project, key = $"\"{propertyKey}\"" });
            var results = new List<StoredEdge>();
            await foreach (var record in cursor)
            {
                ct.ThrowIfCancellationRequested();
                if (!Enum.TryParse<EdgeType>(record["type"].As<string>(), out var type))
                    continue;
                var properties = DeserializeJson(record["properties"].As<string?>()) ?? new();
                if (properties.GetValueOrDefault(propertyKey) is not { } value
                    || !wanted.Contains(value is JsonElement json ? json.ToString() : value.ToString() ?? ""))
                {
                    continue;
                }

                results.Add(new StoredEdge(
                    record["sourceQN"].As<string>(), record["sourceFile"].As<string?>(),
                    record["targetQN"].As<string>(), record["targetFile"].As<string?>(),
                    type, properties));
            }

            return (IReadOnlyList<StoredEdge>)results;
        });
    }
}
