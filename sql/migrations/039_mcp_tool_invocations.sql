CREATE TABLE IF NOT EXISTS mcp_tool_invocations (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(255) NULL,
    token_id BIGINT NULL,
    tool_name VARCHAR(255) NOT NULL,
    success TINYINT(1) NOT NULL,
    duration_ms INT NOT NULL,
    error_code VARCHAR(255) NULL,
    created_at DATETIME(3) NOT NULL,
    INDEX ix_mcp_tool_invocations_created_at (created_at),
    INDEX ix_mcp_tool_invocations_username_created_at (username, created_at),
    INDEX ix_mcp_tool_invocations_tool_name_created_at (tool_name, created_at),
    INDEX ix_mcp_tool_invocations_token_id_created_at (token_id, created_at),
    INDEX ix_mcp_tool_invocations_success_created_at (success, created_at)
) ENGINE=InnoDB;
