-- Indexes for the session pruning job (sc-27882, epic 27864). The job deletes a session only when no
-- durable row still names it, so each column that records a session id needs an index to answer
-- "does anything reference this session?" without scanning the change feed or the item table.
CREATE INDEX IF NOT EXISTS ix_work_changes_actor_session ON work_changes (actor_session_id);
CREATE INDEX IF NOT EXISTS ix_work_handoffs_author_session ON work_handoffs (author_session_id);
CREATE INDEX IF NOT EXISTS ix_work_item_comments_author_session ON work_item_comments (author_session_id);
CREATE INDEX IF NOT EXISTS ix_work_items_created_session ON work_items (created_session_id);
CREATE INDEX IF NOT EXISTS ix_work_features_created_session ON work_features (created_session_id);
CREATE INDEX IF NOT EXISTS ix_work_projects_created_session ON work_projects (created_session_id);
