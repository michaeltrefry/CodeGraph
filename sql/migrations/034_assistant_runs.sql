CREATE TABLE assistant_runs (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    chat_id VARCHAR(255) NOT NULL,
    username VARCHAR(255) NOT NULL,
    status VARCHAR(50) NOT NULL,
    question LONGTEXT NOT NULL,
    context LONGTEXT NULL,
    history_json JSON NULL,
    provider_requested VARCHAR(100) NULL,
    model_requested VARCHAR(255) NULL,
    provider_used VARCHAR(100) NULL,
    model_used VARCHAR(255) NULL,
    final_answer LONGTEXT NULL,
    warnings_json JSON NULL,
    error LONGTEXT NULL,
    created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    started_at DATETIME(3) NULL,
    completed_at DATETIME(3) NULL,
    last_sequence BIGINT NOT NULL DEFAULT 0,
    INDEX ix_assistant_runs_username_created_at (username, created_at),
    INDEX ix_assistant_runs_username_chat_id_created_at (username, chat_id, created_at),
    INDEX ix_assistant_runs_status_created_at (status, created_at)
) ENGINE=InnoDB;

CREATE TABLE assistant_run_events (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    run_id BIGINT NOT NULL,
    sequence BIGINT NOT NULL,
    type VARCHAR(50) NOT NULL,
    content_json JSON NULL,
    created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    CONSTRAINT fk_assistant_run_events_run_id
        FOREIGN KEY (run_id) REFERENCES assistant_runs(id) ON DELETE CASCADE,
    UNIQUE KEY ux_assistant_run_events_run_id_sequence (run_id, sequence),
    INDEX ix_assistant_run_events_run_id_created_at (run_id, created_at)
) ENGINE=InnoDB;
