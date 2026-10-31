-- One private continuation per project. Run history remains independently queryable.
-- Transactional project locking and owner CAS validate identity without coupling
-- independent historical project/run identifier collations through foreign keys.
CREATE TABLE IF NOT EXISTS dreaming_checkpoints (
    project_id CHAR(36) NOT NULL PRIMARY KEY,
    run_id CHAR(36) NOT NULL,
    state_json LONGTEXT NOT NULL CHECK (JSON_VALID(state_json)),
    updated_at DATETIME(6) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Null preserves existing callers and historical records. Each committed execution is unique
-- within its project, and the receipt commits in the same transaction as its side effect.
ALTER TABLE dream_write_journal ADD COLUMN IF NOT EXISTS execution_id CHAR(36) NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_dream_journal_execution ON dream_write_journal (project_id, execution_id);
ALTER TABLE dream_proposals ADD COLUMN IF NOT EXISTS execution_id CHAR(36) NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_dream_proposal_execution ON dream_proposals (project_id, execution_id);
