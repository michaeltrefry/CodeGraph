CREATE TABLE IF NOT EXISTS memory_observations_v2 (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(255) NOT NULL,
    observation_type VARCHAR(100) NOT NULL,
    claim_id BIGINT NULL,
    related_claim_id BIGINT NULL,
    entity_id BIGINT NULL,
    message TEXT NOT NULL,
    resolution_status VARCHAR(32) NOT NULL,
    resolved_by_claim_id BIGINT NULL,
    created_at DATETIME(3) NOT NULL,
    resolved_at DATETIME(3) NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS memory_evidence (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(255) NOT NULL,
    claim_id BIGINT NULL,
    observation_id BIGINT NULL,
    evidence_type VARCHAR(100) NOT NULL,
    source_ref VARCHAR(500) NOT NULL,
    snippet TEXT NULL,
    metadata_json LONGTEXT NULL,
    created_at DATETIME(3) NOT NULL
) ENGINE=InnoDB;
