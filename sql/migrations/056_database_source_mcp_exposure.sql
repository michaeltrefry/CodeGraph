-- Per-source MCP Hub exposure controls — see Shortcut sc-1058.
--   mcp_hub_enabled    fail-closed gate: is this source exposed to the MCP Hub at all
--   mcp_exposure_mode  SchemaOnly | NamedToolsOnly | AggregateOnly | ReadOnlySql
--   mcp_display_name / mcp_environment  admin-facing metadata
-- Existing rows default to NOT exposed, so the MySQL provider fails closed until an admin opts a source in.
ALTER TABLE database_sources
    ADD COLUMN mcp_hub_enabled BOOLEAN NOT NULL DEFAULT FALSE AFTER enabled,
    ADD COLUMN mcp_exposure_mode VARCHAR(32) NOT NULL DEFAULT 'SchemaOnly' AFTER mcp_hub_enabled,
    ADD COLUMN mcp_display_name VARCHAR(255) NULL AFTER mcp_exposure_mode,
    ADD COLUMN mcp_environment VARCHAR(64) NULL AFTER mcp_display_name;
