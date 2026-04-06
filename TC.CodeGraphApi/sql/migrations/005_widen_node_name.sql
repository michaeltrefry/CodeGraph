-- Widen nodes.name to accommodate long ColdFusion/IaC node names.
-- Drop the existing full-column index first, then re-add as a prefix index
-- (VARCHAR(1000) * 4 bytes/char exceeds MySQL's 3072-byte key limit).
DROP INDEX idx_nodes_name ON nodes;
ALTER TABLE nodes MODIFY COLUMN name VARCHAR(1000) NOT NULL;
CREATE INDEX idx_nodes_name ON nodes (project, name(255));
