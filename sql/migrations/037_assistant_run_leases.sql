ALTER TABLE assistant_runs
    ADD COLUMN execution_owner VARCHAR(255) NULL AFTER request_hash,
    ADD COLUMN lease_expires_at DATETIME(3) NULL AFTER execution_owner,
    ADD COLUMN cancel_requested_at DATETIME(3) NULL AFTER lease_expires_at,
    ADD INDEX ix_assistant_runs_status_lease_expires_at (status, lease_expires_at),
    ADD INDEX ix_assistant_runs_execution_owner (execution_owner);
