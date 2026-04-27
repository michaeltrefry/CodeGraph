CREATE TABLE IF NOT EXISTS memory_claims (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(255) NOT NULL,
    claim_key VARCHAR(255) NOT NULL,
    fact_group_key VARCHAR(255) NOT NULL,
    subject_entity_id BIGINT NOT NULL,
    predicate VARCHAR(255) NOT NULL,
    object_entity_id BIGINT NULL,
    value_text TEXT NULL,
    value_json LONGTEXT NULL,
    normalized_text TEXT NOT NULL,
    status VARCHAR(32) NOT NULL,
    confidence DECIMAL(5,4) NULL,
    effective_at DATETIME(3) NULL,
    recorded_at DATETIME(3) NOT NULL,
    supersedes_claim_id BIGINT NULL,
    source VARCHAR(255) NOT NULL,
    embedding_json LONGTEXT NULL,
    UNIQUE KEY uq_memory_claims_username_claim_key (username, claim_key)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS memory_claim_edges (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(255) NOT NULL,
    from_claim_id BIGINT NOT NULL,
    to_claim_id BIGINT NOT NULL,
    edge_type VARCHAR(100) NOT NULL,
    weight DECIMAL(6,4) NULL,
    source VARCHAR(255) NOT NULL,
    created_at DATETIME(3) NOT NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS memory_entity_edges (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(255) NOT NULL,
    from_entity_id BIGINT NOT NULL,
    to_entity_id BIGINT NOT NULL,
    edge_type VARCHAR(100) NOT NULL,
    best_active_claim_id BIGINT NULL,
    weight DECIMAL(6,4) NULL,
    created_at DATETIME(3) NOT NULL,
    updated_at DATETIME(3) NOT NULL
) ENGINE=InnoDB;
