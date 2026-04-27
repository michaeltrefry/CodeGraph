CREATE TABLE IF NOT EXISTS mcp_personal_access_tokens (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(255) NOT NULL,
    token_name VARCHAR(255) NOT NULL,
    token_prefix VARCHAR(32) NOT NULL,
    token_hash VARCHAR(64) NOT NULL,
    last_four CHAR(4) NOT NULL,
    created_at DATETIME(3) NOT NULL,
    expires_at DATETIME(3) NOT NULL,
    revoked_at DATETIME(3) NULL,
    last_used_at DATETIME(3) NULL,
    last_used_from VARCHAR(255) NULL,
    UNIQUE KEY uq_mcp_personal_access_tokens_token_hash (token_hash)
) ENGINE=InnoDB;

CREATE INDEX ix_mcp_personal_access_tokens_username
    ON mcp_personal_access_tokens (username);

CREATE INDEX ix_mcp_personal_access_tokens_expires_at
    ON mcp_personal_access_tokens (expires_at);

CREATE INDEX ix_mcp_personal_access_tokens_revoked_at
    ON mcp_personal_access_tokens (revoked_at);

CREATE INDEX ix_mcp_personal_access_tokens_username_revoked_at_expires_at
    ON mcp_personal_access_tokens (username, revoked_at, expires_at);
