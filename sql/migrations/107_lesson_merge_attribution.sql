-- Merge journals retain the complete actor independently of a prunable work session.
-- System actors (dreamer) have a run id rather than a work_sessions row, so writes
-- persist their model immediately as well as participating in session prune copying.
ALTER TABLE lesson_merges
    ADD COLUMN IF NOT EXISTS author_harness VARCHAR(64) NULL,
    ADD COLUMN IF NOT EXISTS author_model VARCHAR(128) NULL,
    ADD COLUMN IF NOT EXISTS author_host VARCHAR(255) NULL;

-- The exact merged source revision already persists the actor. Recover old journals
-- from it even when the work session has already been pruned; never infer an actor
-- from a different merge, a later revision, or the target lesson's author.
UPDATE lesson_merges m
JOIN knowledge_revisions r ON r.entry_id = m.source_id
    AND r.revision = JSON_VALUE(m.snapshot_json, '$.source.revision')
    AND r.action = 'merged' AND r.author = m.author AND r.author_kind = m.author_kind
    AND r.author_session_id <=> m.author_session_id
LEFT JOIN work_sessions s ON s.id = m.author_session_id
SET m.author_harness = COALESCE(m.author_harness, r.author_harness, s.harness),
    m.author_model = COALESCE(m.author_model, r.author_model, s.model),
    m.author_host = COALESCE(m.author_host, r.author_host, s.host);

CREATE INDEX IF NOT EXISTS ix_lesson_merges_author_session ON lesson_merges (author_session_id);
