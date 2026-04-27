ALTER TABLE memory_entities_v2
    ADD COLUMN canonical_id VARCHAR(255) NULL AFTER external_id;

UPDATE memory_entities_v2
SET canonical_id = COALESCE(
    NULLIF(LOWER(REGEXP_REPLACE(external_id, '[^a-zA-Z0-9]', '')), ''),
    external_id)
WHERE canonical_id IS NULL
   OR canonical_id = '';

CREATE TEMPORARY TABLE tmp_memory_entity_winners AS
WITH ranked_entities AS (
    SELECT
        id,
        username,
        canonical_id,
        ROW_NUMBER() OVER (
            PARTITION BY username, canonical_id
            ORDER BY
                (
                    CASE WHEN LOWER(type) <> 'unknown' AND type <> '' THEN 100 ELSE 0 END +
                    CASE WHEN summary IS NOT NULL AND summary <> '' THEN 40 ELSE 0 END +
                    CASE WHEN canonical_name IS NOT NULL AND canonical_name <> '' AND canonical_name <> external_id THEN 20 ELSE 0 END +
                    CASE WHEN label <> external_id THEN 20 ELSE 0 END +
                    CASE WHEN embedding_json IS NOT NULL AND embedding_json <> '' THEN 10 ELSE 0 END
                ) DESC,
                updated_at DESC,
                created_at DESC,
                id ASC
        ) AS winner_rank
    FROM memory_entities_v2
)
SELECT
    username,
    canonical_id,
    id AS winner_id
FROM ranked_entities
WHERE winner_rank = 1;

CREATE TEMPORARY TABLE tmp_memory_entity_dupes AS
SELECT
    loser.id AS loser_id,
    winner.winner_id,
    loser.username,
    loser.external_id,
    loser.canonical_id
FROM memory_entities_v2 loser
JOIN tmp_memory_entity_winners winner
    ON winner.username = loser.username
   AND winner.canonical_id = loser.canonical_id
WHERE loser.id <> winner.winner_id;

UPDATE memory_claims claim
JOIN tmp_memory_entity_dupes dupes
    ON dupes.loser_id = claim.subject_entity_id
SET claim.subject_entity_id = dupes.winner_id;

UPDATE memory_claims claim
JOIN tmp_memory_entity_dupes dupes
    ON dupes.loser_id = claim.object_entity_id
SET claim.object_entity_id = dupes.winner_id;

UPDATE memory_active_claims claim
JOIN tmp_memory_entity_dupes dupes
    ON dupes.loser_id = claim.subject_entity_id
SET claim.subject_entity_id = dupes.winner_id;

UPDATE memory_active_claims claim
JOIN tmp_memory_entity_dupes dupes
    ON dupes.loser_id = claim.object_entity_id
SET claim.object_entity_id = dupes.winner_id;

UPDATE memory_observations_v2 observation
JOIN tmp_memory_entity_dupes dupes
    ON dupes.loser_id = observation.entity_id
SET observation.entity_id = dupes.winner_id;

UPDATE memory_entity_edges edge_row
JOIN tmp_memory_entity_dupes dupes
    ON dupes.loser_id = edge_row.from_entity_id
SET edge_row.from_entity_id = dupes.winner_id;

UPDATE memory_entity_edges edge_row
JOIN tmp_memory_entity_dupes dupes
    ON dupes.loser_id = edge_row.to_entity_id
SET edge_row.to_entity_id = dupes.winner_id;

UPDATE memory_entity_adjacency edge_row
JOIN tmp_memory_entity_dupes dupes
    ON dupes.loser_id = edge_row.from_entity_id
SET edge_row.from_entity_id = dupes.winner_id;

UPDATE memory_entity_adjacency edge_row
JOIN tmp_memory_entity_dupes dupes
    ON dupes.loser_id = edge_row.to_entity_id
SET edge_row.to_entity_id = dupes.winner_id;

UPDATE memory_seed_aliases alias_row
JOIN tmp_memory_entity_dupes dupes
    ON dupes.loser_id = alias_row.entity_id
SET alias_row.entity_id = dupes.winner_id;

INSERT INTO memory_seed_aliases (username, entity_id, alias_kind, alias_text, normalized_alias, updated_at)
SELECT
    dupes.username,
    dupes.winner_id,
    'external_id',
    dupes.external_id,
    dupes.external_id,
    UTC_TIMESTAMP(3)
FROM tmp_memory_entity_dupes dupes
LEFT JOIN memory_seed_aliases existing
    ON existing.username = dupes.username
   AND existing.entity_id = dupes.winner_id
   AND existing.alias_kind = 'external_id'
   AND existing.normalized_alias = dupes.external_id
WHERE existing.id IS NULL;

INSERT INTO memory_seed_aliases (username, entity_id, alias_kind, alias_text, normalized_alias, updated_at)
SELECT
    dupes.username,
    dupes.winner_id,
    'compact_external_id',
    dupes.external_id,
    dupes.canonical_id,
    UTC_TIMESTAMP(3)
FROM tmp_memory_entity_dupes dupes
LEFT JOIN memory_seed_aliases existing
    ON existing.username = dupes.username
   AND existing.entity_id = dupes.winner_id
   AND existing.alias_kind = 'compact_external_id'
   AND existing.normalized_alias = dupes.canonical_id
WHERE existing.id IS NULL;

DELETE duplicate_alias
FROM memory_seed_aliases duplicate_alias
JOIN memory_seed_aliases canonical_alias
    ON canonical_alias.username = duplicate_alias.username
   AND canonical_alias.entity_id = duplicate_alias.entity_id
   AND canonical_alias.alias_kind = duplicate_alias.alias_kind
   AND canonical_alias.normalized_alias = duplicate_alias.normalized_alias
   AND canonical_alias.id < duplicate_alias.id;

DELETE duplicate_edge
FROM memory_entity_edges duplicate_edge
JOIN memory_entity_edges canonical_edge
    ON canonical_edge.username = duplicate_edge.username
   AND canonical_edge.from_entity_id = duplicate_edge.from_entity_id
   AND canonical_edge.to_entity_id = duplicate_edge.to_entity_id
   AND canonical_edge.edge_type = duplicate_edge.edge_type
   AND (
       canonical_edge.best_active_claim_id <=> duplicate_edge.best_active_claim_id
   )
   AND canonical_edge.id < duplicate_edge.id;

DELETE duplicate_adjacency
FROM memory_entity_adjacency duplicate_adjacency
JOIN memory_entity_adjacency canonical_adjacency
    ON canonical_adjacency.username = duplicate_adjacency.username
   AND canonical_adjacency.from_entity_id = duplicate_adjacency.from_entity_id
   AND canonical_adjacency.to_entity_id = duplicate_adjacency.to_entity_id
   AND canonical_adjacency.edge_type = duplicate_adjacency.edge_type
   AND (
       canonical_adjacency.best_active_claim_id <=> duplicate_adjacency.best_active_claim_id
   )
   AND canonical_adjacency.id < duplicate_adjacency.id;

DELETE entity_row
FROM memory_entities_v2 entity_row
JOIN tmp_memory_entity_dupes dupes
    ON dupes.loser_id = entity_row.id;

DROP TEMPORARY TABLE tmp_memory_entity_dupes;

DROP TEMPORARY TABLE tmp_memory_entity_winners;

ALTER TABLE memory_entities_v2
    MODIFY canonical_id VARCHAR(255) NOT NULL;

ALTER TABLE memory_entities_v2
    ADD UNIQUE KEY uq_memory_entities_v2_username_canonical_id (username, canonical_id);
