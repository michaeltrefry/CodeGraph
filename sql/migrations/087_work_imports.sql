-- Imports into the work tracker (sc-27876, epic 27864, design §10): the Shortcut workspace, the
-- KanbanBoard "SceneWorks Code Review 2026-10-27" epic. Re-runnable and keyed by the source's id, so a
-- second run (or a delta sync by updated_since) updates what the first one wrote instead of
-- duplicating it. Shortcut and KanbanBoard are read only; nothing here is ever written back.

-- An imported feature or item is marked imported (design §10.1: imported items are an explicit
-- exception to the new-item schema and may lack AC, repository, inherits and touches).
-- imported_json: {source, external_id, url, history: {owners, requester, started_at, completed_at, state}}.
ALTER TABLE work_items ADD COLUMN IF NOT EXISTS imported_json LONGTEXT NULL;
ALTER TABLE work_features ADD COLUMN IF NOT EXISTS imported_json LONGTEXT NULL;
-- An open imported feature keeps its requirements in its description until a planning agent
-- extracts E1..En into structured requirements; the flag reads true only while it has none.
ALTER TABLE work_features ADD COLUMN IF NOT EXISTS requirements_unreconciled TINYINT(1) NOT NULL DEFAULT 0;

-- An imported comment keeps its source id, so a re-run inserts only the comments that are new and
-- updates the text of an edited one.
ALTER TABLE work_item_comments ADD COLUMN IF NOT EXISTS external_id VARCHAR(100) NULL;
-- The source's own time of an imported comment. created_at keeps the item's comment order (every
-- comment is later than the ones committed before it), so a comment that arrives in a delta sync
-- with an older source time gets a later created_at and keeps its source time here.
ALTER TABLE work_item_comments ADD COLUMN IF NOT EXISTS source_created_at DATETIME(6) NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_work_item_comments_external ON work_item_comments (item_id, external_id);

-- A PR link an import wrote for a PR that was already merged or closed in the source (a historical
-- PR): GitHub events on it never move the item. When GitHub evidence or an agent adds the same link
-- again, the link changes owner and stops being historical.
ALTER TABLE work_item_links ADD COLUMN IF NOT EXISTS historical TINYINT(1) NOT NULL DEFAULT 0;

-- What each source object became: the idempotency key of every import write. content_hash is the
-- hash of the mapped object, so an unchanged source object writes nothing on a re-run.
-- edges_json: the blockers the import itself added to an item, the only ones a re-run may remove.
-- snapshot_json: the mapped fields as the last run wrote them, the base of the three-way merge of a
-- delta sync (only fields that changed in the source since are written; CodeGraph-side changes to
-- the others survive). relations_json: the blockers the source named at the last dependency sync.
CREATE TABLE IF NOT EXISTS work_import_records (
    source VARCHAR(16) NOT NULL,
    external_kind VARCHAR(16) NOT NULL,
    external_id VARCHAR(64) NOT NULL,
    entity_kind VARCHAR(16) NOT NULL,
    entity_id CHAR(36) NOT NULL,
    project_id CHAR(36) NOT NULL,
    content_hash CHAR(64) NOT NULL,
    edges_json LONGTEXT NULL,
    snapshot_json LONGTEXT NULL,
    relations_json LONGTEXT NULL,
    source_updated_at DATETIME(6) NULL,
    imported_at DATETIME(6) NOT NULL,
    PRIMARY KEY (source, external_kind, external_id),
    KEY ix_work_import_records_entity (entity_id)
) ENGINE=InnoDB;

-- The owner-approved settings of a source: for Shortcut the team -> project table, the holding
-- project and the member map; for KanbanBoard the epic and its target project. An import run is
-- refused until approved_at is set.
CREATE TABLE IF NOT EXISTS work_import_settings (
    source VARCHAR(16) NOT NULL,
    settings_json LONGTEXT NOT NULL,
    approved_by VARCHAR(255) NULL,
    approved_at DATETIME(6) NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (source)
) ENGINE=InnoDB;

-- Every dry run and import run with its report. heartbeat_at moves while a run works, so a run whose
-- host died does not block the next one forever.
CREATE TABLE IF NOT EXISTS work_import_runs (
    id CHAR(36) NOT NULL,
    source VARCHAR(16) NOT NULL,
    mode VARCHAR(16) NOT NULL,
    status VARCHAR(16) NOT NULL,
    started_by VARCHAR(255) NOT NULL,
    updated_since DATETIME(6) NULL,
    report_json LONGTEXT NULL,
    error MEDIUMTEXT NULL,
    started_at DATETIME(6) NOT NULL,
    heartbeat_at DATETIME(6) NOT NULL,
    completed_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    KEY ix_work_import_runs_source (source, started_at)
) ENGINE=InnoDB;

-- Files attached to an item (an imported Shortcut file, downloaded because Shortcut file URLs need
-- Shortcut auth). storage_path is NULL when the file was not stored (over the size bound, or the
-- download failed); skipped_reason then says why and source_url still names it.
CREATE TABLE IF NOT EXISTS work_item_attachments (
    id CHAR(36) NOT NULL,
    item_id CHAR(36) NOT NULL,
    external_id VARCHAR(100) NOT NULL,
    filename VARCHAR(255) NOT NULL,
    content_type VARCHAR(255) NOT NULL,
    size_bytes BIGINT NOT NULL,
    sha256 CHAR(64) NULL,
    storage_path VARCHAR(1024) NULL,
    source_url VARCHAR(2048) NULL,
    skipped_reason VARCHAR(500) NULL,
    created_by VARCHAR(255) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_work_item_attachments_external (item_id, external_id),
    CONSTRAINT fk_work_item_attachments_item FOREIGN KEY (item_id) REFERENCES work_items(id) ON DELETE CASCADE
) ENGINE=InnoDB;
