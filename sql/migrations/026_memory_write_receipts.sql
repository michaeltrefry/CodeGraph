CREATE TABLE IF NOT EXISTS memory_write_receipts (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    receipt_id VARCHAR(64) NOT NULL,
    username VARCHAR(255) NOT NULL,
    source VARCHAR(255) NOT NULL,
    status VARCHAR(32) NOT NULL,
    requested_nodes INT NOT NULL,
    requested_edges INT NOT NULL,
    submitted_at DATETIME(3) NOT NULL,
    processing_started_at DATETIME(3) NULL,
    completed_at DATETIME(3) NULL,
    entities_upserted INT NOT NULL DEFAULT 0,
    claims_inserted INT NOT NULL DEFAULT 0,
    claims_superseded INT NOT NULL DEFAULT 0,
    claims_conflicted INT NOT NULL DEFAULT 0,
    duplicate_claims_skipped INT NOT NULL DEFAULT 0,
    evidence_written INT NOT NULL DEFAULT 0,
    observations_written INT NOT NULL DEFAULT 0,
    retryable_error_count INT NOT NULL DEFAULT 0,
    legacy_nodes_written INT NOT NULL DEFAULT 0,
    legacy_edges_written INT NOT NULL DEFAULT 0,
    legacy_conflicts_detected INT NOT NULL DEFAULT 0,
    error TEXT NULL,
    updated_at DATETIME(3) NOT NULL,
    UNIQUE KEY uq_memory_write_receipts_receipt_id (receipt_id)
) ENGINE=InnoDB;

CREATE INDEX ix_memory_write_receipts_username_submitted_at
    ON memory_write_receipts (username, submitted_at);
