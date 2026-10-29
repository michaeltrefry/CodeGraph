-- The item timeline of the work UI (sc-27874) reads one entity's change feed entries newest first;
-- without this index that is a scan of the whole feed.
CREATE INDEX IF NOT EXISTS ix_work_changes_entity ON work_changes (entity_id, sequence);
