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
