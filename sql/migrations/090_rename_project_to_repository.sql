-- sc-27867: "project" meant a repository from the days it meant a C# project. A project is now a
-- multi-repo work scope (073_work_tracking.sql), so every column that holds repositories.name is
-- renamed to say so. dotnet_project (a .NET project file inside a repository) and the
-- project_name columns of the project-review and project-analysis tables (the .NET project a row
-- is about) keep their names: they never meant a repository.
--
-- Restart safety: every statement is a no-op once its change is in place (RENAME COLUMN / RENAME
-- INDEX / RENAME TABLE ... IF EXISTS, ADD CONSTRAINT), so a host interrupted between
-- statements resumes from the statement journal. Column renames carry their indexes, primary keys
-- and foreign keys with them. Index and constraint renames are cosmetic and follow the columns.

-- The one table that only ever described a repository.
RENAME TABLE IF EXISTS project_security_summaries TO repository_security_summaries;

-- Graph
ALTER TABLE nodes RENAME COLUMN IF EXISTS project TO repository;
ALTER TABLE edges RENAME COLUMN IF EXISTS project TO repository;
ALTER TABLE cross_repo_edges RENAME COLUMN IF EXISTS source_project TO source_repository;
ALTER TABLE cross_repo_edges RENAME COLUMN IF EXISTS target_project TO target_repository;
ALTER TABLE file_hashes RENAME COLUMN IF EXISTS project TO repository;
ALTER TABLE sync_state RENAME COLUMN IF EXISTS project TO repository;
ALTER TABLE repository_summaries RENAME COLUMN IF EXISTS project TO repository;
ALTER TABLE repo_clusters RENAME COLUMN IF EXISTS project_name TO repository_name;
ALTER TABLE repo_clusters RENAME INDEX IF EXISTS idx_project TO idx_repository;
ALTER TABLE repo_clusters RENAME INDEX IF EXISTS uq_project_level TO uq_repository_level;

-- Metrics, health, security, diagnostics, reviews, overlays
ALTER TABLE file_metrics RENAME COLUMN IF EXISTS project TO repository;
ALTER TABLE file_metrics RENAME INDEX IF EXISTS uq_file_metrics_project_path TO uq_file_metrics_repository_path;
ALTER TABLE file_metrics RENAME INDEX IF EXISTS ix_file_metrics_project_health TO ix_file_metrics_repository_health;
ALTER TABLE file_metrics RENAME INDEX IF EXISTS ix_file_metrics_project_risk TO ix_file_metrics_repository_risk;

ALTER TABLE project_health_summaries RENAME COLUMN IF EXISTS project TO repository;
ALTER TABLE project_health_summaries RENAME INDEX IF EXISTS uq_project_health_project_dp TO uq_repository_health_repository_dp;
ALTER TABLE project_health_summaries RENAME INDEX IF EXISTS ix_project_health_project TO ix_project_health_repository;

ALTER TABLE project_health_analyses RENAME COLUMN IF EXISTS project TO repository;
ALTER TABLE project_health_analyses RENAME INDEX IF EXISTS uq_health_analysis_project_dp TO uq_health_analysis_repository_dp;
ALTER TABLE project_health_analyses RENAME INDEX IF EXISTS ix_health_analysis_project TO ix_health_analysis_repository;

ALTER TABLE repository_security_summaries RENAME COLUMN IF EXISTS project TO repository;
ALTER TABLE repository_security_summaries RENAME INDEX IF EXISTS uq_security_summary_project TO uq_security_summary_repository;

ALTER TABLE security_findings RENAME COLUMN IF EXISTS project TO repository;
ALTER TABLE security_findings RENAME INDEX IF EXISTS ix_security_project TO ix_security_repository;

ALTER TABLE project_diagnostics RENAME COLUMN IF EXISTS project TO repository;
ALTER TABLE project_diagnostics RENAME INDEX IF EXISTS ix_project_diagnostics_project_severity TO ix_project_diagnostics_repository_severity;

