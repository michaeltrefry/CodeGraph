-- Downstream-MCP shim support — see Shortcut sc-1056.
--   provider_type  native | provider | shim — system-owned classification of a hub tool.
--                  Native CodeGraph tools and first-party provider tools are catalog-seeded;
--                  shim tools are discovered from a downstream MCP server's tools/list.
--                  Existing rows default to 'native'; the catalog seeder corrects first-party
--                  provider tools to 'provider' on its next run.
--   input_schema   JSON input schema captured from a downstream MCP server during discovery,
--                  so a shim tool can be advertised over /mcp without a static
--                  [McpServerTool] method. NULL for native/provider tools — their schema is
--                  served by the MCP SDK.
ALTER TABLE mcp_hub_tools
    ADD COLUMN provider_type VARCHAR(32) NOT NULL DEFAULT 'native' AFTER provider_key,
    ADD COLUMN input_schema LONGTEXT NULL AFTER access_class;

-- Audit envelope: record which kind of provider served the call so shim invocations are
-- distinguishable from native/first-party-provider invocations. Existing rows pre-date the
-- shim, so 'provider' is the safe default for them.
ALTER TABLE mcp_hub_audit
    ADD COLUMN provider_type VARCHAR(32) NOT NULL DEFAULT 'provider' AFTER provider_key;
