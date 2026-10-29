-- Resources and holds (sc-27872, epic 27864 "CodeGraph agent coordination"; design §5.6, §5.9).
--
-- A resource is a named finite thing of a project (gpu:4090-box, runner:dev-mac) with a capacity
-- (default 1) and labels. A hold is one caller's use of one slot of it, with its OWN time to live
-- and its own renewals: it never lapses because the holding session went quiet. A waiter is a hold
-- row in state 'waiting'. Everything here is advisory: nothing in CodeGraph enforces a hold.
--
-- Concurrency: every change to the holds of a resource first takes the resource's row
-- (SELECT ... FOR UPDATE), so capacity accounting, expiry, promotion of waiters and grants of one
-- resource are serialised; resources do not wait for each other.

CREATE TABLE IF NOT EXISTS work_resources (
    id CHAR(36) NOT NULL,
    project_id CHAR(36) NOT NULL,
    name VARCHAR(200) NOT NULL,
    capacity INT NOT NULL DEFAULT 1,
    labels_json LONGTEXT NOT NULL,
    created_by VARCHAR(255) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_work_resources_name (project_id, name),
    CONSTRAINT fk_work_resources_project FOREIGN KEY (project_id) REFERENCES work_projects(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- state: waiting (queued, no slot yet), held (a slot, until expires_at), released, expired,
-- cancelled (a waiter that gave up, was dropped after its wait window, or whose session ended or
-- went silent). A held row whose expires_at has passed is expired whether or not a sweep has
-- written that yet. seq orders waiters first come first served within a priority.
CREATE TABLE IF NOT EXISTS work_holds (
    id CHAR(36) NOT NULL,
    seq BIGINT NOT NULL AUTO_INCREMENT,
    resource_id CHAR(36) NOT NULL,
    project_id CHAR(36) NOT NULL,
    identity VARCHAR(255) NOT NULL,
    session_id VARCHAR(64) NULL,
    item_id CHAR(36) NULL,
    purpose VARCHAR(500) NOT NULL,
    priority VARCHAR(16) NOT NULL DEFAULT 'medium',
    priority_rank TINYINT AS (CASE priority WHEN 'critical' THEN 0 WHEN 'high' THEN 1 WHEN 'medium' THEN 2 ELSE 3 END) PERSISTENT,
    state VARCHAR(16) NOT NULL,
    ttl_seconds INT NOT NULL,
    requested_at DATETIME(6) NOT NULL,
    wait_expires_at DATETIME(6) NULL,
    granted_at DATETIME(6) NULL,
    expires_at DATETIME(6) NULL,
    renewed_at DATETIME(6) NULL,
    ended_at DATETIME(6) NULL,
    key_hash CHAR(64) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_work_holds_seq (seq),
    UNIQUE KEY ux_work_holds_key (project_id, key_hash),
    KEY ix_work_holds_resource (resource_id, state, expires_at),
    KEY ix_work_holds_session (session_id, state),
    KEY ix_work_holds_project (project_id, state),
    CONSTRAINT fk_work_holds_resource FOREIGN KEY (resource_id) REFERENCES work_resources(id) ON DELETE CASCADE,
    CONSTRAINT fk_work_holds_item FOREIGN KEY (item_id) REFERENCES work_items(id) ON DELETE SET NULL
) ENGINE=InnoDB;
