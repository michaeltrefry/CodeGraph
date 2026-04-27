-- Security findings and summaries for vitals security checks

CREATE TABLE IF NOT EXISTS security_findings (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    project VARCHAR(255) NOT NULL,
    dotnet_project VARCHAR(255) NULL,
    category VARCHAR(32) NOT NULL,
    severity VARCHAR(16) NOT NULL DEFAULT 'medium',
    title VARCHAR(512) NOT NULL,
    description TEXT NOT NULL,
    file_path VARCHAR(1024) NULL,
    line_number INT NULL,
    package VARCHAR(255) NULL,
    package_version VARCHAR(64) NULL,
    advisory VARCHAR(255) NULL,
    computed_at DATETIME NOT NULL,
    INDEX ix_security_project (project),
    INDEX ix_security_severity (project, severity)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS project_security_summaries (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    project VARCHAR(255) NOT NULL,
    security_score DOUBLE NOT NULL DEFAULT 10.0,
    critical_count INT NOT NULL DEFAULT 0,
    high_count INT NOT NULL DEFAULT 0,
    medium_count INT NOT NULL DEFAULT 0,
    low_count INT NOT NULL DEFAULT 0,
    analysis TEXT NULL,
    computed_at DATETIME NOT NULL,
    UNIQUE KEY uq_security_summary_project (project)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
