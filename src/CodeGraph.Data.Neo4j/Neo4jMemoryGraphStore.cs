using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using CodeGraph.Models.Memory;

namespace CodeGraph.Data.Neo4j;

public class Neo4jMemoryGraphStore : IMemoryGraphStore
{
    private readonly Neo4jSessionFactory _sessionFactory;
    private readonly ILogger<Neo4jMemoryGraphStore> _logger;

    public Neo4jMemoryGraphStore(Neo4jSessionFactory sessionFactory, ILogger<Neo4jMemoryGraphStore> logger)
    {
        _sessionFactory = sessionFactory;
        _logger = logger;
    }

    public async Task UpsertEntitiesBatchAsync(IReadOnlyList<MemoryEntity> entities)
    {
        if (entities.Count == 0) return;

        await using var session = _sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                UNWIND $batch AS item
                MERGE (e:MemoryEntity {id: item.id})
                ON CREATE SET
                    e.label = item.label,
                    e.type = item.type,
                    e.summary = item.summary,
                    e.source = item.source,
                    e.embedding = item.embedding,
                    e.createdAt = datetime(item.createdAt),
                    e.updatedAt = datetime(item.updatedAt)
                ON MATCH SET
                    e.summary = CASE
                        WHEN e.summary CONTAINS item.summary THEN e.summary
                        WHEN size(e.summary) > 1000 THEN item.summary
                        ELSE e.summary + ' | ' + item.summary
                    END,
                    e.embedding = COALESCE(item.embedding, e.embedding),
                    e.updatedAt = datetime(item.updatedAt)
                """,
                new
                {
                    batch = entities.Select(e => new
                    {
                        id = e.Id,
                        label = e.Label,
                        type = e.Type,
                        summary = e.Summary,
                        source = e.Source,
                        embedding = e.Embedding,
                        createdAt = e.CreatedAt.ToString("O"),
                        updatedAt = e.UpdatedAt.ToString("O"),
                    }).ToList(),
                });
        });
    }

    public async Task AddRelationshipsBatchAsync(IReadOnlyList<MemoryRelationship> relationships)
    {
        if (relationships.Count == 0) return;

        // Deduplicate within batch — keep most recent for each (from, to, relationship, context)
        var deduped = relationships
            .GroupBy(r => (r.FromId, r.ToId, r.RelationshipType, r.Context))
            .Select(g => g.OrderByDescending(r => r.Timestamp).First())
            .ToList();

        await using var session = _sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                UNWIND $batch AS item
                MATCH (a:MemoryEntity {id: item.fromId})
                MATCH (b:MemoryEntity {id: item.toId})
                CREATE (a)-[:RELATES_TO {
                    relationship: item.relationship,
                    context: item.context,
                    source: item.source,
                    timestamp: datetime(item.timestamp),
                    supersedes: item.supersedes,
                    embedding: item.embedding
                }]->(b)
                """,
                new
                {
                    batch = deduped.Select(r => new
                    {
                        fromId = r.FromId,
                        toId = r.ToId,
                        relationship = r.RelationshipType,
                        context = r.Context,
                        source = r.Source,
                        timestamp = r.Timestamp.ToString("O"),
                        supersedes = r.Supersedes,
                        embedding = r.Embedding,
                    }).ToList(),
                });
        });
    }

    public async Task CreateObservationAsync(MemoryObservation obs, string? fromEntityId = null, string? toEntityId = null)
    {
        await using var session = _sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                CREATE (o:MemoryObservation {
                    id: $id,
                    claim: $claim,
                    conflictsWith: $conflictsWith,
                    source: $source,
                    timestamp: datetime($timestamp),
                    resolved: $resolved,
                    resolution: $resolution,
                    resolvedByMemoryId: $resolvedByMemoryId
                })
                WITH o
                OPTIONAL MATCH (e1:MemoryEntity {id: $fromEntityId})
                OPTIONAL MATCH (e2:MemoryEntity {id: $toEntityId})
                FOREACH (_ IN CASE WHEN e1 IS NOT NULL THEN [1] ELSE [] END |
                    CREATE (o)-[:OBSERVES]->(e1))
                FOREACH (_ IN CASE WHEN e2 IS NOT NULL THEN [1] ELSE [] END |
                    CREATE (o)-[:OBSERVES]->(e2))
                """,
                new
                {
                    id = obs.Id,
                    claim = obs.Claim,
                    conflictsWith = obs.ConflictsWith,
                    source = obs.Source,
                    timestamp = obs.Timestamp.ToString("O"),
                    resolved = obs.Resolved,
                    resolution = obs.Resolution,
                    resolvedByMemoryId = obs.ResolvedByMemoryId,
                    fromEntityId = fromEntityId ?? "",
                    toEntityId = toEntityId ?? "",
                });
        });
    }

    public async Task ResolveObservationAsync(string observationId, string resolution, string? resolvedByMemoryId)
    {
        await using var session = _sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                MATCH (o:MemoryObservation {id: $id})
                SET o.resolved = true,
                    o.resolution = $resolution,
                    o.resolvedByMemoryId = $resolvedByMemoryId
                """,
                new { id = observationId, resolution, resolvedByMemoryId });
        });
    }

    public async Task<List<(MemoryEntity Entity, double Score)>> VectorSearchAsync(float[] queryEmbedding, int topK = 5)
    {
        int[] overfetchFactors = [5, 20, 100];
        foreach (var factor in overfetchFactors)
        {
            var results = await VectorSearchWithOverfetchAsync(queryEmbedding, topK, topK * factor);
            if (results.Count >= topK)
                return results;
            if (results.Count > 0 && factor == overfetchFactors[^1])
                return results;
        }
        return [];
    }

    private async Task<List<(MemoryEntity Entity, double Score)>> VectorSearchWithOverfetchAsync(
        float[] queryEmbedding, int topK, int fetchSize)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var results = new List<(MemoryEntity, double)>();

        try
        {
            var result = await session.RunAsync(
                """
                CALL db.index.vector.queryNodes('memory_entity_embedding', $fetchSize, $embedding)
                YIELD node, score
                RETURN node.id AS id, node.label AS label, node.type AS type,
                       node.summary AS summary, node.source AS source,
                       node.createdAt AS createdAt, node.updatedAt AS updatedAt, score
                LIMIT $topK
                """,
                new { fetchSize, topK, embedding = queryEmbedding });

            await foreach (var record in result)
            {
                var entity = new MemoryEntity
                {
                    Id = record["id"].As<string>(),
                    Label = record["label"].As<string>(),
                    Type = record["type"].As<string>(),
                    Summary = record["summary"].As<string>(),
                    Source = record["source"].As<string>(),
                    CreatedAt = record["createdAt"].As<DateTimeOffset>().UtcDateTime,
                    UpdatedAt = record["updatedAt"].As<DateTimeOffset>().UtcDateTime,
                };
                results.Add((entity, record["score"].As<double>()));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory vector search failed");
        }

        return results;
    }

    public async Task<List<MemoryEntity>> TextSearchAsync(string query, int limit = 5)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        try
        {
            return await TextSearchWithFulltextAsync(session, query, limit);
        }
        catch (ClientException ex) when (IsMissingMemoryFulltextIndex(ex))
        {
            _logger.LogWarning(
                "Memory fulltext index is missing; falling back to scan-based text search.");
            return await TextSearchWithFallbackAsync(session, query, limit);
        }
    }

    public async Task<List<MemoryRelationshipDetail>> GetRelationshipsAsync(string entityId)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var results = new List<MemoryRelationshipDetail>();

        var cypher =
            "MATCH (e:MemoryEntity {id: $entityId})-[rel:RELATES_TO]-(other:MemoryEntity) " +
            "WITH e, rel, other, startNode(rel) AS from, endNode(rel) AS to " +
            "RETURN DISTINCT " +
            "CASE WHEN from.id = $entityId THEN 'outgoing' ELSE 'incoming' END AS direction, " +
            "rel.relationship AS relationship, " +
            "CASE WHEN from.id = $entityId THEN to.label ELSE from.label END AS targetLabel, " +
            "CASE WHEN from.id = $entityId THEN to.id ELSE from.id END AS targetId, " +
            "rel.context AS context, " +
            "rel.timestamp AS timestamp, " +
            "rel.embedding AS embedding " +
            "ORDER BY rel.timestamp DESC";

        var result = await session.RunAsync(cypher, new { entityId });

        await foreach (var record in result)
        {
            float[]? embedding = null;
            try
            {
                var embeddingList = record["embedding"].As<List<object>>();
                if (embeddingList != null)
                    embedding = embeddingList.Select(v => Convert.ToSingle(v)).ToArray();
            }
            catch { /* null or missing embedding */ }

            results.Add(new MemoryRelationshipDetail
            {
                Direction = record["direction"].As<string>(),
                RelationshipType = record["relationship"].As<string>(),
                TargetLabel = record["targetLabel"].As<string>(),
                TargetId = record["targetId"].As<string>(),
                Context = record["context"].As<string?>(),
                Timestamp = record["timestamp"].As<DateTimeOffset>().UtcDateTime,
                Embedding = embedding,
            });
        }

        return results;
    }

    public async Task<(List<MemoryEntity> Entities, List<MemoryRelationshipDetail> Relationships)> GetSubgraphAsync(
        IReadOnlyList<string> seedIds, int hops = 2, int maxNodes = 20, int maxRelsPerPair = 5)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);

        var clampedHops = Math.Clamp(hops, 1, 5);
        var entityCypher =
            "UNWIND $seedIds AS seedId " +
            "MATCH (seed:MemoryEntity {id: seedId}) " +
            $"OPTIONAL MATCH (seed)-[:RELATES_TO*1..{clampedHops}]-(other:MemoryEntity) " +
            "WITH collect(DISTINCT seed) + collect(DISTINCT other) AS allNodes " +
            "UNWIND allNodes AS n " +
            "WITH DISTINCT n WHERE n IS NOT NULL " +
            "RETURN n.id AS id, n.label AS label, n.type AS type, " +
            "       n.summary AS summary, n.source AS source, " +
            "       n.createdAt AS createdAt, n.updatedAt AS updatedAt " +
            "ORDER BY n.updatedAt DESC " +
            "LIMIT $maxNodes";

        var entities = new List<MemoryEntity>();
        var entityResult = await session.RunAsync(entityCypher, new { seedIds, maxNodes });
        await foreach (var record in entityResult)
        {
            entities.Add(new MemoryEntity
            {
                Id = record["id"].As<string>(),
                Label = record["label"].As<string>(),
                Type = record["type"].As<string>(),
                Summary = record["summary"].As<string>(),
                Source = record["source"].As<string>(),
                CreatedAt = record["createdAt"].As<DateTimeOffset>().UtcDateTime,
                UpdatedAt = record["updatedAt"].As<DateTimeOffset>().UtcDateTime,
            });
        }

        if (entities.Count == 0)
            return (entities, []);

        var nodeIds = entities.Select(e => e.Id).ToList();
        var relationships = new List<MemoryRelationshipDetail>();
        var relResult = await session.RunAsync(
            """
            MATCH (a:MemoryEntity)-[rel:RELATES_TO]->(b:MemoryEntity)
            WHERE a.id IN $nodeIds AND b.id IN $nodeIds
            WITH a, b, rel
            ORDER BY rel.timestamp DESC
            WITH a, b, collect(rel) AS rels
            UNWIND rels[0..$maxRelsPerPair] AS rel
            RETURN a.id AS fromId, b.id AS toId, a.label AS fromLabel, b.label AS toLabel,
                   rel.relationship AS relationship, rel.context AS context,
                   rel.timestamp AS timestamp, rel.embedding AS embedding
            ORDER BY rel.timestamp DESC
            """,
            new { nodeIds, maxRelsPerPair });

        await foreach (var record in relResult)
        {
            float[]? embedding = null;
            try
            {
                var embeddingList = record["embedding"].As<List<object>>();
                if (embeddingList != null)
                    embedding = embeddingList.Select(v => Convert.ToSingle(v)).ToArray();
            }
            catch { /* null or missing embedding */ }

            var fromId = record["fromId"].As<string>();
            var toId = record["toId"].As<string>();
            relationships.Add(new MemoryRelationshipDetail
            {
                Direction = "outgoing",
                RelationshipType = record["relationship"].As<string>(),
                TargetLabel = record["toLabel"].As<string>(),
                TargetId = toId,
                Context = record["context"].As<string?>(),
                Timestamp = record["timestamp"].As<DateTimeOffset>().UtcDateTime,
                Embedding = embedding,
                SourceId = fromId,
            });
            relationships.Add(new MemoryRelationshipDetail
            {
                Direction = "incoming",
                RelationshipType = record["relationship"].As<string>(),
                TargetLabel = record["fromLabel"].As<string>(),
                TargetId = fromId,
                Context = record["context"].As<string?>(),
                Timestamp = record["timestamp"].As<DateTimeOffset>().UtcDateTime,
                Embedding = embedding,
                SourceId = toId,
            });
        }

        return (entities, relationships);
    }

    public async Task<List<MemoryObservation>> GetUnresolvedObservationsForEntitiesAsync(IEnumerable<string> entityIds)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var results = new List<MemoryObservation>();

        var result = await session.RunAsync(
            """
            CALL {
                MATCH (o:MemoryObservation {resolved: false})-[:OBSERVES]->(e:MemoryEntity)
                WHERE e.id IN $entityIds
                RETURN DISTINCT o
            UNION
                MATCH (o:MemoryObservation {resolved: false})
                WHERE NOT EXISTS { (o)-[:OBSERVES]->() }
                  AND ANY(id IN $entityIds WHERE o.claim CONTAINS id OR o.conflictsWith CONTAINS id)
                RETURN DISTINCT o
            }
            RETURN o.id AS id, o.claim AS claim, o.conflictsWith AS conflictsWith,
                   o.source AS source, o.timestamp AS timestamp,
                   o.resolved AS resolved, o.resolution AS resolution,
                   o.resolvedByMemoryId AS resolvedByMemoryId
            ORDER BY o.timestamp DESC
            """,
            new { entityIds = entityIds.ToList() });

        await foreach (var record in result)
        {
            results.Add(new MemoryObservation
            {
                Id = record["id"].As<string>(),
                Claim = record["claim"].As<string>(),
                ConflictsWith = record["conflictsWith"].As<string>(),
                Source = record["source"].As<string>(),
                Timestamp = record["timestamp"].As<DateTimeOffset>().UtcDateTime,
                Resolved = record["resolved"].As<bool>(),
                Resolution = record["resolution"].As<string?>(),
                ResolvedByMemoryId = record["resolvedByMemoryId"].As<string?>(),
            });
        }

        return results;
    }

    public async Task<MemoryGraphSnapshot> GetFullGraphAsync(int limit = 200, int skip = 0)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var snapshot = new MemoryGraphSnapshot();

        var entityResult = await session.RunAsync(
            """
            MATCH (e:MemoryEntity)
            RETURN e.id AS id, e.label AS label, e.type AS type,
                   e.summary AS summary, e.source AS source,
                   e.createdAt AS createdAt, e.updatedAt AS updatedAt
            ORDER BY e.updatedAt DESC
            SKIP $skip LIMIT $limit
            """,
            new { limit, skip });

        await foreach (var record in entityResult)
        {
            snapshot.Nodes.Add(new MemoryGraphNode
            {
                Id = record["id"].As<string>(),
                Label = record["label"].As<string>(),
                Type = record["type"].As<string>(),
                Summary = record["summary"].As<string>(),
            });
        }

        if (snapshot.Nodes.Count == 0)
            return snapshot;

        var nodeIds = snapshot.Nodes.Select(n => n.Id).ToList();
        var relResult = await session.RunAsync(
            """
            MATCH (a:MemoryEntity)-[r:RELATES_TO]->(b:MemoryEntity)
            WHERE a.id IN $nodeIds AND b.id IN $nodeIds
            RETURN a.id AS source, b.id AS target,
                   r.relationship AS relationship, r.context AS context,
                   r.timestamp AS timestamp
            """,
            new { nodeIds });

        await foreach (var record in relResult)
        {
            snapshot.Links.Add(new MemoryGraphLink
            {
                Source = record["source"].As<string>(),
                Target = record["target"].As<string>(),
                Relationship = record["relationship"].As<string>(),
                Context = record["context"].As<string?>(),
                Timestamp = record["timestamp"].As<DateTimeOffset>().UtcDateTime,
            });
        }

        var countResult = await session.RunAsync(
            "MATCH (e:MemoryEntity) RETURN count(e) AS total");
        if (await countResult.FetchAsync())
            snapshot.TotalNodeCount = countResult.Current["total"].As<int>();

        return snapshot;
    }

    public async Task<List<string>> FindCandidateEntityIdsAsync(string candidateId, int limit = 20)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);

        var searchTerms = candidateId.Replace("_", " ").Trim();
        if (string.IsNullOrWhiteSpace(searchTerms))
            return [];

        try
        {
            return await FindCandidateEntityIdsWithFulltextAsync(session, searchTerms, limit);
        }
        catch (ClientException ex) when (IsMissingMemoryFulltextIndex(ex))
        {
            _logger.LogWarning(
                "Memory fulltext index is missing; falling back to scan-based candidate search.");
            return await FindCandidateEntityIdsWithFallbackAsync(session, searchTerms, limit);
        }
    }

    public async Task<MemoryEntity?> GetEntityAsync(string entityId)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var result = await session.RunAsync(
            """
            MATCH (e:MemoryEntity {id: $id})
            RETURN e.id AS id, e.label AS label, e.type AS type,
                   e.summary AS summary, e.source AS source,
                   e.createdAt AS createdAt, e.updatedAt AS updatedAt
            """,
            new { id = entityId });

        if (await result.FetchAsync())
        {
            var record = result.Current;
            return new MemoryEntity
            {
                Id = record["id"].As<string>(),
                Label = record["label"].As<string>(),
                Type = record["type"].As<string>(),
                Summary = record["summary"].As<string>(),
                Source = record["source"].As<string>(),
                CreatedAt = record["createdAt"].As<DateTimeOffset>().UtcDateTime,
                UpdatedAt = record["updatedAt"].As<DateTimeOffset>().UtcDateTime,
            };
        }

        return null;
    }

    private static string EscapeLuceneQuery(string query)
    {
        var escaped = new System.Text.StringBuilder(query.Length + 10);
        foreach (var c in query)
        {
            if ("+-&|!(){}[]^\"~*?:\\/".Contains(c))
                escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    internal static bool IsMissingMemoryFulltextIndex(ClientException ex) =>
        ex.Message.Contains("There is no such fulltext schema index: memory_entity_fulltext",
            StringComparison.OrdinalIgnoreCase);

    private static async Task<List<MemoryEntity>> TextSearchWithFulltextAsync(
        IAsyncSession session,
        string query,
        int limit)
    {
        var results = new List<MemoryEntity>();
        var escapedQuery = EscapeLuceneQuery(query);

        var result = await session.RunAsync(
            """
            CALL db.index.fulltext.queryNodes('memory_entity_fulltext', $query)
            YIELD node, score
            RETURN node.id AS id, node.label AS label, node.type AS type,
                   node.summary AS summary, node.source AS source,
                   node.createdAt AS createdAt, node.updatedAt AS updatedAt
            ORDER BY score DESC
            LIMIT $limit
            """,
            new { query = escapedQuery, limit });

        await foreach (var record in result)
        {
            results.Add(MapMemoryEntity(record));
        }

        return results;
    }

    private static async Task<List<MemoryEntity>> TextSearchWithFallbackAsync(
        IAsyncSession session,
        string query,
        int limit)
    {
        var normalizedQuery = query.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return [];

        var result = await session.RunAsync(
            """
            MATCH (e:MemoryEntity)
            WITH e,
                 CASE
                    WHEN toLower(coalesce(e.id, '')) = $query THEN 100
                    WHEN toLower(coalesce(e.label, '')) = $query THEN 90
                    WHEN toLower(coalesce(e.id, '')) CONTAINS $query THEN 80
                    WHEN toLower(coalesce(e.label, '')) CONTAINS $query THEN 70
                    WHEN toLower(coalesce(e.summary, '')) CONTAINS $query THEN 50
                    ELSE 0
                 END AS score
            WHERE score > 0
            RETURN e.id AS id, e.label AS label, e.type AS type,
                   e.summary AS summary, e.source AS source,
                   e.createdAt AS createdAt, e.updatedAt AS updatedAt
            ORDER BY score DESC, e.updatedAt DESC
            LIMIT $limit
            """,
            new { query = normalizedQuery, limit });

        var results = new List<MemoryEntity>();
        await foreach (var record in result)
        {
            results.Add(MapMemoryEntity(record));
        }

        return results;
    }

    private static async Task<List<string>> FindCandidateEntityIdsWithFulltextAsync(
        IAsyncSession session,
        string searchTerms,
        int limit)
    {
        var escapedTerms = EscapeLuceneQuery(searchTerms);

        var result = await session.RunAsync(
            """
            CALL db.index.fulltext.queryNodes('memory_entity_fulltext', $query)
            YIELD node, score
            RETURN node.id AS id
            LIMIT $limit
            """,
            new { query = escapedTerms, limit });

        var ids = new List<string>();
        await foreach (var record in result)
        {
            ids.Add(record["id"].As<string>());
        }

        return ids;
    }

    private static async Task<List<string>> FindCandidateEntityIdsWithFallbackAsync(
        IAsyncSession session,
        string searchTerms,
        int limit)
    {
        var normalizedQuery = searchTerms.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return [];

        var result = await session.RunAsync(
            """
            MATCH (e:MemoryEntity)
            WITH e,
                 CASE
                    WHEN toLower(coalesce(e.id, '')) = $query THEN 100
                    WHEN toLower(coalesce(e.label, '')) = $query THEN 90
                    WHEN toLower(coalesce(e.id, '')) CONTAINS $query THEN 80
                    WHEN toLower(coalesce(e.label, '')) CONTAINS $query THEN 70
                    WHEN toLower(coalesce(e.summary, '')) CONTAINS $query THEN 50
                    ELSE 0
                 END AS score
            WHERE score > 0
            RETURN e.id AS id
            ORDER BY score DESC, e.updatedAt DESC
            LIMIT $limit
            """,
            new { query = normalizedQuery, limit });

        var ids = new List<string>();
        await foreach (var record in result)
        {
            ids.Add(record["id"].As<string>());
        }

        return ids;
    }

    private static MemoryEntity MapMemoryEntity(IRecord record)
    {
        return new MemoryEntity
        {
            Id = record["id"].As<string>(),
            Label = record["label"].As<string>(),
            Type = record["type"].As<string>(),
            Summary = record["summary"].As<string>(),
            Source = record["source"].As<string>(),
            CreatedAt = record["createdAt"].As<DateTimeOffset>().UtcDateTime,
            UpdatedAt = record["updatedAt"].As<DateTimeOffset>().UtcDateTime,
        };
    }
}
