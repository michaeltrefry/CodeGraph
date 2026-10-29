using System.Text.Json;
using CodeGraph.Models;
using Neo4j.Driver;

namespace CodeGraph.Data.Neo4j;

/// <summary>Batched node/edge lookups used by incremental resolution.</summary>
public partial class Neo4jGraphStore
{
    public async Task<IReadOnlyList<GraphNode>> FindNodesByFilesAsync(
        string repository, IReadOnlyCollection<string> filePaths, CancellationToken ct = default)
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
                    "MATCH (n:CodeNode {repository: $repository}) WHERE n.filePath IN $paths RETURN n",
                    new { repository, paths = batch });
                await foreach (var record in cursor)
                    results.Add(MapCodeNode(record["n"].As<INode>()));
            }

            return (IReadOnlyList<GraphNode>)results.DistinctBy(node => node.Id).ToList();
        });
    }

    public async Task<IReadOnlyList<string>> GetNodeFilePathsAsync(string repository, CancellationToken ct = default)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (n:CodeNode {repository: $repository}) WHERE n.filePath IS NOT NULL AND n.filePath <> '' " +
                "RETURN DISTINCT n.filePath AS filePath",
                new { repository });
            var paths = new List<string>();
            await foreach (var record in cursor)
            {
                ct.ThrowIfCancellationRequested();
                paths.Add(record["filePath"].As<string>());
            }

            return (IReadOnlyList<string>)paths;
        });
    }

    public async Task<IReadOnlyList<GraphNode>> FindNodesByNamesAsync(
        string repository, IReadOnlyCollection<string> names, CancellationToken ct = default)
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
                    "MATCH (n:CodeNode {repository: $repository}) WHERE n.name IN $names RETURN n",
                    new { repository, names = batch });
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
        string repository,
        IReadOnlyCollection<EdgeType> types,
        string propertyKey,
        IReadOnlyCollection<string> values,
        CancellationToken ct = default)
    {
        var wanted = values.ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0 || types.Count == 0)
            return [];
        var typeNames = types.Distinct().Select(type => type.ToString()).ToList();

        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (source:CodeNode {repository: $repository})-[edge]->(target:CodeNode)
                WHERE type(edge) IN $types AND edge.properties CONTAINS $key
                RETURN source.qualifiedName AS sourceQN, source.filePath AS sourceFile,
                       target.qualifiedName AS targetQN, target.filePath AS targetFile,
                       type(edge) AS type, edge.properties AS properties
                """, new { repository, types = typeNames, key = $"\"{propertyKey}\"" });
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
