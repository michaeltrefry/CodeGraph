-- Work tracking core (sc-27865, epic 27864 "CodeGraph agent coordination").
-- Ported from KanbanBoard (docs/mariadb-schema.md there) onto CodeGraph's own schema:
-- multi-repository Projects, features (the epic's successor) with documents, items with
-- per-project speakable keys (SW-142, SW-F12), dependencies, advisory claims, links, comments,
-- idempotency keys and one ordered change feed over projects, features and items.
--
-- Lock order, as in KanbanBoard: the named lock of a dependency add or of next_work (taken before
-- the transaction), then the project's row, then rows of work_items (several at once in key
-- order), then the named lock of the change feed, which is taken last for the INSERT of the
-- entries and the COMMIT only.
--
-- Actor columns hold the authenticated identity (the PAT username). The *_session_id columns
-- stay NULL until sessions exist (S2), so S2 adds a session without a schema rewrite.
-- Statuses, types and priorities are stored as their snake_case wire names.

CREATE TABLE IF NOT EXISTS work_projects (
    id CHAR(36) NOT NULL,
    project_key VARCHAR(10) NOT NULL,
    name VARCHAR(160) NOT NULL,
    description MEDIUMTEXT NULL,
    goals_json LONGTEXT NOT NULL,
    -- The next free item / feature number, advanced while the creating transaction holds this
    -- row, so numbers are allocated one writer at a time and roll back with a failed create.
    next_item_number INT NOT NULL DEFAULT 1,
    next_feature_number INT NOT NULL DEFAULT 1,
    version BIGINT NOT NULL DEFAULT 1,
    created_by VARCHAR(255) NOT NULL,
    created_by_kind VARCHAR(16) NOT NULL DEFAULT 'user',
    created_session_id VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_work_projects_key (project_key)
) ENGINE=InnoDB;

