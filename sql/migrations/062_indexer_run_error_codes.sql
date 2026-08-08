ALTER TABLE indexer_runs
    ADD COLUMN IF NOT EXISTS error_code VARCHAR(100) NULL AFTER message;
