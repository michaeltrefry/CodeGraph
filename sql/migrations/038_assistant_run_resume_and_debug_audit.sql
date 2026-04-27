ALTER TABLE assistant_runs
    ADD COLUMN execution_state_json JSON NULL AFTER request_hash;

CREATE TABLE assistant_debug_trace_audit (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    run_id BIGINT NOT NULL,
    chat_id VARCHAR(255) NOT NULL,
    run_username VARCHAR(255) NOT NULL,
    viewed_by_username VARCHAR(255) NOT NULL,
    remote_ip VARCHAR(255) NULL,
    user_agent LONGTEXT NULL,
    viewed_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    CONSTRAINT fk_assistant_debug_trace_audit_run_id
        FOREIGN KEY (run_id) REFERENCES assistant_runs(id) ON DELETE CASCADE,
    INDEX ix_assistant_debug_trace_audit_run_id_viewed_at (run_id, viewed_at),
    INDEX ix_assistant_debug_trace_audit_viewed_by_username_viewed_at (viewed_by_username, viewed_at)
) ENGINE=InnoDB;
