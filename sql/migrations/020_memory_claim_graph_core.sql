CREATE TABLE IF NOT EXISTS memory_entities_v2 (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(255) NOT NULL,
    external_id VARCHAR(255) NOT NULL,
    label VARCHAR(255) NOT NULL,
    type VARCHAR(100) NOT NULL,
    canonical_name VARCHAR(255) NULL,
    summary TEXT NULL,
    embedding_json LONGTEXT NULL,
    created_at DATETIME(3) NOT NULL,
    updated_at DATETIME(3) NOT NULL,
    UNIQUE KEY uq_memory_entities_v2_username_external_id (username, external_id)
) ENGINE=InnoDB;
