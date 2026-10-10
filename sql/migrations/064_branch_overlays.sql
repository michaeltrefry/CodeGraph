-- Branch overlay indexes (sc-25701).
-- An overlay records the graph slice for files that differ between a branch/SHA and the
-- commit of the current default-branch index. Overlay rows never touch nodes/edges, so the
-- default-branch graph is unchanged. Expired overlays keep only their status row; all
-- child rows are deleted by the TTL sweep.
-- Paths and qualified names use utf8mb4_bin: git paths are case-sensitive, so a case-only
-- rename (Foo.cs -> foo.cs) yields a deletion tombstone and a renamed row that must not
-- collide on the (overlay_id, file_path) key.

CREATE TABLE IF NOT EXISTS branch_overlays (
    id BIGINT NOT NULL AUTO_INCREMENT,
    project VARCHAR(255) NOT NULL,
    requested_ref VARCHAR(255) NOT NULL,
    head_sha VARCHAR(40) NULL,
    base_sha VARCHAR(40) NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'queued',
    error TEXT NULL,
    requested_by_username VARCHAR(191) NULL,
    indexer_run_id BIGINT NULL,
    changed_file_count INT NOT NULL DEFAULT 0,
    node_count INT NOT NULL DEFAULT 0,
    edge_count INT NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    started_at DATETIME(6) NULL,
    completed_at DATETIME(6) NULL,
    expired_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    INDEX ix_branch_overlays_project (project, status, created_at),
    INDEX ix_branch_overlays_expiry (status, created_at),
    CONSTRAINT fk_branch_overlays_project FOREIGN KEY (project) REFERENCES repositories(name) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS branch_overlay_files (
    overlay_id BIGINT NOT NULL,
    file_path VARCHAR(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    change_kind VARCHAR(20) NOT NULL,
    previous_path VARCHAR(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    PRIMARY KEY (overlay_id, file_path),
    CONSTRAINT fk_branch_overlay_files_overlay FOREIGN KEY (overlay_id) REFERENCES branch_overlays(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS branch_overlay_nodes (
    id BIGINT NOT NULL AUTO_INCREMENT,
    overlay_id BIGINT NOT NULL,
    dotnet_project VARCHAR(255) NULL,
    label VARCHAR(50) NOT NULL,
    name VARCHAR(255) NOT NULL,
    qualified_name VARCHAR(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    qualified_name_hash BINARY(32) GENERATED ALWAYS AS (UNHEX(SHA2(qualified_name, 256))) STORED,
    file_path VARCHAR(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL DEFAULT '',
    start_line INT NOT NULL DEFAULT 0,
    end_line INT NOT NULL DEFAULT 0,
    properties JSON NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_branch_overlay_nodes_qn (overlay_id, qualified_name_hash),
    INDEX ix_branch_overlay_nodes_file (overlay_id, file_path),
    CONSTRAINT fk_branch_overlay_nodes_overlay FOREIGN KEY (overlay_id) REFERENCES branch_overlays(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS branch_overlay_edges (
    id BIGINT NOT NULL AUTO_INCREMENT,
    overlay_id BIGINT NOT NULL,
    source_qualified_name VARCHAR(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    target_qualified_name VARCHAR(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    type VARCHAR(50) NOT NULL,
    properties JSON NULL,
    PRIMARY KEY (id),
    INDEX ix_branch_overlay_edges_overlay (overlay_id, type),
    CONSTRAINT fk_branch_overlay_edges_overlay FOREIGN KEY (overlay_id) REFERENCES branch_overlays(id) ON DELETE CASCADE
) ENGINE=InnoDB;
