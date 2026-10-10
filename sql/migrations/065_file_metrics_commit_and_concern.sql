-- Per-file hotspot readings name the commit they were computed at (sc-25735).
-- commit_sha: the default-branch commit (checkout HEAD) the vitals run read history, complexity
--   and lint from. NULL for rows computed before this migration (and for non-git folder sources);
--   readers must report it as unknown rather than substituting the current index commit.
-- concern_score: the composite concern score VitalsAnalyzer computes was never persisted on
--   MariaDB (it was always read back as 0). Rows computed before this migration keep the 0 default
--   until the next vitals run; readers treat a row without commit_sha as having no stored concern.
ALTER TABLE file_metrics
    ADD COLUMN IF NOT EXISTS concern_score DOUBLE NOT NULL DEFAULT 0 AFTER risk_score,
    ADD COLUMN IF NOT EXISTS commit_sha VARCHAR(64) NULL AFTER computed_at;