-- A repository (repositories.name) is a member of a project as owned or dependency. It may be
-- owned by at most one project: owned_repository is NULL for a dependency, and a unique key
-- admits any number of NULLs. No foreign key to repositories: a repository that stops being
-- indexed must not silently drop out of a project behind the change feed.
CREATE TABLE IF NOT EXISTS work_project_repositories (
    project_id CHAR(36) NOT NULL,
    repository_name VARCHAR(255) NOT NULL,
    role VARCHAR(16) NOT NULL,
    owned_repository VARCHAR(255) AS (IF(role = 'owned', repository_name, NULL)) PERSISTENT,
    position INT NOT NULL DEFAULT 0,
    PRIMARY KEY (project_id, repository_name),
    UNIQUE KEY ux_work_project_repositories_owned (owned_repository),
    KEY ix_work_project_repositories_repository (repository_name),
    CONSTRAINT fk_work_project_repositories_project FOREIGN KEY (project_id) REFERENCES work_projects(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS work_features (
    id CHAR(36) NOT NULL,
    project_id CHAR(36) NOT NULL,
    number INT NOT NULL,
    title VARCHAR(240) NOT NULL,
    description LONGTEXT NULL,
    version BIGINT NOT NULL DEFAULT 1,
    created_by VARCHAR(255) NOT NULL,
    created_by_kind VARCHAR(16) NOT NULL DEFAULT 'user',
    created_session_id VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_work_features_number (project_id, number),
    CONSTRAINT fk_work_features_project FOREIGN KEY (project_id) REFERENCES work_projects(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS work_feature_documents (
    id CHAR(36) NOT NULL,
    feature_id CHAR(36) NOT NULL,
    position INT NOT NULL,
    title VARCHAR(200) NOT NULL,
    body LONGTEXT NOT NULL,
    created_by VARCHAR(255) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_work_feature_documents_feature (feature_id, position),
    CONSTRAINT fk_work_feature_documents_feature FOREIGN KEY (feature_id) REFERENCES work_features(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- An item is available to next_work when status = 'ready', every blocker is 'complete' (closed
-- does not satisfy a dependency) and it has no live claim. priority_rank orders the ready queue
-- (critical first), then sort_order, then creation.
CREATE TABLE IF NOT EXISTS work_items (
    id CHAR(36) NOT NULL,
    project_id CHAR(36) NOT NULL,
    number INT NOT NULL,
    feature_id CHAR(36) NULL,
    item_type VARCHAR(16) NOT NULL,
    title VARCHAR(240) NOT NULL,
    description LONGTEXT NULL,
    status VARCHAR(16) NOT NULL,
    priority VARCHAR(16) NOT NULL,
    priority_rank TINYINT AS (CASE priority WHEN 'critical' THEN 0 WHEN 'high' THEN 1 WHEN 'medium' THEN 2 ELSE 3 END) PERSISTENT,
    sort_order INT NOT NULL DEFAULT 0,
    labels VARCHAR(4000) NULL,
    repository_name VARCHAR(255) NULL,
    -- The stored claim: identity + marker + expiry, one winner, advisory. A claim past its expiry
    -- reads as no claim; nothing is written when it lapses.
    claim_holder VARCHAR(255) NULL,
    claim_marker VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    claim_session_id VARCHAR(64) NULL,
    claimed_at DATETIME(6) NULL,
    claim_expires_at DATETIME(6) NULL,
    version BIGINT NOT NULL DEFAULT 1,
    created_by VARCHAR(255) NOT NULL,
    created_by_kind VARCHAR(16) NOT NULL DEFAULT 'user',
    created_session_id VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_work_items_number (project_id, number),
    KEY ix_work_items_queue (project_id, status, priority_rank, sort_order, created_at),
    KEY ix_work_items_feature (feature_id),
    KEY ix_work_items_claim (project_id, claim_holder, claim_marker),
    CONSTRAINT fk_work_items_project FOREIGN KEY (project_id) REFERENCES work_projects(id) ON DELETE CASCADE,
    CONSTRAINT fk_work_items_feature FOREIGN KEY (feature_id) REFERENCES work_features(id)
) ENGINE=InnoDB;

-- One row per edge: blocked_item_id is blocked by blocker_item_id.
CREATE TABLE IF NOT EXISTS work_item_dependencies (
    blocked_item_id CHAR(36) NOT NULL,
    blocker_item_id CHAR(36) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (blocked_item_id, blocker_item_id),
    KEY ix_work_item_dependencies_blocker (blocker_item_id),
    CONSTRAINT fk_work_item_dependencies_blocked FOREIGN KEY (blocked_item_id) REFERENCES work_items(id) ON DELETE CASCADE,
    CONSTRAINT fk_work_item_dependencies_blocker FOREIGN KEY (blocker_item_id) REFERENCES work_items(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS work_item_comments (
    id CHAR(36) NOT NULL,
    item_id CHAR(36) NOT NULL,
    author VARCHAR(255) NOT NULL,
    author_kind VARCHAR(16) NOT NULL DEFAULT 'user',
    author_session_id VARCHAR(64) NULL,
    body LONGTEXT NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_work_item_comments_item (item_id, created_at),
    CONSTRAINT fk_work_item_comments_item FOREIGN KEY (item_id) REFERENCES work_items(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- url can exceed an index key under utf8mb4 and the default collation ignores case, so the
-- unique key and the exact lookup by URL use url_hash, the lowercase-hex SHA-256 of url.
CREATE TABLE IF NOT EXISTS work_item_links (
    id CHAR(36) NOT NULL,
    item_id CHAR(36) NOT NULL,
    kind VARCHAR(16) NOT NULL,
    url VARCHAR(2048) NOT NULL,
    url_hash CHAR(64) NOT NULL,
    created_by VARCHAR(255) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_work_item_links_url (item_id, kind, url_hash),
    KEY ix_work_item_links_url_hash (url_hash),
    CONSTRAINT fk_work_item_links_item FOREIGN KEY (item_id) REFERENCES work_items(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- Idempotency keys of creates, plan imports and next_work allocations. key_hash is the
-- lowercase-hex SHA-256 of the caller's identity and key (the key itself is not stored). A key
-- belongs to one identity, one operation and one scope (the project; the nil GUID for project
-- creates). A repeat answers from entity_id (the object as it is now) or result_json (a plan:
-- the ids the first import answered).
CREATE TABLE IF NOT EXISTS work_idempotency_keys (
    scope_id CHAR(36) NOT NULL,
    operation VARCHAR(16) NOT NULL,
    key_hash CHAR(64) NOT NULL,
    entity_id CHAR(36) NULL,
    result_json LONGTEXT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (scope_id, operation, key_hash)
) ENGINE=InnoDB;

-- The change feed behind `changes`: one row per write to a project, feature or item. Rows are
-- inserted and committed while the writer holds the change-feed named lock (scoped to the
-- database, see MySqlWorkStore.ServerLockName), so a higher sequence is always committed later
-- and a reader's cursor never skips a row. No foreign keys: entries outlive what they describe.
-- Never updated or deleted.
CREATE TABLE IF NOT EXISTS work_changes (
    sequence BIGINT NOT NULL AUTO_INCREMENT,
    project_id CHAR(36) NOT NULL,
    project_key VARCHAR(10) NOT NULL,
    entity_kind VARCHAR(16) NOT NULL,
    entity_id CHAR(36) NOT NULL,
    entity_key VARCHAR(32) NOT NULL,
    kind VARCHAR(32) NOT NULL,
    actor_name VARCHAR(255) NOT NULL,
    actor_kind VARCHAR(16) NOT NULL,
    actor_session_id VARCHAR(64) NULL,
    changes LONGTEXT NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (sequence),
    KEY ix_work_changes_project (project_id, sequence)
) ENGINE=InnoDB;
