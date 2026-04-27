CREATE TABLE IF NOT EXISTS embeddings (
    entity_type VARCHAR(100) NOT NULL,
    entity_key VARCHAR(500) NOT NULL,
    embedding_json LONGTEXT NOT NULL,
    updated_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
    PRIMARY KEY (entity_type, entity_key),
    INDEX idx_embeddings_entity_type (entity_type)
) ENGINE=InnoDB;
