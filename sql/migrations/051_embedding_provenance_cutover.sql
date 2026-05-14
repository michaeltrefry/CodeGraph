ALTER TABLE embeddings
    ADD COLUMN IF NOT EXISTS model_name VARCHAR(100) NOT NULL DEFAULT 'nomic-embed-text-v1.5' AFTER embedding_json,
    ADD COLUMN IF NOT EXISTS dimensions INT NOT NULL DEFAULT 768 AFTER model_name;

CREATE INDEX IF NOT EXISTS idx_embeddings_model ON embeddings (model_name, dimensions);

TRUNCATE TABLE embeddings;
