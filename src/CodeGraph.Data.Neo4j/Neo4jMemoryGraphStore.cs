using System.Text;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using CodeGraph.Models.Memory;

namespace CodeGraph.Data.Neo4j;

public partial class Neo4jMemoryGraphStore : IMemoryGraphStore
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
                    e.externalId = item.externalId,
                    e.canonicalName = item.canonicalName,
                    e.aliases = item.aliases,
                    e.summary = item.summary,
                    e.source = item.source,
                    e.embedding = item.embedding,
                    e.createdAt = datetime(item.createdAt),
                    e.updatedAt = datetime(item.updatedAt)
                ON MATCH SET
                    e.label = coalesce(item.label, e.label),
                    e.type = coalesce(item.type, e.type),
                    e.externalId = coalesce(item.externalId, e.externalId),
                    e.canonicalName = coalesce(item.canonicalName, e.canonicalName),
                    e.aliases = CASE
                        WHEN item.aliases IS NULL OR size(item.aliases) = 0 THEN e.aliases
                        ELSE item.aliases
                    END,
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
                        externalId = e.ExternalId,
                        canonicalName = e.CanonicalName,
                        aliases = e.Aliases,
                        summary = e.Summary,
                        source = e.Source,
                        embedding = e.Embedding,
                        createdAt = e.CreatedAt.ToString("O"),
                        updatedAt = e.UpdatedAt.ToString("O"),
                    }).ToList(),
                });
        });
    }

    public async Task UpsertClaimsBatchAsync(IReadOnlyList<MemoryClaim> claims)
    {
        if (claims.Count == 0) return;

        await using var session = _sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                UNWIND $batch AS item
                MERGE (c:MemoryClaim {id: item.id})
                ON CREATE SET
                    c.claimKey = item.claimKey,
                    c.factGroupKey = item.factGroupKey,
                    c.predicate = item.predicate,
                    c.valueText = item.valueText,
                    c.valueJson = item.valueJson,
                    c.normalizedText = item.normalizedText,
                    c.status = item.status,
                    c.confidence = item.confidence,
                    c.effectiveAt = CASE WHEN item.effectiveAt IS NULL THEN null ELSE datetime(item.effectiveAt) END,
                    c.recordedAt = datetime(item.recordedAt),
                    c.supersedesClaimId = item.supersedesClaimId,
                    c.source = item.source,
                    c.embedding = item.embedding
                ON MATCH SET
                    c.claimKey = item.claimKey,
                    c.factGroupKey = item.factGroupKey,
                    c.predicate = item.predicate,
                    c.valueText = item.valueText,
                    c.valueJson = item.valueJson,
                    c.normalizedText = item.normalizedText,
                    c.status = item.status,
                    c.confidence = item.confidence,
                    c.effectiveAt = CASE WHEN item.effectiveAt IS NULL THEN c.effectiveAt ELSE datetime(item.effectiveAt) END,
                    c.recordedAt = datetime(item.recordedAt),
                    c.supersedesClaimId = item.supersedesClaimId,
                    c.source = item.source,
                    c.embedding = coalesce(item.embedding, c.embedding)
                WITH c, item
                MATCH (subject:MemoryEntity {id: item.subjectEntityId})
                MERGE (c)-[:SUBJECT]->(subject)
                WITH c, item
                OPTIONAL MATCH (object:MemoryEntity {id: item.objectEntityId})
                FOREACH (_ IN CASE WHEN item.objectEntityId IS NOT NULL AND item.objectEntityId <> '' AND object IS NOT NULL THEN [1] ELSE [] END |
                    MERGE (c)-[:OBJECT]->(object))
                """,
                new
                {
                    batch = claims.Select(c => new
                    {
                        id = c.Id,
                        claimKey = c.ClaimKey,
                        factGroupKey = c.FactGroupKey,
                        subjectEntityId = c.SubjectEntityId,
                        predicate = c.Predicate,
                        objectEntityId = c.ObjectEntityId,
                        valueText = c.ValueText,
                        valueJson = c.ValueJson,
                        normalizedText = c.NormalizedText,
                        status = ToClaimStatusString(c.Status),
                        confidence = c.Confidence,
                        effectiveAt = c.EffectiveAt?.ToString("O"),
                        recordedAt = c.RecordedAt.ToString("O"),
                        supersedesClaimId = c.SupersedesClaimId,
                        source = c.Source,
                        embedding = c.Embedding,
                    }).ToList(),
                });
        });
    }

    public async Task AddClaimEdgesBatchAsync(IReadOnlyList<MemoryClaimEdge> edges)
    {
        if (edges.Count == 0) return;

        var supportedEdgeTypes = edges
            .Select(edge => edge.EdgeType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var session = _sessionFactory.GetSession();
        foreach (var edgeType in supportedEdgeTypes)
        {
            var relationshipType = MapClaimRelationshipType(edgeType);
            var batch = edges
                .Where(edge => edge.EdgeType.Equals(edgeType, StringComparison.OrdinalIgnoreCase))
                .Select(edge => new
                {
                    fromClaimId = edge.FromClaimId,
                    toClaimId = edge.ToClaimId,
                    weight = edge.Weight,
                    source = edge.Source,
                    createdAt = edge.CreatedAt.ToString("O"),
                })
                .ToList();

            if (batch.Count == 0)
                continue;

            var cypher =
                $$"""
                UNWIND $batch AS item
                MATCH (a:MemoryClaim {id: item.fromClaimId})
                MATCH (b:MemoryClaim {id: item.toClaimId})
                MERGE (a)-[r:{{relationshipType}}]->(b)
                SET r.weight = item.weight,
                    r.source = item.source,
                    r.createdAt = datetime(item.createdAt)
                """;

            await session.ExecuteWriteAsync(async tx => { await tx.RunAsync(cypher, new { batch }); });
        }
    }

    public async Task AddEvidenceBatchAsync(IReadOnlyList<MemoryEvidence> evidence)
    {
        if (evidence.Count == 0) return;

        await using var session = _sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                UNWIND $batch AS item
                MERGE (ev:MemoryEvidence {id: item.id})
                ON CREATE SET
                    ev.claimId = item.claimId,
                    ev.observationId = item.observationId,
                    ev.evidenceType = item.evidenceType,
                    ev.sourceRef = item.sourceRef,
                    ev.snippet = item.snippet,
                    ev.metadataJson = item.metadataJson,
                    ev.createdAt = datetime(item.createdAt)
                ON MATCH SET
                    ev.claimId = item.claimId,
                    ev.observationId = item.observationId,
                    ev.evidenceType = item.evidenceType,
                    ev.sourceRef = item.sourceRef,
                    ev.snippet = item.snippet,
                    ev.metadataJson = item.metadataJson
                WITH ev, item
                OPTIONAL MATCH (c:MemoryClaim {id: item.claimId})
                FOREACH (_ IN CASE WHEN c IS NOT NULL THEN [1] ELSE [] END |
                    MERGE (ev)-[:EVIDENCE_FOR]->(c))
                WITH ev, item
                OPTIONAL MATCH (o:MemoryObservation {id: item.observationId})
                FOREACH (_ IN CASE WHEN o IS NOT NULL THEN [1] ELSE [] END |
                    MERGE (ev)-[:EVIDENCE_FOR]->(o))
                """,
                new
                {
                    batch = evidence.Select(ev => new
                    {
                        id = ev.Id,
                        claimId = ev.ClaimId,
                        observationId = ev.ObservationId,
                        evidenceType = ev.EvidenceType,
                        sourceRef = ev.SourceRef,
                        snippet = ev.Snippet,
                        metadataJson = ev.MetadataJson,
                        createdAt = ev.CreatedAt.ToString("O"),
                    }).ToList(),
                });
        });
    }

    public async Task UpsertEntityEdgesBatchAsync(IReadOnlyList<MemoryEntityEdge> edges)
    {
        if (edges.Count == 0) return;

        await using var session = _sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                UNWIND $batch AS item
                MATCH (a:MemoryEntity {id: item.fromEntityId})
                MATCH (b:MemoryEntity {id: item.toEntityId})
                MERGE (a)-[r:ACTIVE_RELATES_TO {edgeType: item.edgeType}]->(b)
                SET r.bestActiveClaimId = item.bestActiveClaimId,
                    r.weight = item.weight,
                    r.createdAt = CASE WHEN r.createdAt IS NULL THEN datetime(item.createdAt) ELSE r.createdAt END,
                    r.updatedAt = datetime(item.updatedAt)
                """,
                new
                {
                    batch = edges.Select(edge => new
                    {
                        fromEntityId = edge.FromEntityId,
                        toEntityId = edge.ToEntityId,
                        edgeType = edge.EdgeType,
                        bestActiveClaimId = edge.BestActiveClaimId,
                        weight = edge.Weight,
                        createdAt = edge.CreatedAt.ToString("O"),
                        updatedAt = edge.UpdatedAt.ToString("O"),
                    }).ToList(),
                });
        });
    }

    public async Task CreateObservationAsync(MemoryObservation obs)
    {
        var aboutEntityIds = obs.AboutEntityIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var aboutClaimIds = obs.AboutClaimIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var session = _sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                MERGE (o:MemoryObservation {id: $id})
                SET o.claim = $claim,
                    o.conflictsWith = $conflictsWith,
                    o.source = $source,
                    o.timestamp = datetime($timestamp),
                    o.resolved = $resolved,
                    o.resolution = $resolution,
                    o.resolvedByMemoryId = $resolvedByMemoryId
                WITH o
                OPTIONAL MATCH (o)-[existing:ABOUT]->()
                DELETE existing
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
                });

            if (aboutEntityIds.Count > 0)
            {
                await tx.RunAsync(
                    """
                    MATCH (o:MemoryObservation {id: $id})
                    UNWIND $aboutEntityIds AS aboutEntityId
                    MATCH (e:MemoryEntity {id: aboutEntityId})
                    MERGE (o)-[:ABOUT]->(e)
                    """,
                    new { id = obs.Id, aboutEntityIds });
            }

            if (aboutClaimIds.Count > 0)
            {
                await tx.RunAsync(
                    """
                    MATCH (o:MemoryObservation {id: $id})
                    UNWIND $aboutClaimIds AS aboutClaimId
                    MATCH (c:MemoryClaim {id: aboutClaimId})
                    MERGE (o)-[:ABOUT]->(c)
                    """,
                    new { id = obs.Id, aboutClaimIds });
            }
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
            "MATCH (e:MemoryEntity {id: $entityId})-[rel:ACTIVE_RELATES_TO]-(other:MemoryEntity) " +
            "WITH e, rel, other, startNode(rel) AS from, endNode(rel) AS to " +
            "RETURN DISTINCT " +
            "CASE WHEN from.id = $entityId THEN 'outgoing' ELSE 'incoming' END AS direction, " +
            "rel.edgeType AS relationship, " +
            "CASE WHEN from.id = $entityId THEN to.label ELSE from.label END AS targetLabel, " +
            "CASE WHEN from.id = $entityId THEN to.id ELSE from.id END AS targetId, " +
            "rel.bestActiveClaimId AS context, " +
            "rel.updatedAt AS timestamp " +
            "ORDER BY rel.updatedAt DESC";

        var result = await session.RunAsync(cypher, new { entityId });

        await foreach (var record in result)
        {
            results.Add(new MemoryRelationshipDetail
            {
                Direction = record["direction"].As<string>(),
                RelationshipType = record["relationship"].As<string>(),
                TargetLabel = record["targetLabel"].As<string>(),
                TargetId = record["targetId"].As<string>(),
                Context = record["context"].As<string?>(),
                Timestamp = record["timestamp"].As<DateTimeOffset>().UtcDateTime,
            });
        }

        return results;
    }

    public async Task<List<MemoryObservation>> GetUnresolvedObservationsAsync(IEnumerable<string> entityIds, IEnumerable<string> claimIds)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var results = new List<MemoryObservation>();
        var entityIdList = entityIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var claimIdList = claimIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = await session.RunAsync(
            """
            CALL {
                MATCH (o:MemoryObservation {resolved: false})-[:ABOUT]->(e:MemoryEntity)
                WHERE e.id IN $entityIds
                RETURN DISTINCT o
            UNION
                MATCH (o:MemoryObservation {resolved: false})-[:ABOUT]->(c:MemoryClaim)
                WHERE c.id IN $claimIds
                RETURN DISTINCT o
            UNION
                MATCH (o:MemoryObservation {resolved: false})
                WHERE NOT EXISTS { (o)-[:ABOUT]->() }
                  AND (
                      ANY(id IN $entityIds WHERE o.claim CONTAINS id OR o.conflictsWith CONTAINS id)
                      OR ANY(id IN $claimIds WHERE o.claim CONTAINS id OR o.conflictsWith CONTAINS id)
                  )
                RETURN DISTINCT o
            }
            OPTIONAL MATCH (o)-[:ABOUT]->(aboutEntity:MemoryEntity)
            OPTIONAL MATCH (o)-[:ABOUT]->(aboutClaim:MemoryClaim)
            RETURN o.id AS id, o.claim AS claim, o.conflictsWith AS conflictsWith,
                   o.source AS source, o.timestamp AS timestamp,
                   o.resolved AS resolved, o.resolution AS resolution,
                   o.resolvedByMemoryId AS resolvedByMemoryId,
                   collect(DISTINCT aboutEntity.id) AS aboutEntityIds,
                   collect(DISTINCT aboutClaim.id) AS aboutClaimIds
            ORDER BY o.timestamp DESC
            """,
            new { entityIds = entityIdList, claimIds = claimIdList });

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
                AboutEntityIds = TryReadStringList(record, "aboutEntityIds"),
                AboutClaimIds = TryReadStringList(record, "aboutClaimIds"),
            });
        }

        return results;
    }

    public async Task<List<MemoryObservation>> GetUnresolvedObservationsForEntitiesAsync(IEnumerable<string> entityIds)
    {
        return await GetUnresolvedObservationsAsync(entityIds, []);
    }

    public async Task<List<MemoryObservation>> GetAllObservationsAsync()
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var results = new List<MemoryObservation>();

        var result = await session.RunAsync(
            """
            MATCH (o:MemoryObservation)
            OPTIONAL MATCH (o)-[:ABOUT]->(aboutEntity:MemoryEntity)
            OPTIONAL MATCH (o)-[:ABOUT]->(aboutClaim:MemoryClaim)
            OPTIONAL MATCH (o)-[:OBSERVES]->(legacyEntity:MemoryEntity)
            RETURN o.id AS id, o.claim AS claim, o.conflictsWith AS conflictsWith,
                   o.source AS source, o.timestamp AS timestamp,
                   o.resolved AS resolved, o.resolution AS resolution,
                   o.resolvedByMemoryId AS resolvedByMemoryId,
                   collect(DISTINCT aboutEntity.id) + collect(DISTINCT legacyEntity.id) AS aboutEntityIds,
                   collect(DISTINCT aboutClaim.id) AS aboutClaimIds
            ORDER BY o.timestamp ASC, o.id ASC
            """);

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
                AboutEntityIds = TryReadStringList(record, "aboutEntityIds"),
                AboutClaimIds = TryReadStringList(record, "aboutClaimIds"),
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
            MATCH (a:MemoryEntity)-[r:ACTIVE_RELATES_TO]->(b:MemoryEntity)
            WHERE a.id IN $nodeIds AND b.id IN $nodeIds
            RETURN a.id AS source, b.id AS target,
                   r.edgeType AS relationship, r.bestActiveClaimId AS context,
                   r.updatedAt AS timestamp
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
                   e.externalId AS externalId, e.canonicalName AS canonicalName, e.aliases AS aliases,
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

    public async Task<MemoryEntity?> GetEntityByExternalIdAsync(string externalId)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var result = await session.RunAsync(
            """
            MATCH (e:MemoryEntity)
            WHERE e.externalId = $externalId
            RETURN e.id AS id, e.label AS label, e.type AS type,
                   e.externalId AS externalId, e.canonicalName AS canonicalName, e.aliases AS aliases,
                   e.summary AS summary, e.source AS source,
                   e.createdAt AS createdAt, e.updatedAt AS updatedAt
            LIMIT 1
            """,
            new { externalId });

        if (await result.FetchAsync())
            return MapMemoryEntity(result.Current);

        return null;
    }

    public async Task<List<MemoryLegacyRelationship>> GetLegacyRelationshipsAsync()
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var result = await session.RunAsync(
            """
            MATCH (a:MemoryEntity)-[r:RELATES_TO]->(b:MemoryEntity)
            RETURN a.id AS fromEntityId,
                   b.id AS toEntityId,
                   r.relationship AS relationshipType,
                   r.context AS context,
                   r.source AS source,
                   r.timestamp AS timestamp,
                   r.supersedes AS supersedes
            ORDER BY r.timestamp ASC, a.id ASC, r.relationship ASC, b.id ASC
            """);

        var relationships = new List<MemoryLegacyRelationship>();
        await foreach (var record in result)
        {
            relationships.Add(new MemoryLegacyRelationship
            {
                FromEntityId = record["fromEntityId"].As<string>(),
                ToEntityId = record["toEntityId"].As<string>(),
                RelationshipType = record["relationshipType"].As<string>(),
                Context = record["context"].As<string?>(),
                Source = record["source"].As<string?>(),
                Timestamp = TryReadDateTime(record, "timestamp") ?? DateTime.UtcNow,
                Supersedes = record["supersedes"].As<string?>(),
            });
        }

        return relationships;
    }

    public async Task<MemoryClaim?> GetClaimAsync(string claimId)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var result = await session.RunAsync(
            """
            MATCH (c:MemoryClaim {id: $claimId})-[:SUBJECT]->(subject:MemoryEntity)
            OPTIONAL MATCH (c)-[:OBJECT]->(object:MemoryEntity)
            RETURN c.id AS id, c.claimKey AS claimKey, c.factGroupKey AS factGroupKey,
                   subject.id AS subjectEntityId, c.predicate AS predicate, object.id AS objectEntityId,
                   c.valueText AS valueText, c.valueJson AS valueJson, c.normalizedText AS normalizedText,
                   c.status AS status, c.confidence AS confidence, c.effectiveAt AS effectiveAt,
                   c.recordedAt AS recordedAt, c.supersedesClaimId AS supersedesClaimId,
                   c.source AS source, c.embedding AS embedding
            LIMIT 1
            """,
            new { claimId });

        var claims = await ReadClaimsAsync(result);
        return claims.FirstOrDefault();
    }

    public async Task<List<MemoryClaim>> GetClaimsByFactGroupAsync(string factGroupKey)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var result = await session.RunAsync(
            """
            MATCH (c:MemoryClaim {factGroupKey: $factGroupKey})
            OPTIONAL MATCH (c)-[:SUBJECT]->(subject:MemoryEntity)
            OPTIONAL MATCH (c)-[:OBJECT]->(object:MemoryEntity)
            RETURN c.id AS id, c.claimKey AS claimKey, c.factGroupKey AS factGroupKey,
                   subject.id AS subjectEntityId, c.predicate AS predicate, object.id AS objectEntityId,
                   c.valueText AS valueText, c.valueJson AS valueJson, c.normalizedText AS normalizedText,
                   c.status AS status, c.confidence AS confidence, c.effectiveAt AS effectiveAt,
                   c.recordedAt AS recordedAt, c.supersedesClaimId AS supersedesClaimId,
                   c.source AS source, c.embedding AS embedding
            ORDER BY c.recordedAt DESC
            """,
            new { factGroupKey });

        return await ReadClaimsAsync(result);
    }

    public async Task<List<MemoryClaim>> GetClaimsBySubjectPredicateAsync(string subjectEntityId, string predicate)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var result = await session.RunAsync(
            """
            MATCH (c:MemoryClaim)-[:SUBJECT]->(subject:MemoryEntity {id: $subjectEntityId})
            OPTIONAL MATCH (c)-[:OBJECT]->(object:MemoryEntity)
            WHERE c.predicate = $predicate
            RETURN c.id AS id, c.claimKey AS claimKey, c.factGroupKey AS factGroupKey,
                   subject.id AS subjectEntityId, c.predicate AS predicate, object.id AS objectEntityId,
                   c.valueText AS valueText, c.valueJson AS valueJson, c.normalizedText AS normalizedText,
                   c.status AS status, c.confidence AS confidence, c.effectiveAt AS effectiveAt,
                   c.recordedAt AS recordedAt, c.supersedesClaimId AS supersedesClaimId,
                   c.source AS source, c.embedding AS embedding
            ORDER BY c.recordedAt DESC
            """,
            new { subjectEntityId, predicate });

        return await ReadClaimsAsync(result);
    }

    public async Task<MemoryEntityBundle?> GetEntityBundleAsync(
        string entityId,
        bool includeSuperseded = false,
        bool includeConflicts = true,
        int neighborLimit = 20)
    {
        var entity = await GetEntityAsync(entityId);
        if (entity == null)
            return null;

        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var claimsResult = await session.RunAsync(
            """
            MATCH (c:MemoryClaim)-[:SUBJECT]->(subject:MemoryEntity)
            OPTIONAL MATCH (c)-[:OBJECT]->(object:MemoryEntity)
            WITH c, subject, object
            WHERE subject.id = $entityId OR object.id = $entityId
            RETURN c.id AS id, c.claimKey AS claimKey, c.factGroupKey AS factGroupKey,
                   subject.id AS subjectEntityId, c.predicate AS predicate, object.id AS objectEntityId,
                   c.valueText AS valueText, c.valueJson AS valueJson, c.normalizedText AS normalizedText,
                   c.status AS status, c.confidence AS confidence, c.effectiveAt AS effectiveAt,
                   c.recordedAt AS recordedAt, c.supersedesClaimId AS supersedesClaimId,
                   c.source AS source, c.embedding AS embedding
            ORDER BY c.recordedAt DESC
            """,
            new { entityId });

        var claims = (await ReadClaimsAsync(claimsResult))
            .Where(claim => IsClaimInEntityBundle(claim, entityId))
            .ToList();
        var bundle = new MemoryEntityBundle
        {
            Entity = entity,
            ActiveClaims = claims.Where(claim => claim.Status == MemoryClaimStatus.Active).ToList(),
            ConflictingClaims = includeConflicts
                ? claims.Where(claim => claim.Status == MemoryClaimStatus.Conflicted).ToList()
                : [],
            SupersededClaims = includeSuperseded
                ? claims.Where(claim => claim.Status == MemoryClaimStatus.Superseded).ToList()
                : [],
            Observations = await GetUnresolvedObservationsForEntitiesAsync([entityId]),
        };

        var neighborResult = await session.RunAsync(
            """
            MATCH (a:MemoryEntity {id: $entityId})-[r:ACTIVE_RELATES_TO]-(b:MemoryEntity)
            RETURN a.id AS fromEntityId, b.id AS toEntityId, r.edgeType AS edgeType,
                   r.bestActiveClaimId AS bestActiveClaimId, r.weight AS weight,
                   r.createdAt AS createdAt, r.updatedAt AS updatedAt
            ORDER BY r.updatedAt DESC
            LIMIT $limit
            """,
            new { entityId, limit = Math.Clamp(neighborLimit, 1, 100) });

        await foreach (var record in neighborResult)
        {
            bundle.NeighborEdges.Add(new MemoryEntityEdge
            {
                FromEntityId = record["fromEntityId"].As<string>(),
                ToEntityId = record["toEntityId"].As<string>(),
                EdgeType = record["edgeType"].As<string>(),
                BestActiveClaimId = record["bestActiveClaimId"].As<string?>(),
                Weight = TryReadDecimal(record, "weight"),
                CreatedAt = TryReadDateTime(record, "createdAt") ?? DateTime.UtcNow,
                UpdatedAt = TryReadDateTime(record, "updatedAt") ?? DateTime.UtcNow,
            });
        }

        return bundle;
    }

    public async Task<MemoryClaimBundle?> GetClaimBundleAsync(
        string claimId,
        bool includeSupersessionChain = true,
        bool includeConflicts = true,
        bool includeEvidence = true)
    {
        var claim = await GetClaimAsync(claimId);
        if (claim == null)
            return null;

        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var bundle = new MemoryClaimBundle
        {
            Claim = claim,
            FactGroupPeers = (await GetClaimsByFactGroupAsync(claim.FactGroupKey))
                .Where(peer => !peer.Id.Equals(claim.Id, StringComparison.OrdinalIgnoreCase))
                .ToList(),
        };

        if (includeSupersessionChain)
        {
            var supersessionResult = await session.RunAsync(
                """
                MATCH (root:MemoryClaim {id: $claimId})
                OPTIONAL MATCH p=(root)-[:SUPERSEDES*1..5]-(related:MemoryClaim)
                WITH collect(DISTINCT related) AS relatedClaims
                UNWIND relatedClaims AS c
                WITH c WHERE c IS NOT NULL
                MATCH (c)-[:SUBJECT]->(subject:MemoryEntity)
                OPTIONAL MATCH (c)-[:OBJECT]->(object:MemoryEntity)
                RETURN c.id AS id, c.claimKey AS claimKey, c.factGroupKey AS factGroupKey,
                       subject.id AS subjectEntityId, c.predicate AS predicate, object.id AS objectEntityId,
                       c.valueText AS valueText, c.valueJson AS valueJson, c.normalizedText AS normalizedText,
                       c.status AS status, c.confidence AS confidence, c.effectiveAt AS effectiveAt,
                       c.recordedAt AS recordedAt, c.supersedesClaimId AS supersedesClaimId,
                       c.source AS source, c.embedding AS embedding
                ORDER BY c.recordedAt DESC
                """,
                new { claimId });

            bundle.SupersessionChain = await ReadClaimsAsync(supersessionResult);
        }

        if (includeConflicts)
        {
            var conflictsResult = await session.RunAsync(
                """
                MATCH (root:MemoryClaim {id: $claimId})-[:CONFLICTS_WITH]-(related:MemoryClaim)
                MATCH (related)-[:SUBJECT]->(subject:MemoryEntity)
                OPTIONAL MATCH (related)-[:OBJECT]->(object:MemoryEntity)
                RETURN related.id AS id, related.claimKey AS claimKey, related.factGroupKey AS factGroupKey,
                       subject.id AS subjectEntityId, related.predicate AS predicate, object.id AS objectEntityId,
                       related.valueText AS valueText, related.valueJson AS valueJson, related.normalizedText AS normalizedText,
                       related.status AS status, related.confidence AS confidence, related.effectiveAt AS effectiveAt,
                       related.recordedAt AS recordedAt, related.supersedesClaimId AS supersedesClaimId,
                       related.source AS source, related.embedding AS embedding
                ORDER BY related.recordedAt DESC
                """,
                new { claimId });

            bundle.Conflicts = await ReadClaimsAsync(conflictsResult);
        }

        if (includeEvidence)
        {
            var evidenceResult = await session.RunAsync(
                """
                MATCH (ev:MemoryEvidence)-[:EVIDENCE_FOR]->(c:MemoryClaim {id: $claimId})
                RETURN ev.id AS id, ev.claimId AS claimId, ev.observationId AS observationId,
                       ev.evidenceType AS evidenceType, ev.sourceRef AS sourceRef,
                       ev.snippet AS snippet, ev.metadataJson AS metadataJson, ev.createdAt AS createdAt
                ORDER BY ev.createdAt DESC
                """,
                new { claimId });

            await foreach (var record in evidenceResult)
            {
                bundle.Evidence.Add(new MemoryEvidence
                {
                    Id = record["id"].As<string>(),
                    ClaimId = record["claimId"].As<string?>(),
                    ObservationId = record["observationId"].As<string?>(),
                    EvidenceType = record["evidenceType"].As<string>(),
                    SourceRef = record["sourceRef"].As<string>(),
                    Snippet = record["snippet"].As<string?>(),
                    MetadataJson = record["metadataJson"].As<string?>(),
                    CreatedAt = TryReadDateTime(record, "createdAt") ?? DateTime.UtcNow,
                });
            }
        }

        var observationEntityIds = new List<string> { claim.SubjectEntityId };
        if (!string.IsNullOrWhiteSpace(claim.ObjectEntityId))
            observationEntityIds.Add(claim.ObjectEntityId);
        bundle.Observations = await GetUnresolvedObservationsAsync(observationEntityIds, [claim.Id]);

        return bundle;
    }

    public async Task<List<(MemoryClaim Claim, double Score, string MatchKind)>> SearchClaimsAsync(
        string query,
        float[]? queryEmbedding,
        int limit = 5,
        bool includeSuperseded = false)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);

        var results = new Dictionary<string, (MemoryClaim Claim, double Score, string MatchKind)>(StringComparer.OrdinalIgnoreCase);
        var normalizedQuery = query.Trim();
        var normalizedExactQuery = NormalizeMemorySearchText(normalizedQuery);
        var exactClaim = await GetClaimAsync(NormalizeLookupKey(normalizedQuery));
        if (exactClaim != null && (includeSuperseded || exactClaim.Status != MemoryClaimStatus.Superseded))
            results[exactClaim.Id] = (exactClaim, 100, "exact");

        var exactTextMatches = await SearchClaimsByExactTextAsync(session, normalizedExactQuery, limit, includeSuperseded);
        foreach (var (claim, score) in exactTextMatches)
            MergeClaimSearchResult(results, claim, score, "exact");

        try
        {
            var lexicalMatches = await TextSearchClaimsWithFulltextAsync(session, normalizedQuery, limit, includeSuperseded);
            foreach (var (claim, score) in lexicalMatches)
                MergeClaimSearchResult(results, claim, 60 + score, "lexical");
        }
        catch (ClientException ex) when (IsMissingMemoryClaimFulltextIndex(ex))
        {
            var fallbackMatches = await TextSearchClaimsWithFallbackAsync(session, normalizedQuery, limit, includeSuperseded);
            foreach (var (claim, score) in fallbackMatches)
                MergeClaimSearchResult(results, claim, 60 + score, "lexical");
        }

        if (queryEmbedding != null)
        {
            try
            {
                var vectorMatches = await VectorSearchClaimsAsync(session, queryEmbedding, limit, includeSuperseded);
                foreach (var (claim, score) in vectorMatches)
                    MergeClaimSearchResult(results, claim, 40 + score, "vector");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Memory claim vector search failed");
            }
        }

        return results.Values
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Claim.RecordedAt)
            .Take(limit)
            .ToList();
    }

    public async Task<MemorySubgraphResult> GetMemorySubgraphAsync(
        MemorySubgraphQuery query,
        int maxHops = 2,
        int maxReturnedEntities = 20,
        int maxReturnedClaims = 40,
        bool includeSuperseded = false,
        bool includeConflicts = true)
    {
        await using var session = _sessionFactory.GetSession(AccessMode.Read);
        var result = new MemorySubgraphResult
        {
            Query = query,
            Meta = new MemorySubgraphMeta
            {
                MaxHopsUsed = Math.Clamp(maxHops, 1, 5),
            },
        };

        var seedClaimIds = query.SeedClaimIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var directSeedEntityIds = query.SeedEntityIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claimPromotedEntityIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (seedClaimIds.Count > 0)
        {
            var seedClaimEntities = await session.RunAsync(
                """
                UNWIND $seedClaimIds AS claimId
                MATCH (c:MemoryClaim {id: claimId})-[:SUBJECT]->(subject:MemoryEntity)
                OPTIONAL MATCH (c)-[:OBJECT]->(object:MemoryEntity)
                RETURN DISTINCT subject.id AS subjectId, object.id AS objectId
                """,
                new { seedClaimIds });

            await foreach (var record in seedClaimEntities)
            {
                var subjectId = record["subjectId"].As<string>();
                if (!string.IsNullOrWhiteSpace(subjectId) && !directSeedEntityIds.Contains(subjectId))
                    claimPromotedEntityIds.Add(subjectId);

                var objectId = record["objectId"].As<string?>();
                if (!string.IsNullOrWhiteSpace(objectId) && !directSeedEntityIds.Contains(objectId!))
                    claimPromotedEntityIds.Add(objectId!);
            }
        }

        if (directSeedEntityIds.Count == 0 && claimPromotedEntityIds.Count == 0 && seedClaimIds.Count == 0)
            return result;

        var entityDistances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (directSeedEntityIds.Count > 0)
        {
            var neighborhoodResult = await session.RunAsync(
                $$"""
                UNWIND $seedIds AS seedId
                MATCH (seed:MemoryEntity {id: seedId})
                OPTIONAL MATCH p=(seed)-[:ACTIVE_RELATES_TO*1..{{Math.Clamp(maxHops, 1, 5)}}]-(other:MemoryEntity)
                WITH collect({id: seed.id, hopDistance: 0}) + collect(CASE WHEN other IS NULL THEN null ELSE {id: other.id, hopDistance: length(p)} END) AS rows
                UNWIND rows AS row
                WITH row WHERE row IS NOT NULL
                RETURN row.id AS id, min(row.hopDistance) AS hopDistance
                ORDER BY hopDistance ASC, id ASC
                LIMIT $limit
                """,
                new { seedIds = directSeedEntityIds.ToList(), limit = maxReturnedEntities });

            await foreach (var record in neighborhoodResult)
                entityDistances[record["id"].As<string>()] = record["hopDistance"].As<int>();
        }

        foreach (var seedEntityId in directSeedEntityIds)
        {
            if (!entityDistances.ContainsKey(seedEntityId))
                entityDistances[seedEntityId] = 0;
        }

        MergeClaimPromotedEntityDistances(entityDistances, claimPromotedEntityIds);

        var entityIds = RankMemorySubgraphEntityIds(
            entityDistances,
            directSeedEntityIds,
            claimPromotedEntityIds,
            maxReturnedEntities);
        if (entityIds.Count == 0 && seedClaimIds.Count == 0)
            return result;

        var entitiesResult = await session.RunAsync(
            """
            MATCH (e:MemoryEntity)
            WHERE e.id IN $entityIds
            RETURN e.id AS id, e.label AS label, e.type AS type,
                   e.externalId AS externalId, e.canonicalName AS canonicalName, e.aliases AS aliases,
                   e.summary AS summary, e.source AS source,
                   e.createdAt AS createdAt, e.updatedAt AS updatedAt
            """,
            new { entityIds });

        var entityMap = new Dictionary<string, MemoryEntity>(StringComparer.OrdinalIgnoreCase);
        await foreach (var record in entitiesResult)
        {
            var entity = MapMemoryEntity(record);
            entityMap[entity.Id] = entity;
        }

        result.Entities = entityIds
            .Where(entityMap.ContainsKey)
            .Select(id => new MemorySubgraphEntity
            {
                Entity = entityMap[id],
                Score = directSeedEntityIds.Contains(id) ? 100 : Math.Max(1, 50 - (entityDistances[id] * 10)),
                HopDistance = entityDistances[id],
                IsDirectSeed = directSeedEntityIds.Contains(id),
            })
            .OrderBy(entity => entity.HopDistance)
            .ThenByDescending(entity => entity.Score)
            .ToList();

        var claimsResult = await session.RunAsync(
            """
            MATCH (c:MemoryClaim)-[:SUBJECT]->(subject:MemoryEntity)
            OPTIONAL MATCH (c)-[:OBJECT]->(object:MemoryEntity)
            WHERE ($includeSuperseded OR c.status <> 'superseded')
              AND ($includeConflicts OR c.status <> 'conflicted')
              AND (
                   c.id IN $seedClaimIds
                   OR (
                        subject.id IN $entityIds
                        AND (object IS NULL OR object.id IN $entityIds)
                   )
              )
            RETURN c.id AS id, c.claimKey AS claimKey, c.factGroupKey AS factGroupKey,
                   subject.id AS subjectEntityId, c.predicate AS predicate, object.id AS objectEntityId,
                   c.valueText AS valueText, c.valueJson AS valueJson, c.normalizedText AS normalizedText,
                   c.status AS status, c.confidence AS confidence, c.effectiveAt AS effectiveAt,
                   c.recordedAt AS recordedAt, c.supersedesClaimId AS supersedesClaimId,
                   c.source AS source, c.embedding AS embedding
            ORDER BY CASE WHEN c.id IN $seedClaimIds THEN 0 ELSE 1 END, c.recordedAt DESC
            LIMIT $limit
            """,
            new
            {
                seedClaimIds,
                entityIds,
                includeSuperseded,
                includeConflicts,
                limit = maxReturnedClaims,
            });

        var entityIdSet = entityIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seedClaimIdSet = seedClaimIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claims = (await ReadClaimsAsync(claimsResult))
            .Where(claim => IsClaimInMemorySubgraph(claim, entityIdSet, seedClaimIdSet))
            .ToList();
        var claimIds = claims.Select(claim => claim.Id).ToList();

        result.Claims = claims
            .Select(claim => new MemorySubgraphClaim
            {
                Claim = claim,
                Score = seedClaimIdSet.Contains(claim.Id)
                    ? 100
                    : Math.Max(1, 60 - (GetMemorySubgraphClaimHopDistance(claim, entityDistances) * 10)),
                HopDistance = seedClaimIdSet.Contains(claim.Id)
                    ? 0
                    : GetMemorySubgraphClaimHopDistance(claim, entityDistances),
                IsDirectSeed = seedClaimIdSet.Contains(claim.Id),
            })
            .OrderBy(claim => claim.HopDistance)
            .ThenByDescending(claim => claim.Score)
            .ToList();

        if (entityIds.Count > 0)
        {
            var entityEdgesResult = await session.RunAsync(
                """
                MATCH (a:MemoryEntity)-[r:ACTIVE_RELATES_TO]->(b:MemoryEntity)
                WHERE a.id IN $entityIds AND b.id IN $entityIds
                RETURN a.id AS fromEntityId, b.id AS toEntityId, r.edgeType AS edgeType,
                       r.bestActiveClaimId AS bestActiveClaimId, r.weight AS weight,
                       r.createdAt AS createdAt, r.updatedAt AS updatedAt
                """,
                new { entityIds });

            await foreach (var record in entityEdgesResult)
            {
                result.EntityEdges.Add(new MemoryEntityEdge
                {
                    FromEntityId = record["fromEntityId"].As<string>(),
                    ToEntityId = record["toEntityId"].As<string>(),
                    EdgeType = record["edgeType"].As<string>(),
                    BestActiveClaimId = record["bestActiveClaimId"].As<string?>(),
                    Weight = TryReadDecimal(record, "weight"),
                    CreatedAt = TryReadDateTime(record, "createdAt") ?? DateTime.UtcNow,
                    UpdatedAt = TryReadDateTime(record, "updatedAt") ?? DateTime.UtcNow,
                });
            }
        }

        if (claimIds.Count > 0)
        {
            var claimEdgesResult = await session.RunAsync(
                """
                MATCH (a:MemoryClaim)-[r]->(b:MemoryClaim)
                WHERE a.id IN $claimIds AND b.id IN $claimIds
                  AND type(r) IN ['SUPERSEDES', 'CONFLICTS_WITH', 'SUPPORTS', 'DERIVED_FROM']
                RETURN a.id AS fromClaimId, b.id AS toClaimId, type(r) AS edgeType,
                       r.weight AS weight, r.source AS source, r.createdAt AS createdAt
                """,
                new { claimIds });

            await foreach (var record in claimEdgesResult)
            {
                result.ClaimEdges.Add(new MemoryClaimEdge
                {
                    FromClaimId = record["fromClaimId"].As<string>(),
                    ToClaimId = record["toClaimId"].As<string>(),
                    EdgeType = record["edgeType"].As<string>().ToLowerInvariant(),
                    Weight = TryReadDecimal(record, "weight"),
                    Source = record["source"].As<string?>() ?? "unknown",
                    CreatedAt = TryReadDateTime(record, "createdAt") ?? DateTime.UtcNow,
                });
            }
        }

        result.Observations = entityIds.Count > 0 || claimIds.Count > 0
            ? await GetUnresolvedObservationsAsync(entityIds, claimIds)
            : [];

        result.Meta.FrontierExpanded = result.Entities.Count + result.Claims.Count;
        result.Meta.ResponseTruncated = result.Entities.Count >= maxReturnedEntities || result.Claims.Count >= maxReturnedClaims;
        result.Meta.SupersededClaimsHidden = includeSuperseded
            ? 0
            : await CountHiddenClaimsByStatusAsync(session, entityIds, seedClaimIds, claimIds, MemoryClaimStatus.Superseded);
        result.Meta.ActiveClaimsHidden = 0;

        return result;
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

    private static string NormalizeLookupKey(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var chars = input.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray();

        var normalized = new string(chars);
        while (normalized.Contains("__", StringComparison.Ordinal))
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);

        return normalized.Trim('_');
    }

    internal static string NormalizeMemorySearchText(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var builder = new StringBuilder(input.Length);
        var wroteSpace = false;

        foreach (var ch in input.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                wroteSpace = false;
                continue;
            }

            if (wroteSpace || builder.Length == 0)
                continue;

            builder.Append(' ');
            wroteSpace = true;
        }

        return builder.ToString().Trim();
    }

    internal static void MergeClaimPromotedEntityDistances(
        IDictionary<string, int> entityDistances,
        IEnumerable<string> claimPromotedEntityIds)
    {
        foreach (var entityId in claimPromotedEntityIds)
        {
            if (!entityDistances.TryGetValue(entityId, out var existingDistance) || existingDistance > 1)
                entityDistances[entityId] = 1;
        }
    }

    internal static List<string> RankMemorySubgraphEntityIds(
        IReadOnlyDictionary<string, int> entityDistances,
        IReadOnlySet<string> directSeedEntityIds,
        IReadOnlySet<string> claimPromotedEntityIds,
        int limit)
    {
        return entityDistances
            .OrderBy(kvp => directSeedEntityIds.Contains(kvp.Key) ? 0 : claimPromotedEntityIds.Contains(kvp.Key) ? 1 : 2)
            .ThenBy(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    internal static bool IsClaimInMemorySubgraph(
        MemoryClaim claim,
        IReadOnlySet<string> entityIds,
        IReadOnlySet<string> seedClaimIds)
    {
        if (seedClaimIds.Contains(claim.Id))
            return true;

        if (!entityIds.Contains(claim.SubjectEntityId))
            return false;

        return string.IsNullOrWhiteSpace(claim.ObjectEntityId) || entityIds.Contains(claim.ObjectEntityId);
    }

    internal static int GetMemorySubgraphClaimHopDistance(
        MemoryClaim claim,
        IReadOnlyDictionary<string, int> entityDistances)
    {
        var bestDistance = int.MaxValue;

        if (entityDistances.TryGetValue(claim.SubjectEntityId, out var subjectDistance))
            bestDistance = subjectDistance;

        if (!string.IsNullOrWhiteSpace(claim.ObjectEntityId)
            && entityDistances.TryGetValue(claim.ObjectEntityId, out var objectDistance))
        {
            bestDistance = Math.Min(bestDistance, objectDistance);
        }

        return bestDistance == int.MaxValue ? 0 : bestDistance;
    }

    internal static bool IsMissingMemoryFulltextIndex(ClientException ex) =>
        ex.Message.Contains("There is no such fulltext schema index: memory_entity_fulltext",
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsMissingMemoryClaimFulltextIndex(ClientException ex) =>
        ex.Message.Contains("There is no such fulltext schema index: memory_claim_fulltext",
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

    private static async Task<List<(MemoryClaim Claim, double Score)>> SearchClaimsByExactTextAsync(
        IAsyncSession session,
        string query,
        int limit,
        bool includeSuperseded)
    {
        var normalized = NormalizeMemorySearchText(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        var result = await session.RunAsync(
            """
            MATCH (c:MemoryClaim)-[:SUBJECT]->(subject:MemoryEntity)
            OPTIONAL MATCH (c)-[:OBJECT]->(object:MemoryEntity)
            WHERE ($includeSuperseded OR c.status <> 'superseded')
              AND (
                   toLower(coalesce(c.normalizedText, '')) = $query
                   OR toLower(coalesce(c.claimKey, '')) = $query
              )
            RETURN c.id AS id, c.claimKey AS claimKey, c.factGroupKey AS factGroupKey,
                   subject.id AS subjectEntityId, c.predicate AS predicate, object.id AS objectEntityId,
                   c.valueText AS valueText, c.valueJson AS valueJson, c.normalizedText AS normalizedText,
                   c.status AS status, c.confidence AS confidence, c.effectiveAt AS effectiveAt,
                   c.recordedAt AS recordedAt, c.supersedesClaimId AS supersedesClaimId,
                   c.source AS source, c.embedding AS embedding
            ORDER BY c.recordedAt DESC
            LIMIT $limit
            """,
            new { query = normalized, limit, includeSuperseded });

        return (await ReadClaimsAsync(result))
            .Select(claim => (claim, 100d))
            .ToList();
    }

    private static async Task<List<(MemoryClaim Claim, double Score)>> TextSearchClaimsWithFulltextAsync(
        IAsyncSession session,
        string query,
        int limit,
        bool includeSuperseded)
    {
        var result = await session.RunAsync(
            """
            CALL db.index.fulltext.queryNodes('memory_claim_fulltext', $query)
            YIELD node, score
            MATCH (node)-[:SUBJECT]->(subject:MemoryEntity)
            OPTIONAL MATCH (node)-[:OBJECT]->(object:MemoryEntity)
            WHERE ($includeSuperseded OR node.status <> 'superseded')
            RETURN node.id AS id, node.claimKey AS claimKey, node.factGroupKey AS factGroupKey,
                   subject.id AS subjectEntityId, node.predicate AS predicate, object.id AS objectEntityId,
                   node.valueText AS valueText, node.valueJson AS valueJson, node.normalizedText AS normalizedText,
                   node.status AS status, node.confidence AS confidence, node.effectiveAt AS effectiveAt,
                   node.recordedAt AS recordedAt, node.supersedesClaimId AS supersedesClaimId,
                   node.source AS source, node.embedding AS embedding, score AS score
            ORDER BY score DESC
            LIMIT $limit
            """,
            new { query = EscapeLuceneQuery(query), limit, includeSuperseded });

        return await ReadClaimsWithScoreAsync(result);
    }

    private static async Task<List<(MemoryClaim Claim, double Score)>> TextSearchClaimsWithFallbackAsync(
        IAsyncSession session,
        string query,
        int limit,
        bool includeSuperseded)
    {
        var normalized = query.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        var result = await session.RunAsync(
            """
            MATCH (c:MemoryClaim)-[:SUBJECT]->(subject:MemoryEntity)
            OPTIONAL MATCH (c)-[:OBJECT]->(object:MemoryEntity)
            WHERE $includeSuperseded OR c.status <> 'superseded'
            WITH c, subject, object,
                 CASE
                    WHEN toLower(coalesce(c.normalizedText, '')) = $query THEN 100
                    WHEN toLower(coalesce(c.predicate, '')) = $query THEN 90
                    WHEN toLower(coalesce(c.normalizedText, '')) CONTAINS $query THEN 75
                    WHEN toLower(coalesce(c.valueText, '')) CONTAINS $query THEN 65
                    ELSE 0
                 END AS score
            WHERE score > 0
            RETURN c.id AS id, c.claimKey AS claimKey, c.factGroupKey AS factGroupKey,
                   subject.id AS subjectEntityId, c.predicate AS predicate, object.id AS objectEntityId,
                   c.valueText AS valueText, c.valueJson AS valueJson, c.normalizedText AS normalizedText,
                   c.status AS status, c.confidence AS confidence, c.effectiveAt AS effectiveAt,
                   c.recordedAt AS recordedAt, c.supersedesClaimId AS supersedesClaimId,
                   c.source AS source, c.embedding AS embedding, score AS score
            ORDER BY score DESC, c.recordedAt DESC
            LIMIT $limit
            """,
            new { query = normalized, limit, includeSuperseded });

        return await ReadClaimsWithScoreAsync(result);
    }

    private static async Task<List<(MemoryClaim Claim, double Score)>> VectorSearchClaimsAsync(
        IAsyncSession session,
        float[] queryEmbedding,
        int limit,
        bool includeSuperseded)
    {
        var result = await session.RunAsync(
            """
            CALL db.index.vector.queryNodes('memory_claim_embedding', $limit, $embedding)
            YIELD node, score
            MATCH (node)-[:SUBJECT]->(subject:MemoryEntity)
            OPTIONAL MATCH (node)-[:OBJECT]->(object:MemoryEntity)
            WHERE ($includeSuperseded OR node.status <> 'superseded')
            RETURN node.id AS id, node.claimKey AS claimKey, node.factGroupKey AS factGroupKey,
                   subject.id AS subjectEntityId, node.predicate AS predicate, object.id AS objectEntityId,
                   node.valueText AS valueText, node.valueJson AS valueJson, node.normalizedText AS normalizedText,
                   node.status AS status, node.confidence AS confidence, node.effectiveAt AS effectiveAt,
                   node.recordedAt AS recordedAt, node.supersedesClaimId AS supersedesClaimId,
                   node.source AS source, node.embedding AS embedding, score AS score
            LIMIT $limit
            """,
            new { limit, embedding = queryEmbedding, includeSuperseded });

        return await ReadClaimsWithScoreAsync(result);
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

    private static async Task<List<(MemoryClaim Claim, double Score)>> ReadClaimsWithScoreAsync(IResultCursor result)
    {
        var claims = new List<(MemoryClaim Claim, double Score)>();
        await foreach (var record in result)
        {
            claims.Add((MapMemoryClaim(record), record["score"].As<double>()));
        }

        return claims;
    }

    private static MemoryEntity MapMemoryEntity(IRecord record)
    {
        var aliases = TryReadStringList(record, "aliases");

        return new MemoryEntity
        {
            Id = record["id"].As<string>(),
            Label = record["label"].As<string>(),
            Type = record["type"].As<string>(),
            ExternalId = TryReadString(record, "externalId"),
            CanonicalName = TryReadString(record, "canonicalName"),
            Aliases = aliases,
            Summary = record["summary"].As<string>(),
            Source = record["source"].As<string>(),
            CreatedAt = record["createdAt"].As<DateTimeOffset>().UtcDateTime,
            UpdatedAt = record["updatedAt"].As<DateTimeOffset>().UtcDateTime,
        };
    }

    private static MemoryClaim MapMemoryClaim(IRecord record)
    {
        return new MemoryClaim
        {
            Id = record["id"].As<string>(),
            ClaimKey = record["claimKey"].As<string>(),
            FactGroupKey = record["factGroupKey"].As<string>(),
            SubjectEntityId = record["subjectEntityId"].As<string>(),
            Predicate = record["predicate"].As<string>(),
            ObjectEntityId = record["objectEntityId"].As<string?>(),
            ValueText = record["valueText"].As<string?>(),
            ValueJson = record["valueJson"].As<string?>(),
            NormalizedText = record["normalizedText"].As<string>(),
            Status = ParseClaimStatus(record["status"].As<string>()),
            Confidence = TryReadDecimal(record, "confidence"),
            EffectiveAt = TryReadDateTime(record, "effectiveAt"),
            RecordedAt = record["recordedAt"].As<DateTimeOffset>().UtcDateTime,
            SupersedesClaimId = record["supersedesClaimId"].As<string?>(),
            Source = record["source"].As<string>(),
            Embedding = TryReadEmbedding(record, "embedding"),
        };
    }

    private static async Task<List<MemoryClaim>> ReadClaimsAsync(IResultCursor result)
    {
        var claims = new List<MemoryClaim>();
        await foreach (var record in result)
        {
            claims.Add(MapMemoryClaim(record));
        }

        return claims;
    }

    private static void MergeClaimSearchResult(
        IDictionary<string, (MemoryClaim Claim, double Score, string MatchKind)> results,
        MemoryClaim claim,
        double score,
        string matchKind)
    {
        if (results.TryGetValue(claim.Id, out var existing))
        {
            if (existing.Score >= score)
                return;
        }

        results[claim.Id] = (claim, score, matchKind);
    }

    private static async Task<int> CountHiddenClaimsByStatusAsync(
        IAsyncSession session,
        IReadOnlyList<string> entityIds,
        IReadOnlyList<string> seedClaimIds,
        IReadOnlyList<string> returnedClaimIds,
        MemoryClaimStatus claimStatus)
    {
        var result = await session.RunAsync(
            """
            MATCH (c:MemoryClaim)-[:SUBJECT]->(subject:MemoryEntity)
            OPTIONAL MATCH (c)-[:OBJECT]->(object:MemoryEntity)
            WHERE c.id IN $seedClaimIds
               OR subject.id IN $entityIds
               OR object.id IN $entityIds
            RETURN c.id AS id, c.claimKey AS claimKey, c.factGroupKey AS factGroupKey,
                   subject.id AS subjectEntityId, c.predicate AS predicate, object.id AS objectEntityId,
                   c.valueText AS valueText, c.valueJson AS valueJson, c.normalizedText AS normalizedText,
                   c.status AS status, c.confidence AS confidence, c.effectiveAt AS effectiveAt,
                   c.recordedAt AS recordedAt, c.supersedesClaimId AS supersedesClaimId,
                   c.source AS source, c.embedding AS embedding
            """,
            new { entityIds, seedClaimIds });

        var claims = await ReadClaimsAsync(result);
        return claims.Count(claim =>
            claim.Status == claimStatus
            && !returnedClaimIds.Contains(claim.Id, StringComparer.OrdinalIgnoreCase));
    }

    private static string ToClaimStatusString(MemoryClaimStatus status) =>
        status.ToString().ToLowerInvariant();

    internal static MemoryClaimStatus ParseClaimStatus(string status) =>
        Enum.TryParse<MemoryClaimStatus>(status, true, out var parsed)
            ? parsed
            : MemoryClaimStatus.Active;

    internal static bool IsClaimInEntityBundle(MemoryClaim claim, string entityId) =>
        claim.SubjectEntityId.Equals(entityId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(claim.ObjectEntityId, entityId, StringComparison.OrdinalIgnoreCase);

    internal static string MapClaimRelationshipType(string edgeType)
    {
        return edgeType.Trim().ToLowerInvariant() switch
        {
            "supersedes" => "SUPERSEDES",
            "conflicts_with" => "CONFLICTS_WITH",
            "supports" => "SUPPORTS",
            "derived_from" => "DERIVED_FROM",
            _ => throw new InvalidOperationException($"Unsupported memory claim edge type '{edgeType}'."),
        };
    }

    private static string? TryReadString(IRecord record, string key)
    {
        if (!record.Keys.Contains(key))
            return null;

        try
        {
            return record[key].As<string?>();
        }
        catch
        {
            return null;
        }
    }

    private static List<string> TryReadStringList(IRecord record, string key)
    {
        if (!record.Keys.Contains(key))
            return [];

        try
        {
            return record[key].As<List<string>>() ?? [];
        }
        catch
        {
            try
            {
                return record[key].As<List<object>>().Select(value => value.ToString() ?? string.Empty)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList();
            }
            catch
            {
                return [];
            }
        }
    }

    private static DateTime? TryReadDateTime(IRecord record, string key)
    {
        if (!record.Keys.Contains(key))
            return null;

        try
        {
            var value = record[key];
            if (value is null)
                return null;
            return value.As<DateTimeOffset>().UtcDateTime;
        }
        catch
        {
            return null;
        }
    }

    private static decimal? TryReadDecimal(IRecord record, string key)
    {
        if (!record.Keys.Contains(key))
            return null;

        try
        {
            return record[key].As<decimal?>();
        }
        catch
        {
            try
            {
                var doubleValue = record[key].As<double?>();
                return doubleValue.HasValue ? Convert.ToDecimal(doubleValue.Value) : null;
            }
            catch
            {
                return null;
            }
        }
    }

    private static float[]? TryReadEmbedding(IRecord record, string key)
    {
        if (!record.Keys.Contains(key))
            return null;

        try
        {
            var embeddingList = record[key].As<List<object>>();
            return embeddingList?.Select(v => Convert.ToSingle(v)).ToArray();
        }
        catch
        {
            return null;
        }
    }
}
