-- GitHub evidence on work items (agent coordination S6, sc-27870).

-- External aliases of an item (an imported Shortcut story keeps its `sc-27866`): resolvable like a
-- key, and matched in branch names, PR titles and PR bodies. Stored lower-case; an alias names
-- one item. S12 (the importer) fills it.
CREATE TABLE IF NOT EXISTS work_item_aliases (
    item_id CHAR(36) NOT NULL,
    alias VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci NOT NULL,
    created_by VARCHAR(255) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (item_id, alias),
    UNIQUE KEY ux_work_item_aliases_alias (alias),
    CONSTRAINT fk_work_item_aliases_item FOREIGN KEY (item_id) REFERENCES work_items(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- Evidence GitHub reported about an item: merges, PRs closed unmerged, reviews and review comments
-- (human or bot, with priority). Kept beside the links; S9 turns review evidence into lessons.
-- evidence_key names the thing reported (a review id, a comment id, a PR's merge), so a repeated
-- or redelivered event updates the row it made instead of adding another. Evidence of a
-- superseded attempt is recorded (superseded = 1) but moved nothing.
CREATE TABLE IF NOT EXISTS work_github_evidence (
    id CHAR(36) NOT NULL,
    item_id CHAR(36) NOT NULL,
    kind VARCHAR(24) NOT NULL,
    evidence_key VARCHAR(100) NOT NULL,
    repository VARCHAR(255) NULL,
    pr_number INT NULL,
    url VARCHAR(2048) NULL,
    external_id VARCHAR(64) NULL,
    author_login VARCHAR(255) NOT NULL,
    source VARCHAR(8) NOT NULL,
    priority VARCHAR(8) NOT NULL,
    review_state VARCHAR(24) NULL,
    location VARCHAR(500) NULL,
    body MEDIUMTEXT NULL,
    attempt_id CHAR(36) NULL,
    superseded TINYINT(1) NOT NULL DEFAULT 0,
    delivery_id VARCHAR(100) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_work_github_evidence_key (item_id, evidence_key),
    KEY ix_work_github_evidence_item (item_id, created_at),
    CONSTRAINT fk_work_github_evidence_item FOREIGN KEY (item_id) REFERENCES work_items(id) ON DELETE CASCADE
) ENGINE=InnoDB;
