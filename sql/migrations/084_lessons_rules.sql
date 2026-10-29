-- Lessons, rules and challenges (sc-27873, epic 27864 "CodeGraph agent coordination"; design §5.7, §6, R22-R28).
-- 081-083 belong to sibling stories of the epic.
--
-- A lesson is a knowledge entry of kind lesson (078). This adds its learning-loop state:
--   rule_state   candidate (task-scoped), proposed (promote propose), rule (served in briefings when
--                its applies-when matches), rejected (promote reject: not re-proposed, not promoted by
--                recurrence). A lesson from the human's prompt is a rule at once; others become rules
--                by recurrence (Work:Lessons:RuleRecurrenceThreshold distinct events, default 2) or by
--                promote approve. A rule needs an applies-when activity. No time decay: a rule leaves
--                briefings only when superseded, retracted or addressed_by a local skill/hook/test.
--   recurrence   distinct source events (lesson_events.counted), maintained with the events.
--   priority     the best priority of its events: highest (prompt), high (review), normal.
-- Nothing here is decided by a model (E5): merging is the recording agent's same_as or an exact
-- normalized summary match with overlapping applies-when; similarity only lists candidates.
--
-- Lock order: every lesson write holds the project row first (as every knowledge write that may
-- merge does), then knowledge rows (several in ascending id order), then these tables.

ALTER TABLE knowledge_entries ADD COLUMN IF NOT EXISTS rule_state VARCHAR(16) NULL;
ALTER TABLE knowledge_entries ADD COLUMN IF NOT EXISTS rule_reason VARCHAR(16) NULL;
ALTER TABLE knowledge_entries ADD COLUMN IF NOT EXISTS rule_since DATETIME(6) NULL;
ALTER TABLE knowledge_entries ADD COLUMN IF NOT EXISTS rejected_reason VARCHAR(1000) NULL;
ALTER TABLE knowledge_entries ADD COLUMN IF NOT EXISTS recurrence INT NULL;
ALTER TABLE knowledge_entries ADD COLUMN IF NOT EXISTS priority VARCHAR(8) NULL;
-- Review queue (R25a): an autonomous decision acknowledged by someone leaves the queue.
ALTER TABLE knowledge_entries ADD COLUMN IF NOT EXISTS reviewed_at DATETIME(6) NULL;
ALTER TABLE knowledge_entries ADD COLUMN IF NOT EXISTS reviewed_by VARCHAR(255) NULL;
CREATE INDEX IF NOT EXISTS ix_knowledge_entries_rules ON knowledge_entries (project_id, kind, status, rule_state);

-- The activities a session declared (start_session / get_briefing activity): rules are served when
-- one of a rule's applies-when activities is among them. A comma list of snake_case names.
ALTER TABLE work_sessions ADD COLUMN IF NOT EXISTS activities VARCHAR(255) NULL;

