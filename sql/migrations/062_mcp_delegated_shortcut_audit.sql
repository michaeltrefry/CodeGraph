ALTER TABLE mcp_hub_audit
    ADD COLUMN IF NOT EXISTS provider_identity VARCHAR(255) NULL AFTER credential_mode;
