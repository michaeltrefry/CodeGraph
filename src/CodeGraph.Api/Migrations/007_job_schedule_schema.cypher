// Job scheduling schema for embedded worker-driven schedules.

CREATE CONSTRAINT job_schedule_appid IF NOT EXISTS
FOR (s:JobSchedule) REQUIRE s.appId IS UNIQUE;

CREATE CONSTRAINT job_schedule_name IF NOT EXISTS
FOR (s:JobSchedule) REQUIRE s.name IS UNIQUE;

CREATE INDEX job_schedule_enabled_next IF NOT EXISTS
FOR (s:JobSchedule) ON (s.isEnabled, s.nextRunUtc);

CREATE INDEX job_schedule_lease_expires IF NOT EXISTS
FOR (s:JobSchedule) ON (s.leaseExpiresUtc);

CREATE INDEX job_schedule_type IF NOT EXISTS
FOR (s:JobSchedule) ON (s.jobType);
