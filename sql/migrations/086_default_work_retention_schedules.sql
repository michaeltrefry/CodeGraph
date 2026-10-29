-- sc-27887: message retention must run without anyone scheduling it (self-maintaining). Seed a daily
-- PruneWorkMessages schedule unless one exists (an operator's schedule, enabled or not, is left as
-- configured). PruneWorkSessions (sc-27882) is not on this branch; its integration seeds its own.
INSERT INTO job_schedules (name, job_type, is_enabled, cron_expression, time_zone_id, args_json, next_run_utc)
SELECT 'prune-work-messages', 'PruneWorkMessages', TRUE, '30 3 * * *', 'UTC', '{}', UTC_TIMESTAMP(3)
FROM DUAL
WHERE NOT EXISTS (SELECT 1 FROM job_schedules WHERE job_type = 'PruneWorkMessages')
  AND NOT EXISTS (SELECT 1 FROM job_schedules WHERE name = 'prune-work-messages');
