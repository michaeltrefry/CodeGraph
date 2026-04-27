-- Migration 031: add append-only LLM usage tracking

CREATE TABLE llm_usage (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(255) NOT NULL,
    path VARCHAR(64) NOT NULL,
    provider VARCHAR(64) NOT NULL,
    model VARCHAR(255) NOT NULL,
    input_tokens INT NOT NULL DEFAULT 0,
    output_tokens INT NOT NULL DEFAULT 0,
    total_tokens INT NOT NULL DEFAULT 0,
    created_at DATETIME NOT NULL,
    INDEX ix_llm_usage_created_at (created_at),
    INDEX ix_llm_usage_username (username),
    INDEX ix_llm_usage_path (path),
    INDEX ix_llm_usage_provider (provider),
    INDEX ix_llm_usage_user_path_created (username, path, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
