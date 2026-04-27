-- Migration 030: persist the LLM provider used for each indexing batch

ALTER TABLE analysis_batches
    ADD COLUMN provider_name VARCHAR(50) NOT NULL DEFAULT 'anthropic' AFTER anthropic_batch_id;

CREATE INDEX idx_ab_provider_name ON analysis_batches(provider_name);
