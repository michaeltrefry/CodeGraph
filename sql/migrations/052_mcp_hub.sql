ALTER TABLE mcp_personal_access_tokens
    ADD COLUMN entitlement_mode VARCHAR(32) NOT NULL DEFAULT 'all' AFTER last_used_from;

CREATE TABLE IF NOT EXISTS mcp_personal_access_token_tool_entitlements (
    token_id BIGINT NOT NULL,
    tool_name VARCHAR(255) NOT NULL,
    created_at DATETIME(3) NOT NULL,
    PRIMARY KEY (token_id, tool_name),
    INDEX ix_mcp_pat_tool_entitlements_tool_name (tool_name),
    CONSTRAINT fk_mcp_pat_tool_entitlements_token
        FOREIGN KEY (token_id) REFERENCES mcp_personal_access_tokens(id)
        ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS mcp_hub_providers (
    provider_key VARCHAR(64) NOT NULL PRIMARY KEY,
    display_name VARCHAR(255) NOT NULL,
    description TEXT NOT NULL,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    source_visible BOOLEAN NOT NULL DEFAULT FALSE,
    created_at_utc DATETIME(3) NOT NULL,
    updated_at_utc DATETIME(3) NOT NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS mcp_hub_tools (
    tool_name VARCHAR(255) NOT NULL PRIMARY KEY,
    provider_key VARCHAR(64) NOT NULL,
    display_name VARCHAR(255) NOT NULL,
    description TEXT NOT NULL,
    read_only BOOLEAN NOT NULL DEFAULT TRUE,
    destructive BOOLEAN NOT NULL DEFAULT FALSE,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    requires_credential BOOLEAN NOT NULL DEFAULT FALSE,
    created_at_utc DATETIME(3) NOT NULL,
    updated_at_utc DATETIME(3) NOT NULL,
    INDEX ix_mcp_hub_tools_provider_key (provider_key),
    CONSTRAINT fk_mcp_hub_tools_provider
        FOREIGN KEY (provider_key) REFERENCES mcp_hub_providers(provider_key)
        ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS mcp_hub_credentials (
    provider_key VARCHAR(64) NOT NULL,
    credential_key VARCHAR(64) NOT NULL,
    encrypted_value LONGTEXT NULL,
    updated_by VARCHAR(255) NULL,
    updated_at_utc DATETIME(3) NULL,
    PRIMARY KEY (provider_key, credential_key),
    CONSTRAINT fk_mcp_hub_credentials_provider
        FOREIGN KEY (provider_key) REFERENCES mcp_hub_providers(provider_key)
        ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS mcp_hub_config (
    provider_key VARCHAR(64) NOT NULL,
    config_key VARCHAR(64) NOT NULL,
    config_value LONGTEXT NULL,
    updated_by VARCHAR(255) NULL,
    updated_at_utc DATETIME(3) NULL,
    PRIMARY KEY (provider_key, config_key),
    CONSTRAINT fk_mcp_hub_config_provider
        FOREIGN KEY (provider_key) REFERENCES mcp_hub_providers(provider_key)
        ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS mcp_hub_audit (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(255) NULL,
    token_id BIGINT NULL,
    provider_key VARCHAR(64) NOT NULL,
    tool_name VARCHAR(255) NOT NULL,
    action VARCHAR(64) NOT NULL,
    operation VARCHAR(128) NOT NULL DEFAULT '',
    resource_key VARCHAR(255) NULL,
    credential_mode VARCHAR(64) NOT NULL DEFAULT 'none',
    authorization_decision VARCHAR(64) NOT NULL DEFAULT 'unknown',
    status_class VARCHAR(64) NOT NULL DEFAULT 'unknown',
    duration_ms INT NOT NULL DEFAULT 0,
    success BOOLEAN NOT NULL,
    message TEXT NULL,
    created_at_utc DATETIME(3) NOT NULL,
    INDEX ix_mcp_hub_audit_created_at (created_at_utc),
    INDEX ix_mcp_hub_audit_provider_tool_created (provider_key, tool_name, created_at_utc),
    INDEX ix_mcp_hub_audit_token_created (token_id, created_at_utc),
    INDEX ix_mcp_hub_audit_resource_created (provider_key, resource_key, created_at_utc)
) ENGINE=InnoDB;
