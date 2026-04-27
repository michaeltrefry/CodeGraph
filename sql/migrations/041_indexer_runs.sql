CREATE TABLE IF NOT EXISTS indexer_runs (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    operation VARCHAR(100) NOT NULL,
    requested_by_username VARCHAR(255) NULL,
    target VARCHAR(512) NULL,
    status VARCHAR(32) NOT NULL,
    message TEXT NULL,
    error TEXT NULL,
    created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    started_at DATETIME(3) NULL,
    completed_at DATETIME(3) NULL,
    INDEX ix_indexer_runs_status_created (status, created_at),
    INDEX ix_indexer_runs_requested_by_created (requested_by_username, created_at),
    INDEX ix_indexer_runs_operation_created (operation, created_at)
);
