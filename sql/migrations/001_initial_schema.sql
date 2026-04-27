-- CodeGraph schema (consolidated)

CREATE TABLE IF NOT EXISTS migration_history (
    id INT AUTO_INCREMENT PRIMARY KEY,
    script_name VARCHAR(255) NOT NULL UNIQUE,
    applied_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
) ENGINE=InnoDB;

CREATE TABLE repositories (
    name VARCHAR(255) PRIMARY KEY,
    repo_url VARCHAR(500),
    local_path VARCHAR(500),
    default_branch VARCHAR(100) DEFAULT 'main',
    last_commit_sha VARCHAR(40),
    indexed_at DATETIME(3),
    language VARCHAR(50),
    framework VARCHAR(100),
    is_foundational BOOLEAN DEFAULT FALSE,
    properties JSON,
    created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    updated_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3)
) ENGINE=InnoDB;

CREATE TABLE file_hashes (
    project VARCHAR(255) NOT NULL,
    rel_path VARCHAR(500) NOT NULL,
    content_hash VARCHAR(64) NOT NULL,
    PRIMARY KEY (project, rel_path),
    FOREIGN KEY (project) REFERENCES repositories(name) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE nodes (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    project VARCHAR(255) NOT NULL,
    dotnet_project VARCHAR(255) NULL,
    label VARCHAR(50) NOT NULL,
    name VARCHAR(255) NOT NULL,
    qualified_name VARCHAR(1000) NOT NULL,
    file_path VARCHAR(500) DEFAULT '',
    start_line INT DEFAULT 0,
    end_line INT DEFAULT 0,
    properties JSON,
    UNIQUE KEY uq_node (project, qualified_name(700)),
    INDEX idx_nodes_label (project, label),
    INDEX idx_nodes_name (project, name),
    INDEX idx_nodes_file (project, file_path),
    INDEX idx_nodes_dotnet_project (dotnet_project),
    FOREIGN KEY (project) REFERENCES repositories(name) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE edges (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    project VARCHAR(255) NOT NULL,
    source_id BIGINT NOT NULL,
    target_id BIGINT NOT NULL,
    type VARCHAR(50) NOT NULL,
    properties JSON,
    UNIQUE KEY uq_edge (source_id, target_id, type),
    INDEX idx_edges_source (source_id, type),
    INDEX idx_edges_target (target_id, type),
    INDEX idx_edges_type (project, type),
    FOREIGN KEY (source_id) REFERENCES nodes(id) ON DELETE CASCADE,
    FOREIGN KEY (target_id) REFERENCES nodes(id) ON DELETE CASCADE,
    FOREIGN KEY (project) REFERENCES repositories(name) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE cross_repo_edges (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    source_project VARCHAR(255) NOT NULL,
    target_project VARCHAR(255) NOT NULL,
    source_node_id BIGINT NOT NULL,
    target_node_id BIGINT NOT NULL,
    type VARCHAR(50) NOT NULL,
    properties JSON,
    UNIQUE KEY uq_xedge (source_node_id, target_node_id, type),
    INDEX idx_xedge_source (source_project, type),
    INDEX idx_xedge_target (target_project, type),
    FOREIGN KEY (source_node_id) REFERENCES nodes(id) ON DELETE CASCADE,
    FOREIGN KEY (target_node_id) REFERENCES nodes(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE repository_summaries (
    project VARCHAR(255) PRIMARY KEY,
    summary TEXT NOT NULL,
    confidence VARCHAR(10) NOT NULL DEFAULT 'medium',
    source_hash VARCHAR(64) NOT NULL,
    model_used VARCHAR(100) NULL,
    created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    updated_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
    FOREIGN KEY (project) REFERENCES repositories(name) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE sync_state (
    project VARCHAR(255) PRIMARY KEY,
    last_sync_at DATETIME(3),
    last_commit_sha VARCHAR(40),
    status ENUM('idle', 'syncing', 'error') DEFAULT 'idle',
    error_message TEXT,
    FOREIGN KEY (project) REFERENCES repositories(name) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE project_analyses (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    repo VARCHAR(255) NOT NULL,
    project_name VARCHAR(255) NOT NULL,
    summary TEXT NOT NULL,
    confidence VARCHAR(20) NOT NULL DEFAULT 'medium',
    endpoints JSON,
    services JSON,
    external_dependencies JSON,
    database_tables JSON,
    model_used VARCHAR(100) NULL,
    created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    updated_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
    UNIQUE KEY uq_project_analysis (repo, project_name),
    INDEX idx_pa_repo (repo),
    FOREIGN KEY (repo) REFERENCES repositories(name) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE analysis_batches (
    id                  BIGINT AUTO_INCREMENT PRIMARY KEY,
    repo                VARCHAR(500)    NOT NULL,
    anthropic_batch_id  VARCHAR(200)    NOT NULL,
    status              VARCHAR(50)     NOT NULL DEFAULT 'submitted',
    request_count       INT             NOT NULL DEFAULT 0,
    completed_count     INT             NOT NULL DEFAULT 0,
    submitted_at        DATETIME(6)     NOT NULL,
    completed_at        DATETIME(6)     NULL,
    INDEX idx_ab_repo       (repo),
    INDEX idx_ab_status     (status),
    INDEX idx_ab_anthropic  (anthropic_batch_id)
) ENGINE=InnoDB;

CREATE TABLE analysis_batch_requests (
    id              BIGINT AUTO_INCREMENT PRIMARY KEY,
    batch_id        BIGINT          NOT NULL,
    custom_id       VARCHAR(200)    NOT NULL,
    node_id         BIGINT          NULL,
    node_label      VARCHAR(100)    NOT NULL,
    status          VARCHAR(50)     NOT NULL DEFAULT 'pending',
    completed_at    DATETIME(6)     NULL,
    FOREIGN KEY (batch_id) REFERENCES analysis_batches(id),
    INDEX idx_abr_batch  (batch_id),
    INDEX idx_abr_node   (node_id),
    INDEX idx_abr_custom (custom_id)
) ENGINE=InnoDB;

CREATE TABLE node_analysis (
    node_id     BIGINT          NOT NULL PRIMARY KEY,
    description TEXT            NOT NULL,
    confidence  VARCHAR(20)     NOT NULL DEFAULT 'medium',
    model_used  VARCHAR(100)    NULL,
    created_at  DATETIME(6)     NOT NULL,
    updated_at  DATETIME(6)     NOT NULL
) ENGINE=InnoDB;
