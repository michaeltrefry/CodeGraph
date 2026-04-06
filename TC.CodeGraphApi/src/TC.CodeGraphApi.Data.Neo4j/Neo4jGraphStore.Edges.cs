using Neo4j.Driver;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Data.Neo4j;

public partial class Neo4jGraphStore
{
    // ── Edges ─────────────────────────────────────────────────────────────

    public async Task InsertEdgeAsync(GraphEdge edge)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            // Store edges as separate EdgeRecord nodes linked to source/target
            // This approach avoids the need for dynamic relationship types
            await tx.RunAsync("""
                MERGE (e:EdgeRecord {sourceId: $sourceId, targetId: $targetId, type: $type})
                SET e.project = $project,
                    e.properties = $properties
                """,
                new
                {
                    project = edge.Project,
                    sourceId = edge.SourceId,
                    targetId = edge.TargetId,
                    type = edge.Type.ToString(),
                    properties = SerializeJson(edge.Properties)
                });
        });
    }

    public async Task InsertEdgeBatchAsync(IReadOnlyList<GraphEdge> edges, CancellationToken ct = default)
    {
        if (edges.Count == 0) return;

        await using var session = sessionFactory.GetSession();

        // Smaller batches for Neo4j — large UNWIND+MERGE transactions are expensive
        const int neo4jBatchSize = 100;

        foreach (var batch in Chunk(edges, neo4jBatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var edgeParams = batch.Select(e => new Dictionary<string, object?>
            {
                ["project"] = e.Project,
                ["sourceId"] = e.SourceId,
                ["targetId"] = e.TargetId,
                ["type"] = e.Type.ToString(),
                ["properties"] = SerializeJson(e.Properties)
            }).ToList();

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("""
                    UNWIND $edges AS e
                    MERGE (edge:EdgeRecord {sourceId: e.sourceId, targetId: e.targetId, type: e.type})
                    SET edge.project = e.project,
                        edge.properties = e.properties
                    """,
                    new { edges = edgeParams });
            });
        }
    }

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceAsync(long sourceId, EdgeType? type = null)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cypher = type is null
                ? "MATCH (e:EdgeRecord {sourceId: $sourceId}) RETURN e"
                : "MATCH (e:EdgeRecord {sourceId: $sourceId, type: $type}) RETURN e";

            var parameters = new Dictionary<string, object?> { ["sourceId"] = sourceId };
            if (type is not null) parameters["type"] = type.Value.ToString();

            var cursor = await tx.RunAsync(cypher, parameters);
            var results = new List<GraphEdge>();
            await foreach (var record in cursor)
            {
                var node = record["e"].As<INode>();
                results.Add(MapEdgeNode(node));
            }
            return results;
        });
    }

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetAsync(long targetId, EdgeType? type = null)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cypher = type is null
                ? "MATCH (e:EdgeRecord {targetId: $targetId}) RETURN e"
                : "MATCH (e:EdgeRecord {targetId: $targetId, type: $type}) RETURN e";

            var parameters = new Dictionary<string, object?> { ["targetId"] = targetId };
            if (type is not null) parameters["type"] = type.Value.ToString();

            var cursor = await tx.RunAsync(cypher, parameters);
            var results = new List<GraphEdge>();
            await foreach (var record in cursor)
            {
                var node = record["e"].As<INode>();
                results.Add(MapEdgeNode(node));
            }
            return results;
        });
    }

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetBatchAsync(IReadOnlyList<long> targetIds, EdgeType[]? types = null)
    {
        if (targetIds.Count == 0) return [];

        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cypher = types is { Length: > 0 }
                ? "MATCH (e:EdgeRecord) WHERE e.targetId IN $targetIds AND e.type IN $types RETURN e"
                : "MATCH (e:EdgeRecord) WHERE e.targetId IN $targetIds RETURN e";

            var parameters = new Dictionary<string, object?> { ["targetIds"] = targetIds.ToList() };
            if (types is { Length: > 0 })
                parameters["types"] = types.Select(t => t.ToString()).ToList();

            var cursor = await tx.RunAsync(cypher, parameters);
            var results = new List<GraphEdge>();
            await foreach (var record in cursor)
                results.Add(MapEdgeNode(record["e"].As<INode>()));
            return results;
        });
    }

    public async Task<IReadOnlyList<GraphEdge>> FindAllEdgesByTypeAsync(EdgeType type)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (e:EdgeRecord {type: $type}) RETURN e",
                new { type = type.ToString() });
            var results = new List<GraphEdge>();
            await foreach (var record in cursor)
                results.Add(MapEdgeNode(record["e"].As<INode>()));
            return results;
        });
    }

    public async Task<Dictionary<EdgeType, int>> GetEdgeCountsByTypeAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (e:EdgeRecord) RETURN e.type AS type, count(e) AS count");
            var result = new Dictionary<EdgeType, int>();
            await foreach (var record in cursor)
            {
                var typeStr = record["type"].As<string>();
                if (Enum.TryParse<EdgeType>(typeStr, out var et))
                    result[et] = record["count"].As<int>();
            }
            return result;
        });
    }

    public async Task<Dictionary<long, int>> GetCallFanInAsync(string project, int minFanIn)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (e:EdgeRecord {type: 'CALLS'})
                MATCH (n:CodeNode {project: $project, appId: e.targetId})
                WITH e.targetId AS targetId, count(e) AS cnt
                WHERE cnt >= $minFanIn
                RETURN targetId, cnt
                """,
                new { project, minFanIn });

            var result = new Dictionary<long, int>();
            await foreach (var record in cursor)
                result[record["targetId"].As<long>()] = record["cnt"].As<int>();
            return result;
        });
    }

    // ── Cross-Repo Edges ──────────────────────────────────────────────────

    public async Task InsertCrossRepoEdgeAsync(CrossRepoEdge edge)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MERGE (e:CrossRepoEdge {sourceNodeId: $sourceNodeId, targetNodeId: $targetNodeId, type: $type})
                SET e.sourceProject = $sourceProject,
                    e.targetProject = $targetProject,
                    e.properties = $properties
                """,
                new
                {
                    sourceProject = edge.SourceProject,
                    targetProject = edge.TargetProject,
                    sourceNodeId = edge.SourceNodeId,
                    targetNodeId = edge.TargetNodeId,
                    type = edge.Type.ToString(),
                    properties = SerializeJson(edge.Properties)
                });
        });
    }

    public async Task InsertCrossRepoEdgeBatchAsync(IReadOnlyList<CrossRepoEdge> edges, CancellationToken ct = default)
    {
        if (edges.Count == 0) return;

        await using var session = sessionFactory.GetSession();

        const int neo4jBatchSize = 100;

        foreach (var batch in Chunk(edges, neo4jBatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var edgeParams = batch.Select(e => new Dictionary<string, object?>
            {
                ["sourceProject"] = e.SourceProject,
                ["targetProject"] = e.TargetProject,
                ["sourceNodeId"] = e.SourceNodeId,
                ["targetNodeId"] = e.TargetNodeId,
                ["type"] = e.Type.ToString(),
                ["properties"] = SerializeJson(e.Properties)
            }).ToList();

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("""
                    UNWIND $edges AS e
                    MERGE (edge:CrossRepoEdge {sourceNodeId: e.sourceNodeId, targetNodeId: e.targetNodeId, type: e.type})
                    SET edge.sourceProject = e.sourceProject,
                        edge.targetProject = e.targetProject,
                        edge.properties = e.properties
                    """,
                    new { edges = edgeParams });
            });
        }
    }

    public async Task<IReadOnlyList<CrossRepoEdge>> FindCrossRepoEdgesAsync(
        string project, EdgeType? type = null)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cypher = "MATCH (e:CrossRepoEdge) WHERE e.sourceProject = $project OR e.targetProject = $project";
            var parameters = new Dictionary<string, object?> { ["project"] = project };

            if (type is not null)
            {
                cypher += " AND e.type = $type";
                parameters["type"] = type.Value.ToString();
            }
            cypher += " RETURN e";

            var cursor = await tx.RunAsync(cypher, parameters);
            var results = new List<CrossRepoEdge>();
            await foreach (var record in cursor)
            {
                var node = record["e"].As<INode>();
                results.Add(MapCrossRepoEdgeNode(node));
            }
            return results;
        });
    }

    public async Task<IReadOnlyList<string>> FindProjectsWithNoCrossRepoEdgesAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (r:Repository)
                WHERE NOT EXISTS {
                    MATCH (e:CrossRepoEdge)
                    WHERE e.sourceProject = r.name OR e.targetProject = r.name
                }
                RETURN r.name AS name ORDER BY name
                """);
            var results = new List<string>();
            await foreach (var record in cursor)
                results.Add(record["name"].As<string>());
            return results;
        });
    }

    public async Task<IReadOnlyList<CrossRepoEdge>> GetAllCrossRepoEdgesAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("MATCH (e:CrossRepoEdge) RETURN e");
            var results = new List<CrossRepoEdge>();
            await foreach (var record in cursor)
                results.Add(MapCrossRepoEdgeNode(record["e"].As<INode>()));
            return results;
        });
    }

    // ── Traversal ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TraversalEntry>> TraverseAsync(
        long startNodeId, TraceDirection direction, int maxDepth,
        EdgeType[]? edgeFilter = null, double minConfidence = 0)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var edgeFilterClause = edgeFilter is { Length: > 0 }
                ? "AND e.type IN $edgeTypes"
                : "";
            var edgeTypes = edgeFilter?.Select(ef => ef.ToString()).ToList();

            // BFS traversal: expand one hop at a time from application side
            var allResults = new List<TraversalEntry>();
            var currentFrontier = new HashSet<long> { startNodeId };
            var allVisited = new HashSet<long> { startNodeId };

            for (int depth = 1; depth <= maxDepth; depth++)
            {
                if (currentFrontier.Count == 0) break;

                var frontierList = currentFrontier.ToList();
                string whereClause = direction switch
                {
                    TraceDirection.Outbound => "e.sourceId IN $frontier",
                    TraceDirection.Inbound => "e.targetId IN $frontier",
                    TraceDirection.Both => "(e.sourceId IN $frontier OR e.targetId IN $frontier)",
                    _ => throw new ArgumentOutOfRangeException(nameof(direction))
                };

                string selectNodeId = direction switch
                {
                    TraceDirection.Outbound => "e.targetId",
                    TraceDirection.Inbound => "e.sourceId",
                    TraceDirection.Both =>
                        "CASE WHEN e.sourceId IN $frontier THEN e.targetId ELSE e.sourceId END",
                    _ => throw new ArgumentOutOfRangeException(nameof(direction))
                };

                string selectParentId = direction switch
                {
                    TraceDirection.Outbound => "e.sourceId",
                    TraceDirection.Inbound => "e.targetId",
                    TraceDirection.Both =>
                        "CASE WHEN e.sourceId IN $frontier THEN e.sourceId ELSE e.targetId END",
                    _ => throw new ArgumentOutOfRangeException(nameof(direction))
                };

                var hopCypher =
                    "MATCH (e:EdgeRecord) " +
                    "WHERE " + whereClause + " " + edgeFilterClause + " " +
                    "WITH DISTINCT " + selectNodeId + " AS nodeId, e.type AS edgeType, " +
                    selectParentId + " AS parentNodeId, e.properties AS edgeProperties " +
                    "WHERE NOT nodeId IN $visited " +
                    "MATCH (n:CodeNode {appId: nodeId}) " +
                    "RETURN n, edgeType, parentNodeId, edgeProperties " +
                    "ORDER BY n.name";

                var parameters = new Dictionary<string, object?>
                {
                    ["frontier"] = frontierList,
                    ["visited"] = allVisited.ToList()
                };
                if (edgeTypes is not null) parameters["edgeTypes"] = edgeTypes;

                var hopCursor = await tx.RunAsync(hopCypher, parameters);
                var nextFrontier = new HashSet<long>();

                await foreach (var record in hopCursor)
                {
                    var codeNode = MapCodeNode(record["n"].As<INode>());
                    var edgeType = Enum.Parse<EdgeType>(record["edgeType"].As<string>());
                    var parentId = record["parentNodeId"].As<long?>();
                    var edgeProps = DeserializeJson(record["edgeProperties"].As<string?>());

                    allResults.Add(new TraversalEntry(codeNode, depth, edgeType, parentId, edgeProps));
                    nextFrontier.Add(codeNode.Id);
                    allVisited.Add(codeNode.Id);
                }

                currentFrontier = nextFrontier;
            }

            return allResults;
        });
    }

    // ── File Hashes ───────────────────────────────────────────────────────

    public async Task<Dictionary<string, string>> GetFileHashesAsync(string project)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (f:FileHash {project: $project}) RETURN f.relPath AS relPath, f.contentHash AS contentHash",
                new { project });
            var result = new Dictionary<string, string>();
            await foreach (var record in cursor)
                result[record["relPath"].As<string>()] = record["contentHash"].As<string>();
            return result;
        });
    }

    public async Task UpsertFileHashBatchAsync(string project, Dictionary<string, string> hashes, CancellationToken ct = default)
    {
        if (hashes.Count == 0) return;

        await using var session = sessionFactory.GetSession();
        var items = hashes.Select(kv => new Dictionary<string, object?>
        {
            ["project"] = project,
            ["relPath"] = kv.Key,
            ["contentHash"] = kv.Value
        }).ToList();

        const int neo4jBatchSize = 200;
        foreach (var batch in Chunk(items.ToArray() as IReadOnlyList<Dictionary<string, object?>> ?? items, neo4jBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("""
                    UNWIND $items AS h
                    MERGE (f:FileHash {project: h.project, relPath: h.relPath})
                    SET f.contentHash = h.contentHash
                    """,
                    new { items = batch });
            });
        }
    }

    public async Task DeleteFileHashesAsync(string project, IReadOnlyList<string> relPaths)
    {
        if (relPaths.Count == 0) return;

        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (f:FileHash {project: $project}) WHERE f.relPath IN $relPaths DELETE f",
                new { project, relPaths });
        });
    }

    // ── Edge Helpers ──────────────────────────────────────────────────────

    private static GraphEdge MapEdgeNode(INode node)
    {
        var id = node.Properties.ContainsKey("appId") ? node["appId"].As<long>() : node.ElementId.GetHashCode();
        return new()
        {
            Id = id,
            Project = node["project"].As<string>(),
            SourceId = node["sourceId"].As<long>(),
            TargetId = node["targetId"].As<long>(),
            Type = Enum.Parse<EdgeType>(node["type"].As<string>()),
            Properties = DeserializeJson(GetStringOrNull(node, "properties")) ?? new()
        };
    }

    private static CrossRepoEdge MapCrossRepoEdgeNode(INode node)
    {
        var id = node.Properties.ContainsKey("appId") ? node["appId"].As<long>() : node.ElementId.GetHashCode();
        return new()
        {
            Id = id,
            SourceProject = node["sourceProject"].As<string>(),
            TargetProject = node["targetProject"].As<string>(),
            SourceNodeId = node["sourceNodeId"].As<long>(),
            TargetNodeId = node["targetNodeId"].As<long>(),
            Type = Enum.Parse<EdgeType>(node["type"].As<string>()),
            Properties = DeserializeJson(GetStringOrNull(node, "properties")) ?? new()
        };
    }

}
