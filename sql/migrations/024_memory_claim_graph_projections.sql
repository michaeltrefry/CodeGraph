CREATE TABLE IF NOT EXISTS memory_active_claims (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(255) NOT NULL,
    claim_id BIGINT NOT NULL,
    fact_group_key VARCHAR(255) NOT NULL,
    subject_entity_id BIGINT NOT NULL,
    predicate VARCHAR(255) NOT NULL,
    object_entity_id BIGINT NULL,
    status VARCHAR(32) NOT NULL,
    recorded_at DATETIME(3) NOT NULL,
    updated_at DATETIME(3) NOT NULL,
    UNIQUE KEY uq_memory_active_claims_username_claim_id (username, claim_id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS memory_seed_aliases (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(255) NOT NULL,
    entity_id BIGINT NOT NULL,
    alias_kind VARCHAR(50) NOT NULL,
    alias_text VARCHAR(255) NOT NULL,
    normalized_alias VARCHAR(255) NOT NULL,
    updated_at DATETIME(3) NOT NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS memory_entity_adjacency (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(255) NOT NULL,
    from_entity_id BIGINT NOT NULL,
    to_entity_id BIGINT NOT NULL,
    edge_type VARCHAR(100) NOT NULL,
    best_active_claim_id BIGINT NULL,
    updated_at DATETIME(3) NOT NULL
) ENGINE=InnoDB;

CREATE INDEX ix_memory_active_claims_username_fact_group_key
    ON memory_active_claims (username, fact_group_key);

CREATE INDEX ix_memory_seed_aliases_username_normalized_alias
    ON memory_seed_aliases (username, normalized_alias);

CREATE INDEX ix_memory_seed_aliases_username_entity_id
    ON memory_seed_aliases (username, entity_id);

CREATE INDEX ix_memory_entity_adjacency_username_from_entity_edge_type
    ON memory_entity_adjacency (username, from_entity_id, edge_type);

CREATE INDEX ix_memory_entity_adjacency_username_to_entity_edge_type
    ON memory_entity_adjacency (username, to_entity_id, edge_type);
