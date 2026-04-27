CREATE TABLE IF NOT EXISTS memory_entities (
    username VARCHAR(255) NOT NULL,
    id VARCHAR(255) NOT NULL,
    label VARCHAR(255) NOT NULL,
    type VARCHAR(100) NOT NULL,
    summary TEXT NOT NULL,
    source VARCHAR(255) NOT NULL,
    embedding_json LONGTEXT NULL,
    created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    updated_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
    PRIMARY KEY (username, id),
    KEY ix_memory_entities_username_updated_at (username, updated_at),
    KEY ix_memory_entities_username_label (username, label),
    FULLTEXT KEY ft_memory_entities_lookup (id, label, summary)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS memory_relationships (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(255) NOT NULL,
    from_id VARCHAR(255) NOT NULL,
    to_id VARCHAR(255) NOT NULL,
    relationship_type VARCHAR(255) NOT NULL,
    context TEXT NULL,
    source VARCHAR(255) NOT NULL,
    timestamp DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    supersedes VARCHAR(255) NULL,
    embedding_json LONGTEXT NULL,
    KEY ix_memory_relationships_from (username, from_id, timestamp),
    KEY ix_memory_relationships_to (username, to_id, timestamp),
    KEY ix_memory_relationships_pair (username, from_id, to_id),
    CONSTRAINT fk_memory_relationships_from
        FOREIGN KEY (username, from_id) REFERENCES memory_entities(username, id)
        ON DELETE CASCADE,
    CONSTRAINT fk_memory_relationships_to
        FOREIGN KEY (username, to_id) REFERENCES memory_entities(username, id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS memory_observations (
    id VARCHAR(255) PRIMARY KEY,
    username VARCHAR(255) NOT NULL,
    claim TEXT NOT NULL,
    conflicts_with TEXT NOT NULL,
    source VARCHAR(255) NOT NULL,
    timestamp DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    resolved BOOLEAN NOT NULL DEFAULT FALSE,
    resolution TEXT NULL,
    resolved_by_memory_id VARCHAR(255) NULL,
    KEY ix_memory_observations_lookup (username, resolved, timestamp)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS memory_observation_entities (
    observation_id VARCHAR(255) NOT NULL,
    username VARCHAR(255) NOT NULL,
    entity_id VARCHAR(255) NOT NULL,
    PRIMARY KEY (observation_id, entity_id),
    KEY ix_memory_observation_entities_entity (username, entity_id),
    CONSTRAINT fk_memory_observation_entities_observation
        FOREIGN KEY (observation_id) REFERENCES memory_observations(id)
        ON DELETE CASCADE,
    CONSTRAINT fk_memory_observation_entities_entity
        FOREIGN KEY (username, entity_id) REFERENCES memory_entities(username, id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