ALTER TABLE project_review_runs RENAME COLUMN IF EXISTS project TO repository;
ALTER TABLE project_review_runs RENAME INDEX IF EXISTS ix_project_review_runs_project_name_created TO ix_project_review_runs_repository_project_created;
ALTER TABLE project_review_runs RENAME INDEX IF EXISTS ix_project_review_runs_project_sha_created TO ix_project_review_runs_repository_sha_created;

ALTER TABLE repository_vitality_summaries RENAME COLUMN IF EXISTS project TO repository;
ALTER TABLE repository_vitality_summaries RENAME INDEX IF EXISTS uq_repository_vitality_project TO uq_repository_vitality_repository;

ALTER TABLE branch_overlays RENAME COLUMN IF EXISTS project TO repository;
ALTER TABLE branch_overlays RENAME INDEX IF EXISTS ix_branch_overlays_project TO ix_branch_overlays_repository;
ALTER TABLE branch_overlays DROP FOREIGN KEY IF EXISTS fk_branch_overlays_project;
ALTER TABLE branch_overlays
    ADD CONSTRAINT fk_branch_overlays_repository
    FOREIGN KEY IF NOT EXISTS (repository) REFERENCES repositories(name) ON DELETE CASCADE;

-- MCP tools that named a repository "project" are renamed. A personal access token in selected mode
-- holds an allow-list of tool names, so carry each grant to the new name (restart-safe: the copy
-- skips what exists, the delete is a no-op once done), then retire the old catalog rows. The
-- audit tables keep the names the calls were made under.
INSERT INTO mcp_personal_access_token_tool_entitlements (token_id, tool_name, created_at)
SELECT old_grant.token_id, renamed.new_name, old_grant.created_at
FROM mcp_personal_access_token_tool_entitlements AS old_grant
JOIN (
    SELECT 'list_projects' AS old_name, 'list_repositories' AS new_name
    UNION ALL SELECT 'search_projects', 'search_repositories'
    UNION ALL SELECT 'project_report', 'repository_report'
    UNION ALL SELECT 'get_project_health', 'get_repository_health'
) AS renamed ON renamed.old_name = old_grant.tool_name
WHERE NOT EXISTS (
    SELECT 1
    FROM mcp_personal_access_token_tool_entitlements AS existing
    WHERE existing.token_id = old_grant.token_id
      AND existing.tool_name = renamed.new_name
);

DELETE FROM mcp_personal_access_token_tool_entitlements
WHERE tool_name IN ('list_projects', 'search_projects', 'project_report', 'get_project_health');

-- The hub catalog's admin-owned state (enabled, default_selected, access_class) is keyed by tool
-- name, so carry each old row to its new name before retiring it. Otherwise McpHubCatalogSeeder
-- would seed the renamed tools with defaults and re-enable a tool an admin had disabled. The
-- seeder's upsert preserves these admin-owned columns for an existing row and refreshes the
-- system-owned ones.
INSERT INTO mcp_hub_tools (
    tool_name, provider_key, provider_type, display_name, description, read_only, destructive,
    enabled, is_available, default_selected, access_class, requires_credential, input_schema,
    created_at_utc, updated_at_utc)
SELECT renamed.new_name, old_tool.provider_key, old_tool.provider_type, old_tool.display_name,
       old_tool.description, old_tool.read_only, old_tool.destructive, old_tool.enabled,
       old_tool.is_available, old_tool.default_selected, old_tool.access_class,
       old_tool.requires_credential, old_tool.input_schema, old_tool.created_at_utc,
       old_tool.updated_at_utc
FROM mcp_hub_tools AS old_tool
JOIN (
    SELECT 'list_projects' AS old_name, 'list_repositories' AS new_name
    UNION ALL SELECT 'search_projects', 'search_repositories'
    UNION ALL SELECT 'project_report', 'repository_report'
    UNION ALL SELECT 'get_project_health', 'get_repository_health'
) AS renamed ON renamed.old_name = old_tool.tool_name
WHERE NOT EXISTS (
    SELECT 1
    FROM mcp_hub_tools AS existing
    WHERE existing.tool_name = renamed.new_name
);

DELETE FROM mcp_hub_tools
WHERE tool_name IN ('list_projects', 'search_projects', 'project_report', 'get_project_health');
