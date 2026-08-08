ALTER TABLE job_schedules
    ADD COLUMN IF NOT EXISTS schedule_revision BIGINT NOT NULL DEFAULT 0 AFTER next_run_utc;
