-- Denormalised attribution (sc-27882, epic 27864 E3). Every row that records the session behind a
-- write also needs the session's harness, model and host once that session row is pruned. The prune
-- job copies them from work_sessions onto the rows that name a session in the same transaction that
-- deletes it; reads use COALESCE(live session value, the row's own value). Rows therefore carry NULL
-- here until their session is pruned. Restart-safe: columns and indexes are created if missing.

ALTER TABLE work_projects
    ADD COLUMN IF NOT EXISTS created_harness VARCHAR(64) NULL,
    ADD COLUMN IF NOT EXISTS created_model VARCHAR(128) NULL,
    ADD COLUMN IF NOT EXISTS created_host VARCHAR(255) NULL;
ALTER TABLE work_features
    ADD COLUMN IF NOT EXISTS created_harness VARCHAR(64) NULL,
    ADD COLUMN IF NOT EXISTS created_model VARCHAR(128) NULL,
    ADD COLUMN IF NOT EXISTS created_host VARCHAR(255) NULL;
ALTER TABLE work_items
    ADD COLUMN IF NOT EXISTS created_harness VARCHAR(64) NULL,
    ADD COLUMN IF NOT EXISTS created_model VARCHAR(128) NULL,
    ADD COLUMN IF NOT EXISTS created_host VARCHAR(255) NULL;
ALTER TABLE work_changes
    ADD COLUMN IF NOT EXISTS actor_harness VARCHAR(64) NULL,
    ADD COLUMN IF NOT EXISTS actor_model VARCHAR(128) NULL,
    ADD COLUMN IF NOT EXISTS actor_host VARCHAR(255) NULL;
ALTER TABLE work_handoffs
    ADD COLUMN IF NOT EXISTS author_harness VARCHAR(64) NULL,
    ADD COLUMN IF NOT EXISTS author_model VARCHAR(128) NULL,
    ADD COLUMN IF NOT EXISTS author_host VARCHAR(255) NULL;
ALTER TABLE work_item_comments
    ADD COLUMN IF NOT EXISTS author_harness VARCHAR(64) NULL,
    ADD COLUMN IF NOT EXISTS author_model VARCHAR(128) NULL,
    ADD COLUMN IF NOT EXISTS author_host VARCHAR(255) NULL;
ALTER TABLE work_messages
    ADD COLUMN IF NOT EXISTS sender_harness VARCHAR(64) NULL,
    ADD COLUMN IF NOT EXISTS sender_model VARCHAR(128) NULL,
    ADD COLUMN IF NOT EXISTS sender_host VARCHAR(255) NULL,
    ADD COLUMN IF NOT EXISTS resolved_by_harness VARCHAR(64) NULL,
    ADD COLUMN IF NOT EXISTS resolved_by_model VARCHAR(128) NULL,
    ADD COLUMN IF NOT EXISTS resolved_by_host VARCHAR(255) NULL;
ALTER TABLE work_message_events
    ADD COLUMN IF NOT EXISTS actor_harness VARCHAR(64) NULL,
    ADD COLUMN IF NOT EXISTS actor_model VARCHAR(128) NULL,
    ADD COLUMN IF NOT EXISTS actor_host VARCHAR(255) NULL;
ALTER TABLE work_attempts
    ADD COLUMN IF NOT EXISTS harness VARCHAR(64) NULL,
    ADD COLUMN IF NOT EXISTS model VARCHAR(128) NULL,
    ADD COLUMN IF NOT EXISTS host VARCHAR(255) NULL;
ALTER TABLE work_holds
    ADD COLUMN IF NOT EXISTS harness VARCHAR(64) NULL,
    ADD COLUMN IF NOT EXISTS model VARCHAR(128) NULL,
    ADD COLUMN IF NOT EXISTS host VARCHAR(255) NULL;
ALTER TABLE knowledge_revisions
    ADD COLUMN IF NOT EXISTS author_harness VARCHAR(64) NULL,
    ADD COLUMN IF NOT EXISTS author_model VARCHAR(128) NULL,
    ADD COLUMN IF NOT EXISTS author_host VARCHAR(255) NULL;

-- Session lookups of the prune job (081 covers the older tables).
CREATE INDEX IF NOT EXISTS ix_work_messages_sender_session ON work_messages (sender_session_id);
CREATE INDEX IF NOT EXISTS ix_work_messages_resolver_session ON work_messages (resolved_by_session_id);
CREATE INDEX IF NOT EXISTS ix_work_message_events_actor_session ON work_message_events (actor_session_id);
CREATE INDEX IF NOT EXISTS ix_work_attempts_session ON work_attempts (session_id);
CREATE INDEX IF NOT EXISTS ix_knowledge_revisions_author_session ON knowledge_revisions (author_session_id);
CREATE INDEX IF NOT EXISTS ix_work_message_deliveries_recipient ON work_message_deliveries (recipient);
