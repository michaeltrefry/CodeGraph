-- CG-167: re-run every project's memory scope backfill daily, so one that failed when the project
-- was created or its repositories changed is retried. Idempotent. An existing operator schedule wins.
INSERT INTO job_schedules (name, job_type, is_enabled, cron_expression, time_zone_id, args_json, next_run_utc)
SELECT 'memory-project-scope-reconciliation', 'ReconcileMemoryProjectScopes', TRUE, '30 3 * * *', 'UTC', '{}', UTC_TIMESTAMP(3)
FROM DUAL
WHERE NOT EXISTS (SELECT 1 FROM job_schedules WHERE job_type = 'ReconcileMemoryProjectScopes')
  AND NOT EXISTS (SELECT 1 FROM job_schedules WHERE name = 'memory-project-scope-reconciliation');
