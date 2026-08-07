ALTER TABLE assistant_runs
    ADD COLUMN IF NOT EXISTS execution_owner VARCHAR(255) NULL AFTER request_hash,
    ADD COLUMN IF NOT EXISTS lease_expires_at DATETIME(3) NULL AFTER execution_owner,
    ADD COLUMN IF NOT EXISTS cancel_requested_at DATETIME(3) NULL AFTER lease_expires_at,
    ADD INDEX IF NOT EXISTS ix_assistant_runs_status_lease_expires_at (status, lease_expires_at),
    ADD INDEX IF NOT EXISTS ix_assistant_runs_execution_owner (execution_owner);
