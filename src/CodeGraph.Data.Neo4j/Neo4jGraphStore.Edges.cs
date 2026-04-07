using Neo4j.Driver;
using CodeGraph.Models;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jGraphStore
{
    // ── Edges ─────────────────────────────────────────────────────────────

    public async Task InsertEdgeAsync(GraphEdge edge)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await UpsertRelationshipAsync(tx, edge.Type, [BuildEdgeParams(edge)]);
        });
    }

    public async Task InsertEdgeBatchAsync(IReadOnlyList<GraphEdge> edges, CancellationToken ct = default)
    {
        if (edges.Count == 0) return;

        await using var session = sessionFactory.GetSession();

        const int neo4jBatchSize = 100;

        foreach (var batch in Chunk(edges, neo4jBatchSize))
        {
            ct.ThrowIfCancellationRequested();

            foreach (var group in batch.GroupBy(e => e.Type))
            {
                var edgeParams = group.Select(BuildEdgeParams).ToList();
                await session.ExecuteWriteAsync(async tx =>
                {
                    await UpsertRelationshipAsync(tx, group.Key, edgeParams);
                });
            }
        }
    }

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceAsync(long sourceId, EdgeType? type = null)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cypher = type is null
                ? """
                    MATCH (source:CodeNode {appId: $sourceId})-[e]->(target:CodeNode)
                    RETURN elementId(e) AS elementId,
                           coalesce(e.project, source.project) AS project,
                           source.appId AS sourceId,
                           target.appId AS targetId,
                           type(e) AS type,
                           e.properties AS properties
                    """
                : $@"
                    MATCH (source:CodeNode {{appId: $sourceId}})-[e:{type}]->(target:CodeNode)
                    RETURN elementId(e) AS elementId,
                           coalesce(e.project, source.project) AS project,
                           source.appId AS sourceId,
                           target.appId AS targetId,
                           type(e) AS type,
                           e.properties AS properties
                    ";

            var cursor = await tx.RunAsync(cypher, new { sourceId });

            var results = new List<GraphEdge>();
            await foreach (var record in cursor)
                results.Add(MapEdgeRecord(record));
            return results;
        });
    }

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetAsync(long targetId, EdgeType? type = null)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cypher = type is null
                ? """
                    MATCH (source:CodeNode)-[e]->(target:CodeNode {appId: $targetId})
                    RETURN elementId(e) AS elementId,
                           coalesce(e.project, source.project) AS project,
                           source.appId AS sourceId,
                           target.appId AS targetId,
                           type(e) AS type,
                           e.properties AS properties
                    """
                : $@"
                    MATCH (source:CodeNode)-[e:{type}]->(target:CodeNode {{appId: $targetId}})
                    RETURN elementId(e) AS elementId,
                           coalesce(e.project, source.project) AS project,
                           source.appId AS sourceId,
                           target.appId AS targetId,
                           type(e) AS type,
                           e.properties AS properties
                    ";

            var cursor = await tx.RunAsync(cypher, new { targetId });

            var results = new List<GraphEdge>();
            await foreach (var record in cursor)
                results.Add(MapEdgeRecord(record));
            return results;
        });
    }

    public async Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetBatchAsync(IReadOnlyList<long> targetIds, EdgeType[]? types = null)
    {
        if (targetIds.Count == 0) return [];

        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cypher = """
                MATCH (source:CodeNode)-[e]->(target:CodeNode)
                WHERE target.appId IN $targetIds
                  AND ($types IS NULL OR type(e) IN $types)
                RETURN elementId(e) AS elementId,
                       coalesce(e.project, source.project) AS project,
                       source.appId AS sourceId,
                       target.appId AS targetId,
                       type(e) AS type,
                       e.properties AS properties
                """;

            var cursor = await tx.RunAsync(cypher, new
            {
                targetIds = targetIds.ToList(),
                types = types is { Length: > 0 } ? types.Select(t => t.ToString()).ToList() : null
            });

            var results = new List<GraphEdge>();
            await foreach (var record in cursor)
                results.Add(MapEdgeRecord(record));
            return results;
        });
    }

    public async Task<IReadOnlyList<GraphEdge>> FindAllEdgesByTypeAsync(EdgeType type)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cypher = $@"
                MATCH (source:CodeNode)-[e:{type}]->(target:CodeNode)
                RETURN elementId(e) AS elementId,
                       coalesce(e.project, source.project) AS project,
                       source.appId AS sourceId,
                       target.appId AS targetId,
                       type(e) AS type,
                       e.properties AS properties
                ";

            var cursor = await tx.RunAsync(cypher);

            var results = new List<GraphEdge>();
            await foreach (var record in cursor)
                results.Add(MapEdgeRecord(record));
            return results;
        });
    }

    public async Task<Dictionary<EdgeType, int>> GetEdgeCountsByTypeAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (:CodeNode)-[e]->(:CodeNode) RETURN type(e) AS type, count(e) AS count");
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
                MATCH (:CodeNode)-[e:CALLS]->(n:CodeNode {project: $project})
                WITH n.appId AS targetId, count(e) AS cnt
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
            var edgeTypes = edgeFilter is { Length: > 0 }
                ? edgeFilter.Select(ef => ef.ToString()).ToList()
                : null;

            var allResults = new List<TraversalEntry>();
            var currentFrontier = new HashSet<long> { startNodeId };
            var allVisited = new HashSet<long> { startNodeId };

            for (int depth = 1; depth <= maxDepth; depth++)
            {
                if (currentFrontier.Count == 0) break;

                var hopCursor = await tx.RunAsync(BuildTraversalCypher(direction), new
                {
                    frontier = currentFrontier.ToList(),
                    visited = allVisited.ToList(),
                    edgeTypes
                });

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

    private static async Task UpsertRelationshipAsync(
        IAsyncQueryRunner tx,
        EdgeType type,
        IReadOnlyList<Dictionary<string, object?>> edges)
    {
        var relationshipType = type.ToString();
        var cypher = $@"
            UNWIND $edges AS e
            MATCH (source:CodeNode {{appId: e.sourceId}})
            MATCH (target:CodeNode {{appId: e.targetId}})
            MERGE (source)-[rel:{relationshipType}]->(target)
            SET rel.project = e.project,
                rel.properties = e.properties
            SET rel += e.promotedProperties
            ";

        await tx.RunAsync(cypher,
            new { edges });
    }

    private static Dictionary<string, object?> BuildEdgeParams(GraphEdge edge) => new()
    {
        ["project"] = edge.Project,
        ["sourceId"] = edge.SourceId,
        ["targetId"] = edge.TargetId,
        ["properties"] = SerializeJson(edge.Properties),
        ["promotedProperties"] = ExtractPromotedEdgeProperties(edge.Properties)
    };

    private static string BuildTraversalCypher(TraceDirection direction) => direction switch
    {
        TraceDirection.Outbound => """
            MATCH (source:CodeNode)-[e]->(target:CodeNode)
            WHERE source.appId IN $frontier
              AND NOT (target.appId IN $visited)
              AND ($edgeTypes IS NULL OR type(e) IN $edgeTypes)
            RETURN target AS n,
                   type(e) AS edgeType,
                   source.appId AS parentNodeId,
                   e.properties AS edgeProperties
            ORDER BY n.name
            """,

        TraceDirection.Inbound => """
            MATCH (source:CodeNode)-[e]->(target:CodeNode)
            WHERE target.appId IN $frontier
              AND NOT (source.appId IN $visited)
              AND ($edgeTypes IS NULL OR type(e) IN $edgeTypes)
            RETURN source AS n,
                   type(e) AS edgeType,
                   target.appId AS parentNodeId,
                   e.properties AS edgeProperties
            ORDER BY n.name
            """,

        TraceDirection.Both => """
            MATCH (source:CodeNode)-[e]->(target:CodeNode)
            WHERE (source.appId IN $frontier OR target.appId IN $frontier)
              AND ($edgeTypes IS NULL OR type(e) IN $edgeTypes)
            WITH DISTINCT
                CASE WHEN source.appId IN $frontier THEN target ELSE source END AS n,
                type(e) AS edgeType,
                CASE WHEN source.appId IN $frontier THEN source.appId ELSE target.appId END AS parentNodeId,
                e.properties AS edgeProperties
            WHERE NOT (n.appId IN $visited)
            RETURN n, edgeType, parentNodeId, edgeProperties
            ORDER BY n.name
            """,

        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
    };

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
