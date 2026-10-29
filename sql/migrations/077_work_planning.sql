-- Feature planning, re-planning and completion (sc-27868, epic 27864 "CodeGraph agent coordination").
--
-- A feature carries goals, numbered requirements (E1..En, each active or dropped with the decision
-- that dropped it) and acceptance tests. An item carries a goal, 1-3 acceptance criteria (each met
-- or unmet, with evidence), the requirements it inherits, the paths and symbols it touches, the ref
-- it has in its feature's plan, the acceptance report it was completed with, and
-- merged_awaiting_acceptance (set from merge evidence; never completes the item).
-- Requirements, criteria, inherits and touches are small JSON lists on their row, so a change to
-- any of them advances the version of the row that holds it: a sync_plan preview bound to the
-- versions of the feature and its items covers them.
--
-- Lock order (extends 073): the dependency named lock, then a plan preview's row (sync_plan
-- confirm only), then the project's row, then the feature's row, then rows of work_items in key
-- order, then the change-feed named lock.

ALTER TABLE work_features ADD COLUMN IF NOT EXISTS goals_json LONGTEXT NULL;
ALTER TABLE work_features ADD COLUMN IF NOT EXISTS requirements_json LONGTEXT NULL;
ALTER TABLE work_features ADD COLUMN IF NOT EXISTS acceptance_tests_json LONGTEXT NULL;

ALTER TABLE work_items ADD COLUMN IF NOT EXISTS plan_ref VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL;
ALTER TABLE work_items ADD COLUMN IF NOT EXISTS goal MEDIUMTEXT NULL;
ALTER TABLE work_items ADD COLUMN IF NOT EXISTS acceptance_json LONGTEXT NULL;
ALTER TABLE work_items ADD COLUMN IF NOT EXISTS inherits_json LONGTEXT NULL;
ALTER TABLE work_items ADD COLUMN IF NOT EXISTS touches_json LONGTEXT NULL;
ALTER TABLE work_items ADD COLUMN IF NOT EXISTS acceptance_report_json LONGTEXT NULL;
ALTER TABLE work_items ADD COLUMN IF NOT EXISTS merged_awaiting_acceptance TINYINT(1) NOT NULL DEFAULT 0;

-- A ref names one item of a feature's plan; sync_plan diffs a revised plan by it.
CREATE UNIQUE INDEX IF NOT EXISTS ux_work_items_plan_ref ON work_items (feature_id, plan_ref);

-- An execution attempt: one holder's claim on an item (session or identity + marker) with the
-- branch, PRs and handoffs made while it was active. Outcome active while the claim is the item's
-- current one; then superseded (a re-plan changed or closed the item), abandoned (released, the
-- session ended, or the claim lapsed and passed on), complete or closed (the item finished).
-- Evidence of a superseded attempt is kept but moves no status (S6 reads attempt_id on links).
CREATE TABLE IF NOT EXISTS work_attempts (
    id CHAR(36) NOT NULL,
    item_id CHAR(36) NOT NULL,
    number INT NOT NULL,
    holder VARCHAR(255) NOT NULL,
    marker VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    session_id VARCHAR(64) NULL,
    branch VARCHAR(255) NULL,
    outcome VARCHAR(16) NOT NULL,
    reason VARCHAR(1000) NULL,
    handoff_id CHAR(36) NULL,
    started_at DATETIME(6) NOT NULL,
    ended_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_work_attempts_number (item_id, number),
    KEY ix_work_attempts_item_outcome (item_id, outcome),
    CONSTRAINT fk_work_attempts_item FOREIGN KEY (item_id) REFERENCES work_items(id) ON DELETE CASCADE
) ENGINE=InnoDB;

ALTER TABLE work_item_links ADD COLUMN IF NOT EXISTS attempt_id CHAR(36) NULL;
ALTER TABLE work_handoffs ADD COLUMN IF NOT EXISTS attempt_id CHAR(36) NULL;
CREATE INDEX IF NOT EXISTS ix_work_item_links_attempt ON work_item_links (attempt_id);

-- A sync_plan preview: the revised plan as read, and the versions of everything it covers (the
-- feature, every item of the feature or named by the plan, and the dependency edges into them).
-- Confirming applies it only while those are unchanged; a confirmed preview answers its stored
-- result again. A preview belongs to the identity that asked for it.
CREATE TABLE IF NOT EXISTS work_plan_previews (
    id CHAR(36) NOT NULL,
    project_id CHAR(36) NOT NULL,
    feature_id CHAR(36) NOT NULL,
    identity VARCHAR(255) NOT NULL,
    plan_json LONGTEXT NOT NULL,
    versions_json LONGTEXT NOT NULL,
    created_at DATETIME(6) NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    applied_at DATETIME(6) NULL,
    result_json LONGTEXT NULL,
    PRIMARY KEY (id),
    KEY ix_work_plan_previews_feature (feature_id, created_at),
    CONSTRAINT fk_work_plan_previews_feature FOREIGN KEY (feature_id) REFERENCES work_features(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- Notices to the holder of an item a re-plan changed (changed_by_replan): delivered once, on the
-- holder's next work-tool response (to the session, or to the identity for a claim without one).
CREATE TABLE IF NOT EXISTS work_notices (
    id CHAR(36) NOT NULL,
    item_id CHAR(36) NOT NULL,
    session_id VARCHAR(64) NULL,
    identity VARCHAR(255) NOT NULL,
    kind VARCHAR(32) NOT NULL,
    body_json LONGTEXT NOT NULL,
    created_at DATETIME(6) NOT NULL,
    delivered_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    KEY ix_work_notices_session (session_id, delivered_at),
    KEY ix_work_notices_identity (identity, delivered_at),
    CONSTRAINT fk_work_notices_item FOREIGN KEY (item_id) REFERENCES work_items(id) ON DELETE CASCADE
) ENGINE=InnoDB;
