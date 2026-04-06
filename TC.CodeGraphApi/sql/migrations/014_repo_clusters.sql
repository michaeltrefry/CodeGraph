CREATE TABLE IF NOT EXISTS repo_clusters (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    project_name VARCHAR(500) NOT NULL,
    cluster_id INT NOT NULL,
    cluster_label VARCHAR(500),
    modularity_score DECIMAL(6,4),
    level INT DEFAULT 0,
    betweenness_centrality DECIMAL(8,6) DEFAULT 0,
    computed_at DATETIME NOT NULL,
    INDEX idx_cluster (cluster_id, level),
    INDEX idx_project (project_name),
    UNIQUE KEY uq_project_level (project_name, level)
) ENGINE=InnoDB;
