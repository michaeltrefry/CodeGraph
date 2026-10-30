-- Queue presentation state is independent of execution targets and idempotency keys.
ALTER TABLE indexer_runs ADD COLUMN IF NOT EXISTS dismissed_at DATETIME(6) NULL;
ALTER TABLE indexer_runs ADD COLUMN IF NOT EXISTS failure_resolved_at DATETIME(6) NULL;
ALTER TABLE indexer_runs ADD COLUMN IF NOT EXISTS queue_work_identity VARCHAR(64) NULL;
CREATE INDEX IF NOT EXISTS ix_indexer_runs_queue_identity ON indexer_runs (operation, queue_work_identity, status, id);
ALTER TABLE analysis_batches ADD COLUMN IF NOT EXISTS error TEXT NULL;
ALTER TABLE analysis_batches ADD COLUMN IF NOT EXISTS dismissed_at DATETIME(6) NULL;
ALTER TABLE job_schedules ADD COLUMN IF NOT EXISTS failure_at_utc DATETIME(3) NULL;
ALTER TABLE job_schedules ADD COLUMN IF NOT EXISTS failure_error TEXT NULL;
ALTER TABLE job_schedules ADD COLUMN IF NOT EXISTS failure_dismissed_at DATETIME(3) NULL;
-- Start with no historical failures demanding attention. Statement journaling makes
-- each backfill run only once; every statement remains safe after interruption.
UPDATE indexer_runs SET dismissed_at = UTC_TIMESTAMP(6) WHERE status = 'failed' AND dismissed_at IS NULL;
UPDATE analysis_batches SET dismissed_at = UTC_TIMESTAMP(6) WHERE status = 'failed' AND dismissed_at IS NULL;
UPDATE job_schedules SET failure_at_utc = COALESCE(last_run_completed_utc, updated_at_utc),
    failure_error = last_error, failure_dismissed_at = UTC_TIMESTAMP(3)
WHERE last_run_status = 'failed' AND failure_at_utc IS NULL;
