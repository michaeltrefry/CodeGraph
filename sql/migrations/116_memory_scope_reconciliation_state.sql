-- CG-172: the daily memory scope reconciliation scans only what changed since its last successful run.
--
-- memory_scope_backfill_pending: the repositories of a project whose full backfill (CG-167) has not yet
-- succeeded. A project create or update writes its added repositories here in its own transaction; the
-- backfill worker removes them when their backfill succeeds and records the error when it fails. The
-- reconciliation runs a full backfill for every row left (failed, dropped by a full queue, or lost to a
-- restart). repositories_json is a JSON array of repository names. Keyed by project_key (it cannot be
-- changed); a deleted project's row is cleared by the next reconciliation.
CREATE TABLE IF NOT EXISTS memory_scope_backfill_pending (
    project_key VARCHAR(10) NOT NULL PRIMARY KEY,
    repositories_json LONGTEXT NOT NULL,
    enqueued_at DATETIME(6) NOT NULL,
    attempts INT NOT NULL DEFAULT 0,
    last_attempt_at DATETIME(6) NULL,
    last_error VARCHAR(2000) NULL
) ENGINE=InnoDB;

-- memory_scope_watermark: the single row (id 1) of the highest memory_entities_v2.scope_seq,
-- memory_claims.scope_seq (migration 117) and memory_evidence.id the last successful reconciliation
-- scanned. Memory above it is scanned, for every project, by the next run; absent until the first run,
-- which records the current marks without a scan.
CREATE TABLE IF NOT EXISTS memory_scope_watermark (
    id TINYINT NOT NULL PRIMARY KEY,
    entity_seq BIGINT NOT NULL,
    claim_seq BIGINT NOT NULL,
    evidence_id BIGINT NOT NULL,
    updated_at DATETIME(6) NOT NULL
) ENGINE=InnoDB;

-- Every existing project's repositories are pending once, so the first reconciliation after this
-- release runs the full backfill the daily job ran until now, one last time: memory written since the
-- previous full run, and any create/add backfill that failed since, are covered before the job turns
-- incremental. Idempotent: a project already pending keeps its row.
INSERT IGNORE INTO memory_scope_backfill_pending (project_key, repositories_json, enqueued_at, attempts)
SELECT p.project_key,
       CONCAT('[', GROUP_CONCAT(JSON_QUOTE(r.repository_name) ORDER BY r.position, r.repository_name SEPARATOR ','), ']'),
       UTC_TIMESTAMP(6),
       0
FROM work_projects p
JOIN work_project_repositories r ON r.project_id = p.id
GROUP BY p.id, p.project_key;
