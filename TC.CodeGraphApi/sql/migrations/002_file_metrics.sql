CREATE TABLE IF NOT EXISTS file_metrics (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    project VARCHAR(255) NOT NULL,
    file_path VARCHAR(1024) NOT NULL,
    dotnet_project VARCHAR(255) NULL,
    changes INT NOT NULL DEFAULT 0,
    lines_added INT NOT NULL DEFAULT 0,
    lines_removed INT NOT NULL DEFAULT 0,
    author_count INT NOT NULL DEFAULT 0,
    last_change_at DATETIME NULL,
    complexity_score INT NOT NULL DEFAULT 0,
    max_nesting_depth INT NOT NULL DEFAULT 0,
    deep_nesting_lines INT NOT NULL DEFAULT 0,
    function_count INT NOT NULL DEFAULT 0,
    longest_function INT NOT NULL DEFAULT 0,
    max_coupling_strength DOUBLE NOT NULL DEFAULT 0,
    coupling_partners INT NOT NULL DEFAULT 0,
    truck_factor INT NOT NULL DEFAULT 0,
    top_authors JSON NULL,
    health_score DOUBLE NOT NULL DEFAULT 5.0,
    role VARCHAR(16) NOT NULL DEFAULT 'core',
    risk_score DOUBLE NOT NULL DEFAULT 0,
    computed_at DATETIME NOT NULL,
    UNIQUE KEY uq_file_metrics_project_path (project, file_path(512)),
    INDEX ix_file_metrics_project_health (project, health_score),
    INDEX ix_file_metrics_project_risk (project, risk_score DESC)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS project_health_analyses (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    project VARCHAR(255) NOT NULL,
    dotnet_project VARCHAR(255) NULL,
    analysis TEXT NOT NULL,
    confidence VARCHAR(16) NOT NULL DEFAULT 'medium',
    model_used VARCHAR(100) NULL,
    created_at DATETIME NOT NULL,
    updated_at DATETIME NOT NULL,
    UNIQUE KEY uq_health_analysis_project_dp (project, dotnet_project),
    INDEX ix_health_analysis_project (project)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS project_health_summaries (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    project VARCHAR(255) NOT NULL,
    dotnet_project VARCHAR(255) NULL,
    overall_health DOUBLE NOT NULL DEFAULT 5.0,
    total_files INT NOT NULL DEFAULT 0,
    hotspot_count INT NOT NULL DEFAULT 0,
    alert_count INT NOT NULL DEFAULT 0,
    top_hotspots JSON NULL,
    computed_at DATETIME NOT NULL,
    UNIQUE KEY uq_project_health_project_dp (project, dotnet_project),
    INDEX ix_project_health_project (project)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
