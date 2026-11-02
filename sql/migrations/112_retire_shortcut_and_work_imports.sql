-- Retire the Shortcut integration and the work importers (CG-147) after the 2026-11-02 cutover, when
-- CodeGraph became the tracker. Removed from the code: the native Shortcut MCP provider, the generic
-- MCP shim (Shortcut was its only user), per-user delegated provider credentials and the Shortcut and
-- KanbanBoard importers with their WorkImport job.
--
-- Kept: every imported project, feature, item, comment, link and attachment, the imported provenance
-- (work_features.imported_json, work_items.imported_json), the sc-NNNN item aliases (work_item_aliases)
-- and the MCP Hub audit history. An epic's sc-N now resolves through its feature's imported_json
-- (external_id sc-epic-N), which the importer wrote in the same write as its import record, so the
-- import tables are no longer read.

-- Token grants of the retired tools (native Shortcut tools and any discovered shim tools).
DELETE FROM mcp_personal_access_token_tool_entitlements
WHERE tool_name IN (
    SELECT tool_name FROM mcp_hub_tools WHERE provider_key IN ('shortcut', 'shortcut-shim', 'kanban'));

-- The retired providers with their tools, shared credentials (shortcut/apiToken, kanban/apiToken,
-- shortcut-shim/discoveryToken) and config (shortcut/allowSharedCredentialMode,
-- shortcut-shim/discoveryUrl). Credentials and config would cascade with the provider rows; they
-- are deleted explicitly so the statement order says what goes.
DELETE FROM mcp_hub_tools WHERE provider_key IN ('shortcut', 'shortcut-shim', 'kanban');
DELETE FROM mcp_hub_credentials WHERE provider_key IN ('shortcut', 'shortcut-shim', 'kanban');
DELETE FROM mcp_hub_config WHERE provider_key IN ('shortcut', 'shortcut-shim', 'kanban');
DELETE FROM mcp_hub_providers WHERE provider_key IN ('shortcut', 'shortcut-shim', 'kanban');

-- Per-user delegated credentials (migration 054): only Shortcut used them.
DROP TABLE IF EXISTS mcp_provider_credentials;

-- The input schema of discovered shim tools (migration 057); native and provider tools never set it.
ALTER TABLE mcp_hub_tools DROP COLUMN IF EXISTS input_schema;

-- A WorkImport schedule an admin created (none is seeded); the job type no longer exists.
DELETE FROM job_schedules WHERE job_type = 'WorkImport';

-- The import bookkeeping (migration 087): idempotency records, settings and run reports.
DROP TABLE IF EXISTS work_import_records;
DROP TABLE IF EXISTS work_import_runs;
DROP TABLE IF EXISTS work_import_settings;
