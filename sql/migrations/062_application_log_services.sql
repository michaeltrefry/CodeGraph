ALTER TABLE application_logs
    ADD COLUMN IF NOT EXISTS service VARCHAR(32) NOT NULL DEFAULT 'api' AFTER level;

CREATE INDEX IF NOT EXISTS ix_application_logs_service_time
    ON application_logs (service, occurred_at_utc);
