-- Vitals versioning (sc-26138): vitals improvements (source-extension coverage, complexity rules) must
-- reach repositories whose HEAD has not moved.
-- sync_state.vitals_version: VitalsAnalyzer.Version of the last completed vitals computation. Routine
--   (SkipIfUpToDate) syncs recompute vitals only (no re-extraction, no LLM analysis) at an unchanged
--   HEAD when it differs from the running analyzer's version; NULL (every row before this migration)
--   counts as different, so each repository's vitals are recomputed once after deploy.
ALTER TABLE sync_state
    ADD COLUMN IF NOT EXISTS vitals_version INT NULL;
