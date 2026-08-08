ALTER TABLE indexer_runs
    ADD COLUMN IF NOT EXISTS execution_owner VARCHAR(191) NULL,
    ADD COLUMN IF NOT EXISTS lease_expires_at DATETIME(6) NULL,
    ADD COLUMN IF NOT EXISTS heartbeat_at DATETIME(6) NULL,
    ADD COLUMN IF NOT EXISTS cancel_requested_at DATETIME(6) NULL,
    ADD COLUMN IF NOT EXISTS next_attempt_at DATETIME(6) NULL,
    ADD COLUMN IF NOT EXISTS attempt_count INT NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS fencing_token BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS retry_safe BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS submission_key VARCHAR(191) NULL,
    ADD COLUMN IF NOT EXISTS submission_hash CHAR(64) NULL,
    ADD UNIQUE INDEX IF NOT EXISTS ux_indexer_runs_submitter_key (requested_by_username, submission_key),
    ADD INDEX IF NOT EXISTS ix_indexer_runs_claim (status, next_attempt_at, lease_expires_at, created_at);

UPDATE indexer_runs
SET retry_safe = TRUE
WHERE operation IN ('sync_schema', 'sync_all_schemas', 'link', 'detect_communities', 'link_and_detect');

-- A deployment can interrupt rows created by the former process-local Task.Run
-- executor. Those rows have no lease, so classify them explicitly instead of
-- leaving them stranded forever or blindly replaying unknown side effects.
UPDATE indexer_runs
SET status = 'queued',
    message = 'Recovered a pre-lease safe operation after deployment restart.',
    execution_owner = NULL,
    lease_expires_at = NULL,
    heartbeat_at = NULL,
    next_attempt_at = CURRENT_TIMESTAMP(6),
    completed_at = NULL
WHERE status = 'running'
  AND retry_safe = TRUE
  AND lease_expires_at IS NULL;

UPDATE indexer_runs
SET status = 'failed',
    message = 'A pre-lease operation was interrupted; it was not replayed because its side effects are not retry-safe.',
    error = 'Execution state was ambiguous during the durable-worker migration.',
    execution_owner = NULL,
    lease_expires_at = NULL,
    heartbeat_at = CURRENT_TIMESTAMP(6),
    next_attempt_at = NULL,
    completed_at = CURRENT_TIMESTAMP(6)
WHERE status = 'running'
  AND retry_safe = FALSE
  AND lease_expires_at IS NULL;
