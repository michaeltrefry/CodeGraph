-- Per-user delegated provider credentials. The existing `mcp_hub_credentials` table is left
-- intact for genuinely shared infrastructure credentials (RabbitMQ, MySQL); delegated
-- providers (Shortcut) move to this per-user table — see Shortcut sc-1052.
CREATE TABLE IF NOT EXISTS mcp_provider_credentials (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    provider_key VARCHAR(64) NOT NULL,
    username VARCHAR(255) NOT NULL,
    credential_key VARCHAR(64) NOT NULL,
    encrypted_value LONGTEXT NULL,
    token_fingerprint VARCHAR(128) NULL,
    provider_identity VARCHAR(512) NULL,
    validation_state VARCHAR(32) NOT NULL DEFAULT 'unverified',
    validation_message VARCHAR(512) NULL,
    last_validated_at_utc DATETIME(3) NULL,
    last_attempt_at_utc DATETIME(3) NULL,
    expires_at_utc DATETIME(3) NULL,
    created_at_utc DATETIME(3) NOT NULL,
    updated_at_utc DATETIME(3) NOT NULL,
    UNIQUE KEY ux_mcp_provider_credentials (provider_key, username, credential_key),
    INDEX ix_mcp_provider_credentials_user (username)
) ENGINE=InnoDB;
