-- Add do_not_trust flag to nodes (persists across re-indexing)
ALTER TABLE nodes
    ADD COLUMN do_not_trust BOOLEAN NOT NULL DEFAULT FALSE AFTER properties;

CREATE INDEX idx_nodes_do_not_trust ON nodes (project, do_not_trust);
