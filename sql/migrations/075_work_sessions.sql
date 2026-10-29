-- Sessions, handoffs and session-bound claims (sc-27866, epic 27864 "CodeGraph agent coordination").
-- 074 is not used: an early revision of sc-27865 shipped a 074 that was dropped before merge, and a
-- migration number is never reused for a different statement set.
--
-- A session is one running agent: the authenticated identity plus harness, model, host, repository,
-- branch, worktree and a free-text intent, in one project. It is live while it is not ended and its
-- last heartbeat (any work-tool call carrying its id) is younger than the configured idle threshold.
-- A claim made with a session is stored with the session id as its marker and claim_session_id set;
-- its claim_expires_at is the session's heartbeat deadline, pushed forward by each heartbeat while
-- the claim is live, so a session that goes quiet loses its claims without anything being written.

CREATE TABLE IF NOT EXISTS work_sessions (
    id VARCHAR(64) NOT NULL,
    project_id CHAR(36) NOT NULL,
    identity VARCHAR(255) NOT NULL,
    harness VARCHAR(64) NOT NULL,
    model VARCHAR(128) NULL,
    host VARCHAR(255) NULL,
    repository_name VARCHAR(255) NULL,
    branch VARCHAR(255) NULL,
    worktree VARCHAR(1024) NULL,
    intent VARCHAR(1000) NULL,
    started_at DATETIME(6) NOT NULL,
    last_seen_at DATETIME(6) NOT NULL,
    ended_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    KEY ix_work_sessions_project (project_id, ended_at, last_seen_at),
    CONSTRAINT fk_work_sessions_project FOREIGN KEY (project_id) REFERENCES work_projects(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- The items a session has claimed and not let go of. A row stays open (released_at NULL) until the
-- session releases the item, finishes it, or ends; a claim that lapsed or passed to another holder
-- leaves it open, and that is how the session's next write on the item is told claim_lost.
CREATE TABLE IF NOT EXISTS work_session_claims (
    session_id VARCHAR(64) NOT NULL,
    item_id CHAR(36) NOT NULL,
    claimed_at DATETIME(6) NOT NULL,
    released_at DATETIME(6) NULL,
    PRIMARY KEY (session_id, item_id),
    KEY ix_work_session_claims_item (item_id),
    CONSTRAINT fk_work_session_claims_session FOREIGN KEY (session_id) REFERENCES work_sessions(id) ON DELETE CASCADE,
    CONSTRAINT fk_work_session_claims_item FOREIGN KEY (item_id) REFERENCES work_items(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- The structured state of an item when a session (or person) stops work on it. Never updated.
CREATE TABLE IF NOT EXISTS work_handoffs (
    id CHAR(36) NOT NULL,
    item_id CHAR(36) NOT NULL,
    author VARCHAR(255) NOT NULL,
    author_kind VARCHAR(16) NOT NULL DEFAULT 'user',
    author_session_id VARCHAR(64) NULL,
    done MEDIUMTEXT NULL,
    decisions_json LONGTEXT NOT NULL,
    open_issues_json LONGTEXT NOT NULL,
    corrections_json LONGTEXT NOT NULL,
    next_action MEDIUMTEXT NULL,
    branch VARCHAR(255) NULL,
    commit_sha VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_work_handoffs_item (item_id, created_at),
    CONSTRAINT fk_work_handoffs_item FOREIGN KEY (item_id) REFERENCES work_items(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- Heartbeats renew the claims of one session.
CREATE INDEX IF NOT EXISTS ix_work_items_claim_session ON work_items (claim_session_id);
