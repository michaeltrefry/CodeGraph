-- Project knowledge store (sc-27869, epic 27864 "CodeGraph agent coordination"; design §5.3, §5.8, §10.3).
-- 076 and 077 belong to sibling stories of the epic.
--
-- A knowledge entry is one goal, decision, constraint, fact, gotcha, attempt, architecture note or
-- lesson, scoped to a Project (required) and optionally to repositories, paths/globs and graph
-- nodes. It is written synchronously by record or by the save-point fields of update_work and
-- end_session, attributed to its session or person, and never edited in place: every write adds a
-- revision (knowledge_revisions) and an entry leaves the active set only by being superseded or
-- retracted. Freshness: knowledge_entry_files holds the indexed content hash (file_hashes) of every
-- file the entry was verified against; an entry reads possibly_stale while any of those files has a
-- different hash or is no longer indexed. A file not yet indexed at write time is stored with a
-- NULL hash and baselined by the first read after it is indexed.
--
-- Actor columns follow the work tables: the authenticated identity, author_kind user or session,
-- and for a session its id, harness, model and host. Enum values are stored as snake_case wire names.

CREATE TABLE IF NOT EXISTS knowledge_entries (
    id CHAR(36) NOT NULL,
    project_id CHAR(36) NOT NULL,
    kind VARCHAR(16) NOT NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'active',
    summary VARCHAR(500) NOT NULL,
    -- SHA-256 (lowercase hex) of the normalized summary: the exact-match dedup key of imports.
    summary_hash CHAR(64) NOT NULL,
    body MEDIUMTEXT NULL,
    -- Kind-specific fields (decision: why, alternatives, context, consequences; attempt: why_failed;
    -- lesson: what, applies_when, source) as a JSON object.
    details_json LONGTEXT NULL,
    confidence VARCHAR(8) NOT NULL,
    made_autonomously TINYINT(1) NULL,
    -- Lesson filters, copied from details_json so recall can filter in SQL: a comma list of
    -- applies-when activities and the correction's source kind.
    activities VARCHAR(255) NULL,
    source_kind VARCHAR(16) NULL,
    item_id CHAR(36) NULL,
    feature_id CHAR(36) NULL,
    supersedes_id CHAR(36) NULL,
    superseded_by_id CHAR(36) NULL,
    status_reason VARCHAR(1000) NULL,
    -- Native-memory import: the file the entry came from.
    source_file VARCHAR(1024) NULL,
    verified_commit VARCHAR(64) NULL,
    verified_at DATETIME(6) NOT NULL,
    revision INT NOT NULL DEFAULT 1,
    version BIGINT NOT NULL DEFAULT 1,
    author VARCHAR(255) NOT NULL,
    author_kind VARCHAR(16) NOT NULL DEFAULT 'user',
    author_session_id VARCHAR(64) NULL,
    author_harness VARCHAR(64) NULL,
    author_model VARCHAR(128) NULL,
    author_host VARCHAR(255) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_knowledge_entries_project (project_id, status, created_at, id),
    KEY ix_knowledge_entries_dedup (project_id, kind, summary_hash),
    KEY ix_knowledge_entries_item (item_id),
    CONSTRAINT fk_knowledge_entries_project FOREIGN KEY (project_id) REFERENCES work_projects(id) ON DELETE CASCADE,
    CONSTRAINT fk_knowledge_entries_item FOREIGN KEY (item_id) REFERENCES work_items(id) ON DELETE SET NULL,
    CONSTRAINT fk_knowledge_entries_feature FOREIGN KEY (feature_id) REFERENCES work_features(id) ON DELETE SET NULL
) ENGINE=InnoDB;

-- The repositories an entry is scoped to (members of its project). None: the whole project.
CREATE TABLE IF NOT EXISTS knowledge_entry_repositories (
    entry_id CHAR(36) NOT NULL,
    repository_name VARCHAR(255) NOT NULL,
    PRIMARY KEY (entry_id, repository_name),
    KEY ix_knowledge_entry_repositories_repository (repository_name),
    CONSTRAINT fk_knowledge_entry_repositories_entry FOREIGN KEY (entry_id) REFERENCES knowledge_entries(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- The paths or globs an entry is scoped to, relative to the repository root with forward slashes.
-- prefix is the literal part before the first wildcard (the whole path when there is none).
CREATE TABLE IF NOT EXISTS knowledge_entry_paths (
    entry_id CHAR(36) NOT NULL,
    path VARCHAR(500) NOT NULL,
    prefix VARCHAR(500) NOT NULL,
    PRIMARY KEY (entry_id, path),
    KEY ix_knowledge_entry_paths_prefix (prefix(255)),
    CONSTRAINT fk_knowledge_entry_paths_entry FOREIGN KEY (entry_id) REFERENCES knowledge_entries(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- The graph nodes an entry is scoped to. The node's repository and qualified name are kept, so a
-- node re-created by a later index (a new id) still matches.
CREATE TABLE IF NOT EXISTS knowledge_entry_nodes (
    entry_id CHAR(36) NOT NULL,
    node_id BIGINT NOT NULL,
    repository_name VARCHAR(255) NOT NULL,
    qualified_name VARCHAR(1000) NOT NULL,
    file_path VARCHAR(500) NULL,
    PRIMARY KEY (entry_id, node_id),
    KEY ix_knowledge_entry_nodes_name (repository_name, qualified_name(255)),
    CONSTRAINT fk_knowledge_entry_nodes_entry FOREIGN KEY (entry_id) REFERENCES knowledge_entries(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- The files an entry was verified against, with their indexed content hash at verification.
CREATE TABLE IF NOT EXISTS knowledge_entry_files (
    id BIGINT NOT NULL AUTO_INCREMENT,
    entry_id CHAR(36) NOT NULL,
    repository_name VARCHAR(255) NOT NULL,
    path VARCHAR(500) NOT NULL,
    content_hash VARCHAR(64) NULL,
    PRIMARY KEY (id),
    KEY ix_knowledge_entry_files_entry (entry_id),
    KEY ix_knowledge_entry_files_file (repository_name, path),
    CONSTRAINT fk_knowledge_entry_files_entry FOREIGN KEY (entry_id) REFERENCES knowledge_entries(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- Every write of an entry, as a snapshot of the entry after it. Never updated.
CREATE TABLE IF NOT EXISTS knowledge_revisions (
    entry_id CHAR(36) NOT NULL,
    revision INT NOT NULL,
    action VARCHAR(16) NOT NULL,
    snapshot_json LONGTEXT NOT NULL,
    author VARCHAR(255) NOT NULL,
    author_kind VARCHAR(16) NOT NULL DEFAULT 'user',
    author_session_id VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (entry_id, revision),
    CONSTRAINT fk_knowledge_revisions_entry FOREIGN KEY (entry_id) REFERENCES knowledge_entries(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- D17 / R33: the existing memory tools leave the default MCP listing. They become explicit-only
-- (McpHubExplicitEntitlementPolicy): an all-mode token no longer lists or calls them, a token that
-- selected them keeps them. They are no longer pre-checked when a token is created. Their data and
-- catalog rows are kept.
UPDATE mcp_hub_tools SET default_selected = FALSE
WHERE tool_name IN (
    'store_memory_v2', 'get_memory_write_status', 'query_memory', 'search_memory', 'get_memory_subgraph',
    'get_entity_bundle', 'get_claim_bundle', 'expand_memory_frontier', 'render_memory_summary',
    'memory_read', 'memory_store', 'memory_diagnostics');
