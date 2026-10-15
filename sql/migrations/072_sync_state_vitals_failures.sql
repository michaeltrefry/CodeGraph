-- Bounded vitals retries (sc-26138): a vitals computation that keeps failing must not be retried by
-- every routine sync of an unchanged HEAD.
-- sync_state.vitals_failed_commit_sha / vitals_failed_version / vitals_consecutive_failures: the commit
--   and VitalsAnalyzer.Version whose vitals computation has been failing, and how many times in a row.
--   Routine syncs stop vitals-only refreshes at IndexingOptions.MaxConsecutiveIndexFailures until the
--   commit or the vitals version changes; a successful computation clears the record.
ALTER TABLE sync_state
    ADD COLUMN IF NOT EXISTS vitals_failed_commit_sha VARCHAR(64) NULL;

ALTER TABLE sync_state
    ADD COLUMN IF NOT EXISTS vitals_failed_version INT NULL;

ALTER TABLE sync_state
    ADD COLUMN IF NOT EXISTS vitals_consecutive_failures INT NOT NULL DEFAULT 0;
