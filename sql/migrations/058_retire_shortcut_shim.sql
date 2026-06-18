-- Retire the downstream Shortcut MCP shim now that the hub hosts native Shortcut
-- tools backed directly by the Shortcut REST API.

UPDATE mcp_hub_providers
SET enabled = FALSE,
    description = 'Retired downstream Shortcut MCP shim. Use the native Shortcut provider instead.',
    updated_at_utc = UTC_TIMESTAMP()
WHERE provider_key = 'shortcut-shim';

UPDATE mcp_hub_tools
SET enabled = FALSE,
    is_available = FALSE,
    updated_at_utc = UTC_TIMESTAMP()
WHERE provider_key = 'shortcut-shim';
