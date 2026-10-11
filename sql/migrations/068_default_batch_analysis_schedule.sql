-- sc-25698: submitted analysis batches are finished by the periodic ProcessBatchAnalysis job.
-- The per-batch delayed re-check needed RabbitMQ's delayed-message exchange plugin, which brokers
-- may not have, so every installation needs the sweep. Seed it unless one already exists (an
-- operator's existing schedule, enabled or not, is left as configured).
INSERT INTO job_schedules (name, job_type, is_enabled, cron_expression, time_zone_id, args_json, next_run_utc)
SELECT 'process-batch-analysis', 'ProcessBatchAnalysis', TRUE, '*/15 * * * *', 'UTC', '{}', UTC_TIMESTAMP(3)
FROM DUAL
WHERE NOT EXISTS (SELECT 1 FROM job_schedules WHERE job_type = 'ProcessBatchAnalysis')
  AND NOT EXISTS (SELECT 1 FROM job_schedules WHERE name = 'process-batch-analysis');
