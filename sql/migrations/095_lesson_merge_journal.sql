-- Exact reversible same_as transfers (sc-27965). One journal per source/version;
-- never guess transfers for legacy merges. Kept after unmerge for audit.
CREATE TABLE IF NOT EXISTS lesson_merges (
    source_id CHAR(36) NOT NULL,
    source_version BIGINT NOT NULL,
    target_id CHAR(36) NOT NULL,
    project_id CHAR(36) NOT NULL,
    snapshot_json LONGTEXT NOT NULL,
    author VARCHAR(255) NOT NULL,
    author_kind VARCHAR(16) NOT NULL,
    author_session_id VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    unmerged_at DATETIME(6) NULL,
    PRIMARY KEY (source_id, source_version),
    KEY ix_lesson_merges_target (target_id, unmerged_at),
    CONSTRAINT fk_lesson_merges_source FOREIGN KEY (source_id) REFERENCES knowledge_entries(id) ON DELETE CASCADE,
    CONSTRAINT fk_lesson_merges_target FOREIGN KEY (target_id) REFERENCES knowledge_entries(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- Corrections after a merge retain their own applicability, so unmerge can remove
-- the imported applicability while retaining newly learned target corrections.
ALTER TABLE lesson_events ADD COLUMN IF NOT EXISTS applies_when_json LONGTEXT NULL;
UPDATE lesson_events le JOIN knowledge_entries e ON e.id = le.entry_id
SET le.applies_when_json = JSON_EXTRACT(e.details_json, '$.applies_when')
WHERE le.applies_when_json IS NULL;
