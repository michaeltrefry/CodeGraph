CREATE TABLE IF NOT EXISTS application_logs (
    id BIGINT NOT NULL AUTO_INCREMENT,
    occurred_at_utc DATETIME(6) NOT NULL,
    level VARCHAR(16) NOT NULL,
    source VARCHAR(128) NOT NULL,
    category VARCHAR(512) NOT NULL,
    event_id INT NOT NULL DEFAULT 0,
    message MEDIUMTEXT NOT NULL,
    exception LONGTEXT NULL,
    trace_id VARCHAR(32) NULL,
    span_id VARCHAR(16) NULL,
    properties_json JSON NULL,
    PRIMARY KEY (id),
    INDEX ix_application_logs_occurred_at (occurred_at_utc),
    INDEX ix_application_logs_level_time (level, occurred_at_utc),
    INDEX ix_application_logs_source_time (source, occurred_at_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
