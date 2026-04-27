using System.Text.Json;
using CodeGraph.Models.Memory;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public class MySqlMemoryGraphStore(IOptions<MariaDbStorageOptions> optionsAccessor)
    : IMemoryGraphStore
{
    private const string DefaultUsername = "default";
    private readonly MariaDbStorageOptions options = optionsAccessor.Value;

    public async Task CreateWriteReceiptAsync(MemoryWriteReceipt receipt)
    {
        const string sql = """
            INSERT INTO memory_write_receipts (
                receipt_id, username, source, input_mode, status, requested_nodes,
                requested_edges, evidence_requested, submitted_at, entities_upserted,
                claims_inserted, claims_superseded, claims_conflicted,
                duplicate_claims_skipped, evidence_written, observations_written,
                retryable_error_count, legacy_nodes_written, legacy_edges_written,
                legacy_conflicts_detected, error, updated_at)
            VALUES (
                @Id, @Username, @Source, @InputMode, @Status, @EntitiesRequested,
                @ClaimsRequested, @EvidenceRequested, @CreatedAt, @NodesWritten,
                @ClaimsWritten, 0, @ConflictsDetected, 0, @EvidenceWritten,
                @ObservationsWritten, @AttemptCount, @NodesWritten, @EdgesWritten,
                @ConflictsDetected, @ErrorMessage, @UpdatedAt)
            ON DUPLICATE KEY UPDATE
                source = VALUES(source),
                input_mode = VALUES(input_mode),
                status = VALUES(status),
                requested_nodes = VALUES(requested_nodes),
                requested_edges = VALUES(requested_edges),
                evidence_requested = VALUES(evidence_requested),
                entities_upserted = VALUES(entities_upserted),
                claims_inserted = VALUES(claims_inserted),
                evidence_written = VALUES(evidence_written),
                observations_written = VALUES(observations_written),
                retryable_error_count = VALUES(retryable_error_count),
                legacy_nodes_written = VALUES(legacy_nodes_written),
                legacy_edges_written = VALUES(legacy_edges_written),
                legacy_conflicts_detected = VALUES(legacy_conflicts_detected),
                error = VALUES(error),
                updated_at = VALUES(updated_at);
            """;

        await using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, ToReceiptParameters(receipt));
    }

    public async Task<MemoryWriteReceipt?> GetWriteReceiptAsync(string receiptId)
    {
        const string sql = """
            SELECT receipt_id AS Id,
                   source AS Source,
                   input_mode AS InputMode,
                   status AS Status,
                   requested_nodes AS EntitiesRequested,
                   requested_edges AS ClaimsRequested,
                   evidence_requested AS EvidenceRequested,
                   retryable_error_count AS AttemptCount,
                   legacy_nodes_written AS NodesWritten,
                   legacy_edges_written AS EdgesWritten,
                   legacy_conflicts_detected AS ConflictsDetected,
                   claims_inserted AS ClaimsWritten,
                   evidence_written AS EvidenceWritten,
                   observations_written AS ObservationsWritten,
                   error AS ErrorMessage,
                   submitted_at AS CreatedAt,
                   updated_at AS UpdatedAt,
                   processing_started_at AS StartedAt,
                   completed_at AS CompletedAt
            FROM memory_write_receipts
            WHERE receipt_id = @receiptId
            LIMIT 1;
            """;

        await using var connection = CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<MemoryWriteReceiptRow>(sql, new { receiptId });
        return row?.ToModel();
    }

    public async Task UpdateWriteReceiptStatusAsync(
        string receiptId,
        MemoryWriteReceiptStatus status,
        StoreMemoryResult? result = null,
        string? errorMessage = null)
    {
        const string sql = """
            UPDATE memory_write_receipts
            SET status = @Status,
                updated_at = @Now,
                error = @ErrorMessage,
                processing_started_at = CASE
                    WHEN @Status = 'processing' THEN COALESCE(processing_started_at, @Now)
                    ELSE processing_started_at
                END,
                completed_at = CASE
                    WHEN @Status IN ('completed', 'failed') THEN @Now
                    ELSE completed_at
                END,
                retryable_error_count = CASE
                    WHEN @Status = 'processing' THEN retryable_error_count + 1
                    ELSE retryable_error_count
                END,
                legacy_nodes_written = COALESCE(@NodesWritten, legacy_nodes_written),
                legacy_edges_written = COALESCE(@EdgesWritten, legacy_edges_written),
                legacy_conflicts_detected = COALESCE(@ConflictsDetected, legacy_conflicts_detected),
                claims_inserted = COALESCE(@ClaimsWritten, claims_inserted),
                evidence_written = COALESCE(@EvidenceWritten, evidence_written),
                observations_written = COALESCE(@ObservationsWritten, observations_written)
            WHERE receipt_id = @ReceiptId;
            """;

        await using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, new
        {
            ReceiptId = receiptId,
            Status = ToStatus(status),
            Now = DateTime.UtcNow,
            ErrorMessage = errorMessage,
            NodesWritten = result?.NodesWritten,
            EdgesWritten = result?.EdgesWritten,
            ConflictsDetected = result?.ConflictsDetected,
            ClaimsWritten = result?.ClaimsWritten,
            EvidenceWritten = result?.EvidenceWritten,
            ObservationsWritten = result?.ObservationsWritten,
        });
    }

    public async Task UpsertEntitiesBatchAsync(IReadOnlyList<MemoryEntity> entities)
    {
        if (entities.Count == 0)
            return;

        const string sql = """
            INSERT INTO memory_entities_v2 (
                username, external_id, canonical_id, label, type, canonical_name,
                summary, embedding_json, created_at, updated_at)
            VALUES (
                @Username, @Id, @CanonicalId, @Label, @Type, @CanonicalName,
                @Summary, @EmbeddingJson, @CreatedAt, @UpdatedAt)
            ON DUPLICATE KEY UPDATE
                label = VALUES(label),
                type = VALUES(type),
                canonical_name = COALESCE(VALUES(canonical_name), canonical_name),
                summary = CASE
                    WHEN summary IS NULL OR summary = '' THEN VALUES(summary)
                    WHEN VALUES(summary) IS NULL OR VALUES(summary) = '' THEN summary
                    WHEN LOCATE(VALUES(summary), summary) > 0 THEN summary
                    WHEN CHAR_LENGTH(summary) > 1000 THEN VALUES(summary)
                    ELSE CONCAT(summary, ' | ', VALUES(summary))
                END,
                embedding_json = COALESCE(VALUES(embedding_json), embedding_json),
                updated_at = VALUES(updated_at);
            """;

        await using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, entities.Select(entity => new
        {
            Username = DefaultUsername,
            entity.Id,
            CanonicalId = string.IsNullOrWhiteSpace(entity.ExternalId) ? entity.Id : entity.ExternalId,
            entity.Label,
            entity.Type,
            entity.CanonicalName,
            entity.Summary,
            EmbeddingJson = SerializeEmbedding(entity.Embedding),
            entity.CreatedAt,
            entity.UpdatedAt,
        }));
    }

    public async Task UpsertClaimsBatchAsync(IReadOnlyList<MemoryClaim> claims)
    {
        if (claims.Count == 0)
            return;

        await using var connection = CreateConnection();
        foreach (var claim in claims)
        {
            var subjectId = await GetEntityRowIdAsync(connection, claim.SubjectEntityId);
            if (!subjectId.HasValue)
                continue;

            var objectId = string.IsNullOrWhiteSpace(claim.ObjectEntityId)
                ? null
                : await GetEntityRowIdAsync(connection, claim.ObjectEntityId);

            await connection.ExecuteAsync(
                """
                INSERT INTO memory_claims (
                    username, external_id, claim_key, fact_group_key, subject_entity_id,
                    predicate, object_entity_id, value_text, value_json, normalized_text,
                    status, confidence, effective_at, recorded_at, supersedes_claim_id,
                    source, embedding_json)
                VALUES (
                    @Username, @Id, @ClaimKey, @FactGroupKey, @SubjectEntityId,
                    @Predicate, @ObjectEntityId, @ValueText, @ValueJson, @NormalizedText,
                    @Status, @Confidence, @EffectiveAt, @RecordedAt, @SupersedesClaimId,
                    @Source, @EmbeddingJson)
                ON DUPLICATE KEY UPDATE
                    claim_key = VALUES(claim_key),
                    fact_group_key = VALUES(fact_group_key),
                    subject_entity_id = VALUES(subject_entity_id),
                    predicate = VALUES(predicate),
                    object_entity_id = VALUES(object_entity_id),
                    value_text = VALUES(value_text),
                    value_json = VALUES(value_json),
                    normalized_text = VALUES(normalized_text),
                    status = VALUES(status),
                    confidence = VALUES(confidence),
                    effective_at = VALUES(effective_at),
                    recorded_at = VALUES(recorded_at),
                    supersedes_claim_id = VALUES(supersedes_claim_id),
                    source = VALUES(source),
                    embedding_json = COALESCE(VALUES(embedding_json), embedding_json);
                """,
                new
                {
                    Username = DefaultUsername,
                    claim.Id,
                    claim.ClaimKey,
                    claim.FactGroupKey,
                    SubjectEntityId = subjectId.Value,
                    claim.Predicate,
                    ObjectEntityId = objectId,
                    claim.ValueText,
                    claim.ValueJson,
                    claim.NormalizedText,
                    Status = ToStatus(claim.Status),
                    claim.Confidence,
                    claim.EffectiveAt,
                    claim.RecordedAt,
                    SupersedesClaimId = await GetClaimRowIdAsync(connection, claim.SupersedesClaimId),
                    claim.Source,
                    EmbeddingJson = SerializeEmbedding(claim.Embedding),
                });
        }
    }

    public async Task AddClaimEdgesBatchAsync(IReadOnlyList<MemoryClaimEdge> edges)
    {
        if (edges.Count == 0)
            return;

        await using var connection = CreateConnection();
        foreach (var edge in edges)
        {
            var fromClaimId = await GetClaimRowIdAsync(connection, edge.FromClaimId);
            var toClaimId = await GetClaimRowIdAsync(connection, edge.ToClaimId);
            if (!fromClaimId.HasValue || !toClaimId.HasValue)
                continue;

            await connection.ExecuteAsync(
                """
                INSERT INTO memory_claim_edges (
                    username, from_claim_id, to_claim_id, edge_type, weight, source, created_at)
                VALUES (@Username, @FromClaimId, @ToClaimId, @EdgeType, @Weight, @Source, @CreatedAt);
                """,
                new
                {
                    Username = DefaultUsername,
                    FromClaimId = fromClaimId.Value,
                    ToClaimId = toClaimId.Value,
                    edge.EdgeType,
                    edge.Weight,
                    edge.Source,
                    edge.CreatedAt,
                });
        }
    }

    public async Task AddEvidenceBatchAsync(IReadOnlyList<MemoryEvidence> evidence)
    {
        if (evidence.Count == 0)
            return;

        await using var connection = CreateConnection();
        foreach (var item in evidence)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO memory_evidence (
                    username, external_id, claim_id, observation_id, evidence_type,
                    source_ref, snippet, metadata_json, created_at)
                VALUES (
                    @Username, @Id, @ClaimId, @ObservationId, @EvidenceType,
                    @SourceRef, @Snippet, @MetadataJson, @CreatedAt)
                ON DUPLICATE KEY UPDATE
                    claim_id = VALUES(claim_id),
                    observation_id = VALUES(observation_id),
                    evidence_type = VALUES(evidence_type),
                    source_ref = VALUES(source_ref),
                    snippet = VALUES(snippet),
                    metadata_json = VALUES(metadata_json);
                """,
                new
                {
                    Username = DefaultUsername,
                    item.Id,
                    ClaimId = await GetClaimRowIdAsync(connection, item.ClaimId),
                    ObservationId = await GetObservationRowIdAsync(connection, item.ObservationId),
                    item.EvidenceType,
                    item.SourceRef,
                    item.Snippet,
                    item.MetadataJson,
                    item.CreatedAt,
                });
        }
    }

    public async Task UpsertEntityEdgesBatchAsync(IReadOnlyList<MemoryEntityEdge> edges)
    {
        if (edges.Count == 0)
            return;

        await using var connection = CreateConnection();
        foreach (var edge in edges)
        {
            var fromEntityId = await GetEntityRowIdAsync(connection, edge.FromEntityId);
            var toEntityId = await GetEntityRowIdAsync(connection, edge.ToEntityId);
            if (!fromEntityId.HasValue || !toEntityId.HasValue)
                continue;

            await connection.ExecuteAsync(
                """
                INSERT INTO memory_entity_edges (
                    username, from_entity_id, to_entity_id, edge_type,
                    best_active_claim_id, weight, created_at, updated_at)
                VALUES (
                    @Username, @FromEntityId, @ToEntityId, @EdgeType,
                    @BestActiveClaimId, @Weight, @CreatedAt, @UpdatedAt)
                ON DUPLICATE KEY UPDATE
                    best_active_claim_id = VALUES(best_active_claim_id),
                    weight = VALUES(weight),
                    updated_at = VALUES(updated_at);
                """,
                new
                {
                    Username = DefaultUsername,
                    FromEntityId = fromEntityId.Value,
                    ToEntityId = toEntityId.Value,
                    edge.EdgeType,
                    BestActiveClaimId = await GetClaimRowIdAsync(connection, edge.BestActiveClaimId),
                    edge.Weight,
                    edge.CreatedAt,
                    edge.UpdatedAt,
                });
        }
    }

    public async Task CreateObservationAsync(MemoryObservation obs)
    {
        await using var connection = CreateConnection();
        var primaryClaimId = obs.AboutClaimIds.Count > 0
            ? await GetClaimRowIdAsync(connection, obs.AboutClaimIds[0])
            : null;
        var relatedClaimId = obs.AboutClaimIds.Count > 1
            ? await GetClaimRowIdAsync(connection, obs.AboutClaimIds[1])
            : null;
        var entityId = obs.AboutEntityIds.Count > 0
            ? await GetEntityRowIdAsync(connection, obs.AboutEntityIds[0])
            : null;

        await connection.ExecuteAsync(
            """
            INSERT INTO memory_observations_v2 (
                username, external_id, legacy_claim_text, legacy_conflicts_with,
                legacy_source, observation_type, claim_id, related_claim_id, entity_id,
                message, resolution_status, legacy_resolution,
                legacy_resolved_by_memory_id, created_at, resolved_at)
            VALUES (
                @Username, @Id, @Claim, @ConflictsWith, @Source, 'conflict',
                @ClaimId, @RelatedClaimId, @EntityId, @Message, @ResolutionStatus,
                @Resolution, @ResolvedByMemoryId, @Timestamp, @ResolvedAt)
            ON DUPLICATE KEY UPDATE
                legacy_claim_text = VALUES(legacy_claim_text),
                legacy_conflicts_with = VALUES(legacy_conflicts_with),
                legacy_source = VALUES(legacy_source),
                claim_id = VALUES(claim_id),
                related_claim_id = VALUES(related_claim_id),
                entity_id = VALUES(entity_id),
                message = VALUES(message),
                resolution_status = VALUES(resolution_status),
                legacy_resolution = VALUES(legacy_resolution),
                legacy_resolved_by_memory_id = VALUES(legacy_resolved_by_memory_id),
                resolved_at = VALUES(resolved_at);
            """,
            new
            {
                Username = DefaultUsername,
                obs.Id,
                obs.Claim,
                obs.ConflictsWith,
                obs.Source,
                ClaimId = primaryClaimId,
                RelatedClaimId = relatedClaimId,
                EntityId = entityId,
                Message = string.IsNullOrWhiteSpace(obs.Claim) ? "Memory observation" : obs.Claim,
                ResolutionStatus = obs.Resolved ? "resolved" : "open",
                obs.Resolution,
                obs.ResolvedByMemoryId,
                obs.Timestamp,
                ResolvedAt = obs.Resolved ? obs.Timestamp : (DateTime?)null,
            });
    }

    public async Task ResolveObservationAsync(string observationId, string resolution, string? resolvedByMemoryId)
    {
        await using var connection = CreateConnection();
        await connection.ExecuteAsync(
            """
            UPDATE memory_observations_v2
            SET resolution_status = 'resolved',
                legacy_resolution = @resolution,
                legacy_resolved_by_memory_id = @resolvedByMemoryId,
                resolved_at = @resolvedAt
            WHERE username = @username AND external_id = @observationId;
            """,
            new
            {
                username = DefaultUsername,
                observationId,
                resolution,
                resolvedByMemoryId,
                resolvedAt = DateTime.UtcNow,
            });
    }

    public async Task<List<(MemoryEntity Entity, double Score)>> VectorSearchAsync(float[] queryEmbedding, int topK = 5)
    {
        await using var connection = CreateConnection();
        var entities = (await connection.QueryAsync<MemoryEntityRow>(
            """
            SELECT id AS RowId, external_id AS Id, canonical_id AS ExternalId, label, type,
                   canonical_name AS CanonicalName, summary, 'memory' AS Source,
                   embedding_json AS EmbeddingJson, created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM memory_entities_v2
            WHERE username = @username AND embedding_json IS NOT NULL
            ORDER BY updated_at DESC;
            """,
            new { username = DefaultUsername })).ToList();

        return entities
            .Select(row => (Entity: row.ToModel(), Score: CosineSimilarity(queryEmbedding, DeserializeEmbedding(row.EmbeddingJson))))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .Take(topK)
            .ToList();
    }

    public async Task<List<MemoryEntity>> TextSearchAsync(string query, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        await using var connection = CreateConnection();
        var rows = await connection.QueryAsync<MemoryEntityRow>(
            """
            SELECT id AS RowId, external_id AS Id, canonical_id AS ExternalId, label, type,
                   canonical_name AS CanonicalName, summary, 'memory' AS Source,
                   embedding_json AS EmbeddingJson, created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM memory_entities_v2
            WHERE username = @username
              AND (
                  external_id LIKE @likeQuery
                  OR label LIKE @likeQuery
                  OR canonical_name LIKE @likeQuery
                  OR summary LIKE @likeQuery
              )
            ORDER BY updated_at DESC
            LIMIT @limit;
            """,
            new { username = DefaultUsername, likeQuery = $"%{query.Trim()}%", limit });

        return rows.Select(row => row.ToModel()).ToList();
    }

    public async Task<List<MemoryRelationshipDetail>> GetRelationshipsAsync(string entityId)
    {
        await using var connection = CreateConnection();
        var rows = await connection.QueryAsync<MemoryRelationshipRow>(
            """
            SELECT CASE WHEN source.external_id = @entityId THEN 'outgoing' ELSE 'incoming' END AS Direction,
                   edge.edge_type AS RelationshipType,
                   CASE WHEN source.external_id = @entityId THEN target.label ELSE source.label END AS TargetLabel,
                   CASE WHEN source.external_id = @entityId THEN target.external_id ELSE source.external_id END AS TargetId,
                   claim.external_id AS Context,
                   edge.updated_at AS Timestamp
            FROM memory_entity_edges edge
            JOIN memory_entities_v2 source ON source.id = edge.from_entity_id
            JOIN memory_entities_v2 target ON target.id = edge.to_entity_id
            LEFT JOIN memory_claims claim ON claim.id = edge.best_active_claim_id
            WHERE edge.username = @username
              AND (source.external_id = @entityId OR target.external_id = @entityId)
            ORDER BY edge.updated_at DESC;
            """,
            new { username = DefaultUsername, entityId });

        return rows.Select(row => new MemoryRelationshipDetail
        {
            Direction = row.Direction,
            RelationshipType = row.RelationshipType,
            TargetLabel = row.TargetLabel,
            TargetId = row.TargetId,
            Context = row.Context,
            Timestamp = row.Timestamp,
        }).ToList();
    }

    public async Task<List<MemoryObservation>> GetUnresolvedObservationsAsync(
        IEnumerable<string> entityIds,
        IEnumerable<string> claimIds)
    {
        var entitySet = entityIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claimSet = claimIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (entitySet.Count == 0 && claimSet.Count == 0)
            return [];

        var observations = await GetAllObservationsAsync();
        return observations
            .Where(obs => !obs.Resolved)
            .Where(obs =>
                obs.AboutEntityIds.Any(entitySet.Contains)
                || obs.AboutClaimIds.Any(claimSet.Contains)
                || entitySet.Any(id => obs.Claim.Contains(id, StringComparison.OrdinalIgnoreCase)
                    || obs.ConflictsWith.Contains(id, StringComparison.OrdinalIgnoreCase))
                || claimSet.Any(id => obs.Claim.Contains(id, StringComparison.OrdinalIgnoreCase)
                    || obs.ConflictsWith.Contains(id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public Task<List<MemoryObservation>> GetUnresolvedObservationsForEntitiesAsync(IEnumerable<string> entityIds) =>
        GetUnresolvedObservationsAsync(entityIds, []);

    public async Task<MemoryGraphSnapshot> GetFullGraphAsync(int limit = 200, int skip = 0)
    {
        await using var connection = CreateConnection();
        var rows = (await connection.QueryAsync<MemoryEntityRow>(
            """
            SELECT id AS RowId, external_id AS Id, canonical_id AS ExternalId, label, type,
                   canonical_name AS CanonicalName, summary, 'memory' AS Source,
                   embedding_json AS EmbeddingJson, created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM memory_entities_v2
            WHERE username = @username
            ORDER BY updated_at DESC
            LIMIT @limit OFFSET @skip;
            """,
            new { username = DefaultUsername, limit, skip })).ToList();

        var snapshot = new MemoryGraphSnapshot
        {
            Nodes = rows.Select(row => new MemoryGraphNode
            {
                Id = row.Id,
                Label = row.Label,
                Type = row.Type,
                Summary = row.Summary ?? "",
                Source = row.Source,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt,
            }).ToList(),
            TotalNodeCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM memory_entities_v2 WHERE username = @username;",
                new { username = DefaultUsername }),
        };

        if (snapshot.Nodes.Count == 0)
            return snapshot;

        var nodeIds = rows.Select(row => row.RowId).ToList();
        snapshot.Links = (await QueryEntityEdgesAsync(connection, nodeIds, nodeIds))
            .Select(ToGraphLink)
            .ToList();
        return snapshot;
    }

    public async Task<MemoryGraphSnapshot> GetEntityGraphAsync(string entityId, int neighborLimit = 200)
    {
        var entity = await GetEntityAsync(entityId);
        if (entity is null)
            return new MemoryGraphSnapshot();

        await using var connection = CreateConnection();
        var centerRowId = await GetEntityRowIdAsync(connection, entityId);
        if (!centerRowId.HasValue)
            return new MemoryGraphSnapshot();

        var relatedRows = (await connection.QueryAsync<MemoryEntityRow>(
            """
            SELECT DISTINCT entity.id AS RowId, entity.external_id AS Id, entity.canonical_id AS ExternalId,
                   entity.label, entity.type, entity.canonical_name AS CanonicalName, entity.summary,
                   'memory' AS Source, entity.embedding_json AS EmbeddingJson,
                   entity.created_at AS CreatedAt, entity.updated_at AS UpdatedAt
            FROM memory_entities_v2 entity
            JOIN memory_entity_edges edge
              ON entity.id = edge.from_entity_id OR entity.id = edge.to_entity_id
            WHERE edge.username = @username
              AND (edge.from_entity_id = @rowId OR edge.to_entity_id = @rowId)
            ORDER BY entity.updated_at DESC
            LIMIT @limit;
            """,
            new { username = DefaultUsername, rowId = centerRowId.Value, limit = Math.Clamp(neighborLimit, 1, 500) }))
            .ToList();

        var rows = new List<MemoryEntityRow>
        {
            new()
            {
                RowId = centerRowId.Value,
                Id = entity.Id,
                Label = entity.Label,
                Type = entity.Type,
                Summary = entity.Summary,
                Source = entity.Source,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt,
            }
        };
        rows.AddRange(relatedRows.Where(row => !row.Id.Equals(entityId, StringComparison.OrdinalIgnoreCase)));

        var rowIds = rows.Select(row => row.RowId).ToList();
        return new MemoryGraphSnapshot
        {
            TotalNodeCount = rows.Count,
            Nodes = rows.Select(row => new MemoryGraphNode
            {
                Id = row.Id,
                Label = row.Label,
                Type = row.Type,
                Summary = row.Summary ?? "",
                Source = row.Source,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt,
            }).ToList(),
            Links = (await QueryEntityEdgesAsync(connection, rowIds, rowIds)).Select(ToGraphLink).ToList(),
        };
    }

    public async Task<List<string>> FindCandidateEntityIdsAsync(string candidateId, int limit = 20)
    {
        var textResults = await TextSearchAsync(candidateId.Replace("_", " "), limit);
        return textResults.Select(entity => entity.Id).ToList();
    }

    public async Task<MemoryEntity?> GetEntityAsync(string entityId)
    {
        await using var connection = CreateConnection();
        return await GetEntityAsync(connection, entityId);
    }

    public async Task<MemoryEntity?> GetEntityByExternalIdAsync(string externalId)
    {
        await using var connection = CreateConnection();
        return await GetEntityAsync(connection, externalId);
    }

    public async Task<List<MemoryLegacyRelationship>> GetLegacyRelationshipsAsync()
    {
        var relationships = await GetRelationshipsForAllEntitiesAsync();
        return relationships.Select(row => new MemoryLegacyRelationship
        {
            FromEntityId = row.SourceId ?? "",
            ToEntityId = row.TargetId,
            RelationshipType = row.RelationshipType,
            Context = row.Context,
            Source = "memory_entity_edges",
            Timestamp = row.Timestamp,
        }).ToList();
    }

    public async Task<List<MemoryObservation>> GetAllObservationsAsync()
    {
        await using var connection = CreateConnection();
        var rows = await connection.QueryAsync<MemoryObservationRow>(
            """
            SELECT obs.external_id AS Id,
                   COALESCE(obs.legacy_claim_text, obs.message) AS Claim,
                   COALESCE(obs.legacy_conflicts_with, '') AS ConflictsWith,
                   COALESCE(obs.legacy_source, 'memory') AS Source,
                   obs.created_at AS Timestamp,
                   CASE WHEN obs.resolution_status = 'resolved' THEN TRUE ELSE FALSE END AS Resolved,
                   obs.legacy_resolution AS Resolution,
                   obs.legacy_resolved_by_memory_id AS ResolvedByMemoryId,
                   entity.external_id AS EntityId,
                   claim.external_id AS ClaimId,
                   related.external_id AS RelatedClaimId
            FROM memory_observations_v2 obs
            LEFT JOIN memory_entities_v2 entity ON entity.id = obs.entity_id
            LEFT JOIN memory_claims claim ON claim.id = obs.claim_id
            LEFT JOIN memory_claims related ON related.id = obs.related_claim_id
            WHERE obs.username = @username
            ORDER BY obs.created_at ASC, obs.id ASC;
            """,
            new { username = DefaultUsername });

        return rows.Select(row => row.ToModel()).ToList();
    }

    public async Task<MemoryClaim?> GetClaimAsync(string claimId)
    {
        await using var connection = CreateConnection();
        return await GetClaimAsync(connection, claimId);
    }

    public async Task<List<MemoryClaim>> GetClaimsByFactGroupAsync(string factGroupKey)
    {
        await using var connection = CreateConnection();
        var rows = await connection.QueryAsync<MemoryClaimRow>(
            ClaimSelectSql + "\n" + """
            WHERE memory_claim.username = @username AND memory_claim.fact_group_key = @factGroupKey
            ORDER BY memory_claim.recorded_at DESC;
            """,
            new { username = DefaultUsername, factGroupKey });

        return rows.Select(row => row.ToModel()).ToList();
    }

    public async Task<List<MemoryClaim>> GetClaimsBySubjectPredicateAsync(string subjectEntityId, string predicate)
    {
        await using var connection = CreateConnection();
        var rows = await connection.QueryAsync<MemoryClaimRow>(
            ClaimSelectSql + "\n" + """
            WHERE memory_claim.username = @username
              AND subject.external_id = @subjectEntityId
              AND memory_claim.predicate = @predicate
            ORDER BY memory_claim.recorded_at DESC;
            """,
            new { username = DefaultUsername, subjectEntityId, predicate });

        return rows.Select(row => row.ToModel()).ToList();
    }

    public async Task<MemoryEntityBundle?> GetEntityBundleAsync(
        string entityId,
        bool includeSuperseded = false,
        bool includeConflicts = true,
        int neighborLimit = 20)
    {
        var entity = await GetEntityAsync(entityId);
        if (entity is null)
            return null;

        await using var connection = CreateConnection();
        var claims = (await connection.QueryAsync<MemoryClaimRow>(
            ClaimSelectSql + "\n" + """
            WHERE memory_claim.username = @username
              AND (subject.external_id = @entityId OR object_entity.external_id = @entityId)
            ORDER BY memory_claim.recorded_at DESC;
            """,
            new { username = DefaultUsername, entityId }))
            .Select(row => row.ToModel())
            .ToList();

        var centerRowId = await GetEntityRowIdAsync(connection, entityId);
        var neighborEdges = centerRowId.HasValue
            ? (await QueryEntityEdgesAsync(connection, [centerRowId.Value], null))
                .Take(Math.Clamp(neighborLimit, 1, 100))
                .Select(ToEntityEdge)
                .ToList()
            : [];

        return new MemoryEntityBundle
        {
            Entity = entity,
            ActiveClaims = claims.Where(claim => claim.Status == MemoryClaimStatus.Active).ToList(),
            ConflictingClaims = includeConflicts
                ? claims.Where(claim => claim.Status == MemoryClaimStatus.Conflicted).ToList()
                : [],
            SupersededClaims = includeSuperseded
                ? claims.Where(claim => claim.Status == MemoryClaimStatus.Superseded).ToList()
                : [],
            NeighborEdges = neighborEdges,
            Observations = await GetUnresolvedObservationsForEntitiesAsync([entityId]),
        };
    }

    public async Task<MemoryClaimBundle?> GetClaimBundleAsync(
        string claimId,
        bool includeSupersessionChain = true,
        bool includeConflicts = true,
        bool includeEvidence = true)
    {
        var claim = await GetClaimAsync(claimId);
        if (claim is null)
            return null;

        await using var connection = CreateConnection();
        var bundle = new MemoryClaimBundle
        {
            Claim = claim,
            FactGroupPeers = (await GetClaimsByFactGroupAsync(claim.FactGroupKey))
                .Where(peer => !peer.Id.Equals(claim.Id, StringComparison.OrdinalIgnoreCase))
                .ToList(),
        };

        if (includeSupersessionChain || includeConflicts)
        {
            var edgeRows = await connection.QueryAsync<MemoryClaimEdgeRow>(
                """
                SELECT source.external_id AS FromClaimId, target.external_id AS ToClaimId,
                       edge.edge_type AS EdgeType, edge.weight AS Weight,
                       edge.source AS Source, edge.created_at AS CreatedAt
                FROM memory_claim_edges edge
                JOIN memory_claims source ON source.id = edge.from_claim_id
                JOIN memory_claims target ON target.id = edge.to_claim_id
                WHERE edge.username = @username
                  AND (source.external_id = @claimId OR target.external_id = @claimId);
                """,
                new { username = DefaultUsername, claimId });

            foreach (var edge in edgeRows)
            {
                var otherId = edge.FromClaimId.Equals(claimId, StringComparison.OrdinalIgnoreCase)
                    ? edge.ToClaimId
                    : edge.FromClaimId;
                var otherClaim = await GetClaimAsync(connection, otherId);
                if (otherClaim is null)
                    continue;

                if (edge.EdgeType.Equals("supersedes", StringComparison.OrdinalIgnoreCase) && includeSupersessionChain)
                    bundle.SupersessionChain.Add(otherClaim);
                if (edge.EdgeType.Equals("conflicts_with", StringComparison.OrdinalIgnoreCase) && includeConflicts)
                    bundle.Conflicts.Add(otherClaim);
            }
        }

        if (includeEvidence)
        {
            bundle.Evidence = (await connection.QueryAsync<MemoryEvidenceRow>(
                """
                SELECT evidence.external_id AS Id, claim.external_id AS ClaimId,
                       obs.external_id AS ObservationId, evidence.evidence_type AS EvidenceType,
                       evidence.source_ref AS SourceRef, evidence.snippet AS Snippet,
                       evidence.metadata_json AS MetadataJson, evidence.created_at AS CreatedAt
                FROM memory_evidence evidence
                LEFT JOIN memory_claims claim ON claim.id = evidence.claim_id
                LEFT JOIN memory_observations_v2 obs ON obs.id = evidence.observation_id
                WHERE evidence.username = @username AND claim.external_id = @claimId
                ORDER BY evidence.created_at DESC;
                """,
                new { username = DefaultUsername, claimId }))
                .Select(row => row.ToModel())
                .ToList();
        }

        bundle.Observations = await GetUnresolvedObservationsAsync(
            [claim.SubjectEntityId, claim.ObjectEntityId ?? ""],
            [claim.Id]);
        return bundle;
    }

    public async Task<List<(MemoryClaim Claim, double Score, string MatchKind)>> SearchClaimsAsync(
        string query,
        float[]? queryEmbedding,
        int limit = 5,
        bool includeSuperseded = false)
    {
        await using var connection = CreateConnection();
        var exact = await GetClaimAsync(connection, NormalizeLookupKey(query));
        var results = new Dictionary<string, (MemoryClaim Claim, double Score, string MatchKind)>(StringComparer.OrdinalIgnoreCase);
        if (exact is not null && (includeSuperseded || exact.Status != MemoryClaimStatus.Superseded))
            results[exact.Id] = (exact, 100, "exact");

        var rows = await connection.QueryAsync<MemoryClaimRow>(
            ClaimSelectSql + "\n" + """
            WHERE memory_claim.username = @username
              AND (@includeSuperseded OR memory_claim.status <> 'superseded')
              AND (
                  memory_claim.external_id LIKE @likeQuery
                  OR memory_claim.normalized_text LIKE @likeQuery
                  OR memory_claim.predicate LIKE @likeQuery
                  OR memory_claim.value_text LIKE @likeQuery
              )
            ORDER BY memory_claim.recorded_at DESC
            LIMIT @limit;
            """,
            new
            {
                username = DefaultUsername,
                includeSuperseded,
                likeQuery = $"%{query.Trim()}%",
                limit,
            });

        foreach (var claim in rows.Select(row => row.ToModel()))
            results.TryAdd(claim.Id, (claim, 60, "lexical"));

        if (queryEmbedding is not null)
        {
            var vectorRows = await connection.QueryAsync<MemoryClaimRow>(
                ClaimSelectSql + "\n" + """
                WHERE memory_claim.username = @username
                  AND (@includeSuperseded OR memory_claim.status <> 'superseded')
                  AND memory_claim.embedding_json IS NOT NULL;
                """,
                new { username = DefaultUsername, includeSuperseded });

            foreach (var row in vectorRows)
            {
                var score = CosineSimilarity(queryEmbedding, DeserializeEmbedding(row.EmbeddingJson));
                if (score > 0)
                {
                    var claim = row.ToModel();
                    if (!results.TryGetValue(claim.Id, out var existing) || score + 40 > existing.Score)
                        results[claim.Id] = (claim, score + 40, "vector");
                }
            }
        }

        return results.Values
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.Claim.RecordedAt)
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
        var result = new MemorySubgraphResult
        {
            Query = query,
            Meta = new MemorySubgraphMeta { MaxHopsUsed = Math.Clamp(maxHops, 1, 5) },
        };

        var seedEntityIds = query.SeedEntityIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var seedClaimIds = query.SeedClaimIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (seedEntityIds.Count == 0 && seedClaimIds.Count == 0)
            return result;

        await using var connection = CreateConnection();
        var entityIds = seedEntityIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var seedClaimId in seedClaimIds)
        {
            var claim = await GetClaimAsync(connection, seedClaimId);
            if (claim is null)
                continue;

            entityIds.Add(claim.SubjectEntityId);
            if (!string.IsNullOrWhiteSpace(claim.ObjectEntityId))
                entityIds.Add(claim.ObjectEntityId);
        }

        var entities = new List<MemoryEntity>();
        foreach (var entityId in entityIds.Take(maxReturnedEntities))
        {
            var entity = await GetEntityAsync(connection, entityId);
            if (entity is not null)
                entities.Add(entity);
        }

        result.Entities = entities.Select(entity => new MemorySubgraphEntity
        {
            Entity = entity,
            Score = seedEntityIds.Contains(entity.Id) ? 100 : 50,
            HopDistance = seedEntityIds.Contains(entity.Id) ? 0 : 1,
            IsDirectSeed = seedEntityIds.Contains(entity.Id),
        }).ToList();

        var entitySet = entities.Select(entity => entity.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claims = new List<MemoryClaim>();
        foreach (var entityId in entitySet)
        {
            var entityClaims = await connection.QueryAsync<MemoryClaimRow>(
                ClaimSelectSql + "\n" + """
                WHERE memory_claim.username = @username
                  AND (@includeSuperseded OR memory_claim.status <> 'superseded')
                  AND (@includeConflicts OR memory_claim.status <> 'conflicted')
                  AND (subject.external_id = @entityId OR object_entity.external_id = @entityId)
                ORDER BY memory_claim.recorded_at DESC
                LIMIT @limit;
                """,
                new
                {
                    username = DefaultUsername,
                    includeSuperseded,
                    includeConflicts,
                    entityId,
                    limit = maxReturnedClaims,
                });
            claims.AddRange(entityClaims.Select(row => row.ToModel()));
        }

        foreach (var seedClaimId in seedClaimIds)
        {
            var seedClaim = await GetClaimAsync(connection, seedClaimId);
            if (seedClaim is not null)
                claims.Add(seedClaim);
        }

        claims = claims
            .GroupBy(claim => claim.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(maxReturnedClaims)
            .ToList();

        result.Claims = claims.Select(claim => new MemorySubgraphClaim
        {
            Claim = claim,
            Score = seedClaimIds.Contains(claim.Id) ? 100 : 50,
            HopDistance = seedClaimIds.Contains(claim.Id) ? 0 : 1,
            IsDirectSeed = seedClaimIds.Contains(claim.Id),
        }).ToList();

        var rowIds = new List<long>();
        foreach (var entityId in entitySet)
        {
            var rowId = await GetEntityRowIdAsync(connection, entityId);
            if (rowId.HasValue)
                rowIds.Add(rowId.Value);
        }
        result.EntityEdges = (await QueryEntityEdgesAsync(connection, rowIds, rowIds)).Select(ToEntityEdge).ToList();

        var claimIds = claims.Select(claim => claim.Id).ToList();
        result.ClaimEdges = (await connection.QueryAsync<MemoryClaimEdgeRow>(
            """
            SELECT source.external_id AS FromClaimId, target.external_id AS ToClaimId,
                   edge.edge_type AS EdgeType, edge.weight AS Weight,
                   edge.source AS Source, edge.created_at AS CreatedAt
            FROM memory_claim_edges edge
            JOIN memory_claims source ON source.id = edge.from_claim_id
            JOIN memory_claims target ON target.id = edge.to_claim_id
            WHERE edge.username = @username
              AND source.external_id IN @claimIds
              AND target.external_id IN @claimIds;
            """,
            new { username = DefaultUsername, claimIds }))
            .Select(row => row.ToModel())
            .ToList();

        result.Observations = await GetUnresolvedObservationsAsync(entitySet, claimIds);
        result.Meta.FrontierExpanded = result.Entities.Count + result.Claims.Count;
        result.Meta.ResponseTruncated = result.Entities.Count >= maxReturnedEntities || result.Claims.Count >= maxReturnedClaims;
        return result;
    }

    private MySqlConnection CreateConnection() => new(options.ConnectionString);

    private async Task<MemoryEntity?> GetEntityAsync(MySqlConnection connection, string entityId)
    {
        var row = await connection.QuerySingleOrDefaultAsync<MemoryEntityRow>(
            """
            SELECT id AS RowId, external_id AS Id, canonical_id AS ExternalId, label, type,
                   canonical_name AS CanonicalName, summary, 'memory' AS Source,
                   embedding_json AS EmbeddingJson, created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM memory_entities_v2
            WHERE username = @username AND external_id = @entityId
            LIMIT 1;
            """,
            new { username = DefaultUsername, entityId });
        return row?.ToModel();
    }

    private async Task<MemoryClaim?> GetClaimAsync(MySqlConnection connection, string claimId)
    {
        var row = await connection.QuerySingleOrDefaultAsync<MemoryClaimRow>(
            ClaimSelectSql + "\n" + """
            WHERE memory_claim.username = @username
              AND (memory_claim.external_id = @claimId OR memory_claim.claim_key = @claimId)
            LIMIT 1;
            """,
            new { username = DefaultUsername, claimId });
        return row?.ToModel();
    }

    private async Task<long?> GetEntityRowIdAsync(MySqlConnection connection, string? externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return null;

        return await connection.QuerySingleOrDefaultAsync<long?>(
            "SELECT id FROM memory_entities_v2 WHERE username = @username AND external_id = @externalId LIMIT 1;",
            new { username = DefaultUsername, externalId });
    }

    private async Task<long?> GetClaimRowIdAsync(MySqlConnection connection, string? externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return null;

        return await connection.QuerySingleOrDefaultAsync<long?>(
            """
            SELECT id
            FROM memory_claims
            WHERE username = @username AND (external_id = @externalId OR claim_key = @externalId)
            LIMIT 1;
            """,
            new { username = DefaultUsername, externalId });
    }

    private async Task<long?> GetObservationRowIdAsync(MySqlConnection connection, string? externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return null;

        return await connection.QuerySingleOrDefaultAsync<long?>(
            "SELECT id FROM memory_observations_v2 WHERE username = @username AND external_id = @externalId LIMIT 1;",
            new { username = DefaultUsername, externalId });
    }

    private async Task<List<MemoryEntityEdgeRow>> QueryEntityEdgesAsync(
        MySqlConnection connection,
        IReadOnlyList<long> sourceOrTargetRowIds,
        IReadOnlyList<long>? constrainedRowIds)
    {
        if (sourceOrTargetRowIds.Count == 0)
            return [];

        var sql = """
            SELECT source.external_id AS FromEntityId, target.external_id AS ToEntityId,
                   edge.edge_type AS EdgeType, claim.external_id AS BestActiveClaimId,
                   edge.weight AS Weight, edge.created_at AS CreatedAt, edge.updated_at AS UpdatedAt
            FROM memory_entity_edges edge
            JOIN memory_entities_v2 source ON source.id = edge.from_entity_id
            JOIN memory_entities_v2 target ON target.id = edge.to_entity_id
            LEFT JOIN memory_claims claim ON claim.id = edge.best_active_claim_id
            WHERE edge.username = @username
              AND (edge.from_entity_id IN @sourceOrTargetRowIds OR edge.to_entity_id IN @sourceOrTargetRowIds)
            """;

        if (constrainedRowIds is not null)
        {
            sql += """
               AND edge.from_entity_id IN @constrainedRowIds
               AND edge.to_entity_id IN @constrainedRowIds
            """;
        }

        sql += " ORDER BY edge.updated_at DESC;";

        return (await connection.QueryAsync<MemoryEntityEdgeRow>(
            sql,
            new { username = DefaultUsername, sourceOrTargetRowIds, constrainedRowIds }))
            .ToList();
    }

    private async Task<List<MemoryRelationshipDetail>> GetRelationshipsForAllEntitiesAsync()
    {
        await using var connection = CreateConnection();
        var rows = await connection.QueryAsync<MemoryRelationshipRow>(
            """
            SELECT 'outgoing' AS Direction,
                   source.external_id AS SourceId,
                   edge.edge_type AS RelationshipType,
                   target.label AS TargetLabel,
                   target.external_id AS TargetId,
                   claim.external_id AS Context,
                   edge.updated_at AS Timestamp
            FROM memory_entity_edges edge
            JOIN memory_entities_v2 source ON source.id = edge.from_entity_id
            JOIN memory_entities_v2 target ON target.id = edge.to_entity_id
            LEFT JOIN memory_claims claim ON claim.id = edge.best_active_claim_id
            WHERE edge.username = @username
            ORDER BY edge.updated_at DESC;
            """,
            new { username = DefaultUsername });

        return rows.Select(row => new MemoryRelationshipDetail
        {
            Direction = row.Direction,
            RelationshipType = row.RelationshipType,
            TargetLabel = row.TargetLabel,
            TargetId = row.TargetId,
            Context = row.Context,
            Timestamp = row.Timestamp,
            SourceId = row.SourceId,
        }).ToList();
    }

    private static object ToReceiptParameters(MemoryWriteReceipt receipt) => new
    {
        receipt.Id,
        Username = DefaultUsername,
        receipt.Source,
        receipt.InputMode,
        Status = ToStatus(receipt.Status),
        receipt.EntitiesRequested,
        receipt.ClaimsRequested,
        receipt.EvidenceRequested,
        receipt.NodesWritten,
        receipt.EdgesWritten,
        receipt.ConflictsDetected,
        receipt.ClaimsWritten,
        receipt.EvidenceWritten,
        receipt.ObservationsWritten,
        receipt.AttemptCount,
        receipt.ErrorMessage,
        receipt.CreatedAt,
        receipt.UpdatedAt,
    };

    private static string? SerializeEmbedding(float[]? embedding) =>
        embedding is null ? null : JsonSerializer.Serialize(embedding);

    private static float[]? DeserializeEmbedding(string? embeddingJson)
    {
        if (string.IsNullOrWhiteSpace(embeddingJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<float[]>(embeddingJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double CosineSimilarity(float[] query, float[]? candidate)
    {
        if (candidate is null || query.Length == 0 || candidate.Length == 0 || query.Length != candidate.Length)
            return 0;

        double dot = 0;
        double queryMagnitude = 0;
        double candidateMagnitude = 0;
        for (var i = 0; i < query.Length; i++)
        {
            dot += query[i] * candidate[i];
            queryMagnitude += query[i] * query[i];
            candidateMagnitude += candidate[i] * candidate[i];
        }

        return queryMagnitude == 0 || candidateMagnitude == 0
            ? 0
            : dot / (Math.Sqrt(queryMagnitude) * Math.Sqrt(candidateMagnitude));
    }

    private static string ToStatus(MemoryClaimStatus status) =>
        status.ToString().ToLowerInvariant();

    private static MemoryClaimStatus ParseClaimStatus(string? status) =>
        Enum.TryParse<MemoryClaimStatus>(status, true, out var parsed) ? parsed : MemoryClaimStatus.Active;

    private static string ToStatus(MemoryWriteReceiptStatus status) =>
        status.ToString().ToLowerInvariant();

    private static MemoryWriteReceiptStatus ParseWriteReceiptStatus(string? status) =>
        Enum.TryParse<MemoryWriteReceiptStatus>(status, true, out var parsed) ? parsed : MemoryWriteReceiptStatus.Queued;

    private static string NormalizeLookupKey(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var normalized = new string(input.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray());

        while (normalized.Contains("__", StringComparison.Ordinal))
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);

        return normalized.Trim('_');
    }

    private static MemoryEntityEdge ToEntityEdge(MemoryEntityEdgeRow row) => new()
    {
        FromEntityId = row.FromEntityId,
        ToEntityId = row.ToEntityId,
        EdgeType = row.EdgeType,
        BestActiveClaimId = row.BestActiveClaimId,
        Weight = row.Weight,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };

    private static MemoryGraphLink ToGraphLink(MemoryEntityEdgeRow row) => new()
    {
        Source = row.FromEntityId,
        Target = row.ToEntityId,
        Relationship = row.EdgeType,
        Context = row.BestActiveClaimId,
        Timestamp = row.UpdatedAt,
    };

    private const string ClaimSelectSql = """
        SELECT memory_claim.id AS RowId, memory_claim.external_id AS Id, memory_claim.claim_key AS ClaimKey,
               memory_claim.fact_group_key AS FactGroupKey, subject.external_id AS SubjectEntityId,
               memory_claim.predicate AS Predicate, object_entity.external_id AS ObjectEntityId,
               memory_claim.value_text AS ValueText, memory_claim.value_json AS ValueJson,
               memory_claim.normalized_text AS NormalizedText, memory_claim.status AS Status,
               memory_claim.confidence AS Confidence, memory_claim.effective_at AS EffectiveAt,
               memory_claim.recorded_at AS RecordedAt, superseded.external_id AS SupersedesClaimId,
               memory_claim.source AS Source, memory_claim.embedding_json AS EmbeddingJson
        FROM memory_claims memory_claim
        JOIN memory_entities_v2 subject ON subject.id = memory_claim.subject_entity_id
        LEFT JOIN memory_entities_v2 object_entity ON object_entity.id = memory_claim.object_entity_id
        LEFT JOIN memory_claims superseded ON superseded.id = memory_claim.supersedes_claim_id
        """;

    private sealed class MemoryEntityRow
    {
        public long RowId { get; set; }
        public string Id { get; set; } = "";
        public string? ExternalId { get; set; }
        public string Label { get; set; } = "";
        public string Type { get; set; } = "";
        public string? CanonicalName { get; set; }
        public string? Summary { get; set; }
        public string Source { get; set; } = "memory";
        public string? EmbeddingJson { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public MemoryEntity ToModel() => new()
        {
            Id = Id,
            Label = Label,
            Type = Type,
            ExternalId = ExternalId,
            CanonicalName = CanonicalName,
            Summary = Summary ?? "",
            Source = Source,
            Embedding = DeserializeEmbedding(EmbeddingJson),
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }

    private sealed class MemoryClaimRow
    {
        public string Id { get; set; } = "";
        public string ClaimKey { get; set; } = "";
        public string FactGroupKey { get; set; } = "";
        public string SubjectEntityId { get; set; } = "";
        public string Predicate { get; set; } = "";
        public string? ObjectEntityId { get; set; }
        public string? ValueText { get; set; }
        public string? ValueJson { get; set; }
        public string NormalizedText { get; set; } = "";
        public string Status { get; set; } = "active";
        public decimal? Confidence { get; set; }
        public DateTime? EffectiveAt { get; set; }
        public DateTime RecordedAt { get; set; }
        public string? SupersedesClaimId { get; set; }
        public string Source { get; set; } = "";
        public string? EmbeddingJson { get; set; }

        public MemoryClaim ToModel() => new()
        {
            Id = Id,
            ClaimKey = ClaimKey,
            FactGroupKey = FactGroupKey,
            SubjectEntityId = SubjectEntityId,
            Predicate = Predicate,
            ObjectEntityId = ObjectEntityId,
            ValueText = ValueText,
            ValueJson = ValueJson,
            NormalizedText = NormalizedText,
            Status = ParseClaimStatus(Status),
            Confidence = Confidence,
            EffectiveAt = EffectiveAt,
            RecordedAt = RecordedAt,
            SupersedesClaimId = SupersedesClaimId,
            Source = Source,
            Embedding = DeserializeEmbedding(EmbeddingJson),
        };
    }

    private sealed class MemoryEntityEdgeRow
    {
        public string FromEntityId { get; set; } = "";
        public string ToEntityId { get; set; } = "";
        public string EdgeType { get; set; } = "";
        public string? BestActiveClaimId { get; set; }
        public decimal? Weight { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class MemoryClaimEdgeRow
    {
        public string FromClaimId { get; set; } = "";
        public string ToClaimId { get; set; } = "";
        public string EdgeType { get; set; } = "";
        public decimal? Weight { get; set; }
        public string Source { get; set; } = "";
        public DateTime CreatedAt { get; set; }

        public MemoryClaimEdge ToModel() => new()
        {
            FromClaimId = FromClaimId,
            ToClaimId = ToClaimId,
            EdgeType = EdgeType,
            Weight = Weight,
            Source = Source,
            CreatedAt = CreatedAt,
        };
    }

    private sealed class MemoryRelationshipRow
    {
        public string Direction { get; set; } = "";
        public string? SourceId { get; set; }
        public string RelationshipType { get; set; } = "";
        public string TargetLabel { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string? Context { get; set; }
        public DateTime Timestamp { get; set; }
    }

    private sealed class MemoryObservationRow
    {
        public string Id { get; set; } = "";
        public string Claim { get; set; } = "";
        public string ConflictsWith { get; set; } = "";
        public string Source { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public bool Resolved { get; set; }
        public string? Resolution { get; set; }
        public string? ResolvedByMemoryId { get; set; }
        public string? EntityId { get; set; }
        public string? ClaimId { get; set; }
        public string? RelatedClaimId { get; set; }

        public MemoryObservation ToModel() => new()
        {
            Id = Id,
            Claim = Claim,
            ConflictsWith = ConflictsWith,
            Source = Source,
            Timestamp = Timestamp,
            Resolved = Resolved,
            Resolution = Resolution,
            ResolvedByMemoryId = ResolvedByMemoryId,
            AboutEntityIds = string.IsNullOrWhiteSpace(EntityId) ? [] : [EntityId],
            AboutClaimIds = new[] { ClaimId, RelatedClaimId }
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToList(),
        };
    }

    private sealed class MemoryEvidenceRow
    {
        public string Id { get; set; } = "";
        public string? ClaimId { get; set; }
        public string? ObservationId { get; set; }
        public string EvidenceType { get; set; } = "";
        public string SourceRef { get; set; } = "";
        public string? Snippet { get; set; }
        public string? MetadataJson { get; set; }
        public DateTime CreatedAt { get; set; }

        public MemoryEvidence ToModel() => new()
        {
            Id = Id,
            ClaimId = ClaimId,
            ObservationId = ObservationId,
            EvidenceType = EvidenceType,
            SourceRef = SourceRef,
            Snippet = Snippet,
            MetadataJson = MetadataJson,
            CreatedAt = CreatedAt,
        };
    }

    private sealed class MemoryWriteReceiptRow
    {
        public string Id { get; set; } = "";
        public string Source { get; set; } = "api";
        public string InputMode { get; set; } = "typed";
        public string Status { get; set; } = "queued";
        public int EntitiesRequested { get; set; }
        public int ClaimsRequested { get; set; }
        public int EvidenceRequested { get; set; }
        public int AttemptCount { get; set; }
        public int NodesWritten { get; set; }
        public int EdgesWritten { get; set; }
        public int ConflictsDetected { get; set; }
        public int ClaimsWritten { get; set; }
        public int EvidenceWritten { get; set; }
        public int ObservationsWritten { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        public MemoryWriteReceipt ToModel() => new()
        {
            Id = Id,
            Source = Source,
            InputMode = InputMode,
            Status = ParseWriteReceiptStatus(Status),
            EntitiesRequested = EntitiesRequested,
            ClaimsRequested = ClaimsRequested,
            EvidenceRequested = EvidenceRequested,
            AttemptCount = AttemptCount,
            NodesWritten = NodesWritten,
            EdgesWritten = EdgesWritten,
            ConflictsDetected = ConflictsDetected,
            ClaimsWritten = ClaimsWritten,
            EvidenceWritten = EvidenceWritten,
            ObservationsWritten = ObservationsWritten,
            ErrorMessage = ErrorMessage,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            StartedAt = StartedAt,
            CompletedAt = CompletedAt,
        };
    }
}
