-- sc-27887, sc-27882: message and session retention must run without anyone scheduling them
-- (self-maintaining). Seed daily PruneWorkMessages and PruneWorkSessions schedules unless one exists for
-- the job type or name (an operator's schedule, enabled or not, is left as configured).
INSERT INTO job_schedules (name, job_type, is_enabled, cron_expression, time_zone_id, args_json, next_run_utc)
SELECT 'prune-work-messages', 'PruneWorkMessages', TRUE, '30 3 * * *', 'UTC', '{}', UTC_TIMESTAMP(3)
FROM DUAL
WHERE NOT EXISTS (SELECT 1 FROM job_schedules WHERE job_type = 'PruneWorkMessages')
  AND NOT EXISTS (SELECT 1 FROM job_schedules WHERE name = 'prune-work-messages');

INSERT INTO job_schedules (name, job_type, is_enabled, cron_expression, time_zone_id, args_json, next_run_utc)
SELECT 'prune-work-sessions', 'PruneWorkSessions', TRUE, '15 3 * * *', 'UTC', '{}', UTC_TIMESTAMP(3)
FROM DUAL
WHERE NOT EXISTS (SELECT 1 FROM job_schedules WHERE job_type = 'PruneWorkSessions')
  AND NOT EXISTS (SELECT 1 FROM job_schedules WHERE name = 'prune-work-sessions');
