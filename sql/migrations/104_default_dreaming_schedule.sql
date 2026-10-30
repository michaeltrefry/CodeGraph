-- sc-27969: dream nightly without manual setup. Preserve operator-created schedules, including disabled ones.
INSERT INTO job_schedules (name, job_type, is_enabled, cron_expression, time_zone_id, args_json, next_run_utc)
SELECT 'dream-projects', 'DreamProjects', TRUE, '0 2 * * *', 'UTC', '{}', UTC_TIMESTAMP(3)
FROM DUAL
WHERE NOT EXISTS (SELECT 1 FROM job_schedules WHERE job_type = 'DreamProjects')
  AND NOT EXISTS (SELECT 1 FROM job_schedules WHERE name = 'dream-projects');
