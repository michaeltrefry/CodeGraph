ALTER TABLE memory_claims
    ADD COLUMN IF NOT EXISTS external_id VARCHAR(255) NULL AFTER id;

UPDATE memory_claims
SET external_id = claim_key
WHERE external_id IS NULL OR external_id = '';

ALTER TABLE memory_claims
    MODIFY COLUMN external_id VARCHAR(255) NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_memory_claims_username_external_id
    ON memory_claims (username, external_id);

ALTER TABLE memory_evidence
    ADD COLUMN IF NOT EXISTS external_id VARCHAR(255) NULL AFTER id;

UPDATE memory_evidence
SET external_id = CONCAT('evidence_', id)
WHERE external_id IS NULL OR external_id = '';

ALTER TABLE memory_evidence
    MODIFY COLUMN external_id VARCHAR(255) NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_memory_evidence_username_external_id
    ON memory_evidence (username, external_id);

ALTER TABLE memory_observations_v2
    ADD COLUMN IF NOT EXISTS external_id VARCHAR(255) NULL AFTER id,
    ADD COLUMN IF NOT EXISTS legacy_claim_text TEXT NULL AFTER external_id,
    ADD COLUMN IF NOT EXISTS legacy_conflicts_with TEXT NULL AFTER legacy_claim_text,
    ADD COLUMN IF NOT EXISTS legacy_source VARCHAR(255) NULL AFTER legacy_conflicts_with,
    ADD COLUMN IF NOT EXISTS legacy_resolution TEXT NULL AFTER resolution_status,
    ADD COLUMN IF NOT EXISTS legacy_resolved_by_memory_id VARCHAR(255) NULL AFTER legacy_resolution;

UPDATE memory_observations_v2
SET external_id = CONCAT('obs_', id),
    legacy_claim_text = COALESCE(legacy_claim_text, message),
    legacy_conflicts_with = COALESCE(legacy_conflicts_with, ''),
    legacy_source = COALESCE(legacy_source, 'migration')
WHERE external_id IS NULL OR external_id = '';

ALTER TABLE memory_observations_v2
    MODIFY COLUMN external_id VARCHAR(255) NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_memory_observations_v2_username_external_id
    ON memory_observations_v2 (username, external_id);

ALTER TABLE memory_write_receipts
    ADD COLUMN IF NOT EXISTS input_mode VARCHAR(32) NOT NULL DEFAULT 'typed' AFTER source,
    ADD COLUMN IF NOT EXISTS evidence_requested INT NOT NULL DEFAULT 0 AFTER requested_edges;
