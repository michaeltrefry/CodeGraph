-- Fix: MySQL treats NULL != NULL in unique keys, so ON DUPLICATE KEY UPDATE
-- never fires for repo-level rows (dotnet_project IS NULL). Each re-index
-- inserted a new row instead of updating, causing unbounded growth.
-- Fix: use empty string '' as the sentinel for repo-level entries.

-- ── project_health_summaries ─────────────────────────────────────────────

-- Keep only the most recent row per (project, NULL) group
DELETE phs FROM project_health_summaries phs
INNER JOIN (
    SELECT project, MAX(id) AS keep_id
    FROM project_health_summaries
    WHERE dotnet_project IS NULL
    GROUP BY project
) latest ON phs.project = latest.project
    AND phs.dotnet_project IS NULL
    AND phs.id <> latest.keep_id;

-- Convert remaining NULLs to empty string
UPDATE project_health_summaries SET dotnet_project = '' WHERE dotnet_project IS NULL;

-- Make column NOT NULL with empty-string default
ALTER TABLE project_health_summaries
    MODIFY COLUMN dotnet_project VARCHAR(255) NOT NULL DEFAULT '';

-- ── project_health_analyses ──────────────────────────────────────────────

-- Keep only the most recent row per (project, NULL) group
DELETE pha FROM project_health_analyses pha
INNER JOIN (
    SELECT project, MAX(id) AS keep_id
    FROM project_health_analyses
    WHERE dotnet_project IS NULL
    GROUP BY project
) latest ON pha.project = latest.project
    AND pha.dotnet_project IS NULL
    AND pha.id <> latest.keep_id;

-- Convert remaining NULLs to empty string
UPDATE project_health_analyses SET dotnet_project = '' WHERE dotnet_project IS NULL;

-- Make column NOT NULL with empty-string default
ALTER TABLE project_health_analyses
    MODIFY COLUMN dotnet_project VARCHAR(255) NOT NULL DEFAULT '';
