-- Explicit catalog-state model for hub tools — see Shortcut sc-1055.
--   is_enabled       (existing `enabled` column) — admin-owned: may this tool be used at all
--   is_available     system-owned: does the tool actually exist/work in this deployment
--   default_selected token-creation guidance only: pre-checked when minting a PAT
--   access_class     UI grouping label (read | write | admin)
-- Existing rows take the column defaults, which is a safe conservative baseline.
ALTER TABLE mcp_hub_tools
    ADD COLUMN is_available BOOLEAN NOT NULL DEFAULT TRUE AFTER enabled,
    ADD COLUMN default_selected BOOLEAN NOT NULL DEFAULT FALSE AFTER is_available,
    ADD COLUMN access_class VARCHAR(32) NOT NULL DEFAULT 'read' AFTER default_selected;
