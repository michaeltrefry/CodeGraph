ALTER TABLE assistant_runs
    ADD COLUMN message_index_start BIGINT NOT NULL DEFAULT 0 AFTER error,
    ADD COLUMN message_index_end BIGINT NOT NULL DEFAULT 0 AFTER message_index_start,
    ADD COLUMN idempotency_key VARCHAR(255) NULL AFTER message_index_end,
    ADD COLUMN request_hash VARCHAR(128) NULL AFTER idempotency_key,
    ADD UNIQUE KEY ux_assistant_runs_username_idempotency_key (username, idempotency_key);

CREATE TABLE assistant_chat_messages (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(255) NOT NULL,
    chat_id VARCHAR(255) NOT NULL,
    message_index BIGINT NOT NULL,
    role VARCHAR(50) NOT NULL,
    content LONGTEXT NOT NULL,
    source_run_id BIGINT NULL,
    created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    CONSTRAINT fk_assistant_chat_messages_source_run_id
        FOREIGN KEY (source_run_id) REFERENCES assistant_runs(id) ON DELETE SET NULL,
    UNIQUE KEY ux_assistant_chat_messages_username_chat_id_message_index (username, chat_id, message_index),
    INDEX ix_assistant_chat_messages_username_chat_id_created_at (username, chat_id, created_at)
) ENGINE=InnoDB;
