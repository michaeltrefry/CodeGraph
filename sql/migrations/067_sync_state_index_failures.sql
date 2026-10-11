-- Repeated indexing failure tracking (sc-25698).
-- failed_commit_sha: the commit whose indexing keeps failing; NULL once a run succeeds.
-- consecutive_failures: failed runs of failed_commit_sha in a row. Once it reaches
--   CodeGraph:IndexingOptions:MaxConsecutiveIndexFailures, routine (SkipIfUpToDate) syncs skip that
--   commit until HEAD moves or a manual reindex runs, so one poison repository cannot re-index forever.
-- last_failure_at: when the most recent failure was recorded.
ALTER TABLE sync_state
    ADD COLUMN IF NOT EXISTS failed_commit_sha VARCHAR(40) NULL,
    ADD COLUMN IF NOT EXISTS consecutive_failures INT NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS last_failure_at DATETIME(3) NULL;
