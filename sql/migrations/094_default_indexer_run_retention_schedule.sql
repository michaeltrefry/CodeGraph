-- Retention preserves unresolved failures and active work. An existing operator schedule wins.
INSERT INTO job_schedules (name, job_type, is_enabled, cron_expression, time_zone_id, args_json, next_run_utc)
SELECT 'indexer-run-retention', 'IndexerRunRetention', TRUE, '0 4 * * 0', 'UTC', '{}', UTC_TIMESTAMP(3)
FROM DUAL
WHERE NOT EXISTS (SELECT 1 FROM job_schedules WHERE job_type = 'IndexerRunRetention')
  AND NOT EXISTS (SELECT 1 FROM job_schedules WHERE name = 'indexer-run-retention');