-- Every correction recorded into a lesson, as its evidence. correction_id is the id the write gave
-- the correction (the lesson's own id for the first one). event_key identifies the source event
-- (source.ref, else the recording session, else the correction itself); counted is 1 for the first
-- correction of an event on its lesson, 0 for a repeat (a retry, or the same correction reported in
-- update_work and end_session): recurrence counts distinct events. rule_at_event: the lesson was a
-- rule when this correction arrived; served: the rule had been served to the recording session
-- (NULL: no session, or not a rule then).
CREATE TABLE IF NOT EXISTS lesson_events (
    correction_id CHAR(36) NOT NULL,
    entry_id CHAR(36) NOT NULL,
    project_id CHAR(36) NOT NULL,
    event_key VARCHAR(600) NOT NULL,
    event_hash CHAR(64) NOT NULL,
    counted TINYINT(1) NOT NULL,
    rule_at_event TINYINT(1) NOT NULL DEFAULT 0,
    served TINYINT(1) NULL,
    source_kind VARCHAR(16) NULL,
    source_ref VARCHAR(500) NULL,
    summary VARCHAR(500) NOT NULL,
    summary_hash CHAR(64) NOT NULL,
    what MEDIUMTEXT NULL,
    item_id CHAR(36) NULL,
    session_id VARCHAR(64) NULL,
    author VARCHAR(255) NOT NULL,
    author_kind VARCHAR(16) NOT NULL DEFAULT 'user',
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (correction_id),
    KEY ix_lesson_events_entry (entry_id, event_hash),
    KEY ix_lesson_events_session (session_id, summary_hash),
    CONSTRAINT fk_lesson_events_entry FOREIGN KEY (entry_id) REFERENCES knowledge_entries(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- Lessons the recording agent marked as contradicting each other: both are kept, both are served
-- with the conflict shown, and the pair is in the review queue while both are active. One row per
-- direction.
CREATE TABLE IF NOT EXISTS lesson_links (
    entry_id CHAR(36) NOT NULL,
    other_id CHAR(36) NOT NULL,
    relation VARCHAR(16) NOT NULL,
    author VARCHAR(255) NOT NULL,
    author_kind VARCHAR(16) NOT NULL DEFAULT 'user',
    author_session_id VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (entry_id, other_id, relation),
    KEY ix_lesson_links_other (other_id),
    CONSTRAINT fk_lesson_links_entry FOREIGN KEY (entry_id) REFERENCES knowledge_entries(id) ON DELETE CASCADE,
    CONSTRAINT fk_lesson_links_other FOREIGN KEY (other_id) REFERENCES knowledge_entries(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- challenge: an agent disputes an active rule with evidence. The rule stays active and is served
-- with its open challenges; the challenge is in the review queue until resolved upheld, amended or
-- retracted (the outcome is also a revision of the rule). Never blocks anything.
CREATE TABLE IF NOT EXISTS lesson_challenges (
    id CHAR(36) NOT NULL,
    entry_id CHAR(36) NOT NULL,
    project_id CHAR(36) NOT NULL,
    status VARCHAR(16) NOT NULL,
    reason VARCHAR(1000) NOT NULL,
    evidence_json LONGTEXT NOT NULL,
    resolution_reason VARCHAR(1000) NULL,
    author VARCHAR(255) NOT NULL,
    author_kind VARCHAR(16) NOT NULL DEFAULT 'user',
    author_session_id VARCHAR(64) NULL,
    author_harness VARCHAR(64) NULL,
    resolved_by VARCHAR(255) NULL,
    resolved_by_kind VARCHAR(16) NULL,
    resolved_by_session_id VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    resolved_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    KEY ix_lesson_challenges_entry (entry_id, status),
    KEY ix_lesson_challenges_project (project_id, status, created_at),
    CONSTRAINT fk_lesson_challenges_entry FOREIGN KEY (entry_id) REFERENCES knowledge_entries(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- promote addressed_by: the lesson is addressed locally by a skill, instruction, hook, test or CI
-- gate (made outside CodeGraph; CodeGraph only records the link). It then leaves briefings (R27, R28).
CREATE TABLE IF NOT EXISTS lesson_addressed_by (
    entry_id CHAR(36) NOT NULL,
    kind VARCHAR(16) NOT NULL,
    name VARCHAR(255) NOT NULL,
    ref VARCHAR(500) NULL,
    author VARCHAR(255) NOT NULL,
    author_kind VARCHAR(16) NOT NULL DEFAULT 'user',
    author_session_id VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (entry_id, kind, name),
    CONSTRAINT fk_lesson_addressed_by_entry FOREIGN KEY (entry_id) REFERENCES knowledge_entries(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- The served-rule log (R26): which rules each session's briefings served, so a recurrence can be
-- read as with or without its rule served. One row per session and rule.
CREATE TABLE IF NOT EXISTS lesson_served (
    session_id VARCHAR(64) NOT NULL,
    entry_id CHAR(36) NOT NULL,
    project_id CHAR(36) NOT NULL,
    first_served_at DATETIME(6) NOT NULL,
    last_served_at DATETIME(6) NOT NULL,
    times INT NOT NULL DEFAULT 1,
    PRIMARY KEY (session_id, entry_id),
    KEY ix_lesson_served_entry (entry_id, last_served_at),
    CONSTRAINT fk_lesson_served_entry FOREIGN KEY (entry_id) REFERENCES knowledge_entries(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- Lessons stored before this migration: one counted event each; a prompt lesson with an activity is
-- a rule (it would have been one at once), every other lesson a candidate.
INSERT INTO lesson_events (correction_id, entry_id, project_id, event_key, event_hash, counted, rule_at_event, served,
    source_kind, source_ref, summary, summary_hash, what, item_id, session_id, author, author_kind, created_at)
SELECT e.id, e.id, e.project_id, CONCAT('legacy:', e.id), SHA2(CONCAT('legacy:', e.id), 256), 1, 0, NULL,
    e.source_kind, NULL, e.summary, e.summary_hash, NULL, e.item_id, e.author_session_id, e.author, e.author_kind, e.created_at
FROM knowledge_entries e
WHERE e.kind = 'lesson' AND NOT EXISTS (SELECT 1 FROM lesson_events le WHERE le.correction_id = e.id);

UPDATE knowledge_entries
SET recurrence = 1,
    priority = CASE source_kind WHEN 'prompt' THEN 'highest' WHEN 'review' THEN 'high' ELSE 'normal' END,
    rule_state = CASE WHEN source_kind = 'prompt' AND activities IS NOT NULL AND activities <> '' THEN 'rule' ELSE 'candidate' END,
    rule_reason = CASE WHEN source_kind = 'prompt' AND activities IS NOT NULL AND activities <> '' THEN 'human_prompt' ELSE NULL END,
    rule_since = CASE WHEN source_kind = 'prompt' AND activities IS NOT NULL AND activities <> '' THEN created_at ELSE NULL END
WHERE kind = 'lesson' AND rule_state IS NULL;
