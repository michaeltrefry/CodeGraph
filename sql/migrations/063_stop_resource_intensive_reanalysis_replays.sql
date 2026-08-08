-- A ReAnalyze run created before this hotfix may already be queued for another
-- resource-intensive attempt, or may have been interrupted while such a retry was
-- running. The deployment replaces the sole indexer worker before migrations run,
-- so terminalize those pre-hotfix attempts instead of recovering them.
UPDATE indexer_runs
SET status = 'failed',
    message = 'Re-analysis stopped by the resource-isolation hotfix; submit a new run after reviewing the semantic failure.',
    error_code = COALESCE(error_code, 'reanalyze_stopped_by_resource_hotfix'),
    error = COALESCE(error, 'A prior re-analysis attempt was not replayed because it could monopolize deployment resources.'),
    completed_at = CURRENT_TIMESTAMP(6),
    next_attempt_at = NULL,
    execution_owner = NULL,
    lease_expires_at = NULL,
    heartbeat_at = CURRENT_TIMESTAMP(6)
WHERE operation = 'reanalyze'
  AND status IN ('queued', 'running')
  AND attempt_count > 0;
