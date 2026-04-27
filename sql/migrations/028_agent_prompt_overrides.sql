CREATE TABLE IF NOT EXISTS agent_prompt_overrides (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    prompt_key VARCHAR(255) NOT NULL,
    prompt_text MEDIUMTEXT NOT NULL,
    updated_by VARCHAR(255) NOT NULL,
    updated_at DATETIME(3) NOT NULL,
    UNIQUE KEY uq_agent_prompt_overrides_prompt_key (prompt_key)
) ENGINE=InnoDB;

CREATE INDEX ix_agent_prompt_overrides_updated_at
    ON agent_prompt_overrides (updated_at);
