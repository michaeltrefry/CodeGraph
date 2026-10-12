-- Extraction versioning (sc-25698): extractor improvements must reach files whose content did not change.
-- file_hashes.extraction_version: the extraction version (ExtractionVersionCatalog.ForPath) a file was
--   last extracted with. Incremental indexing re-extracts a file whose stored version differs from the
--   current one; NULL (every row before this migration) counts as different, so the first index after
--   deploy re-extracts each file once (no LLM work).
-- sync_state.extraction_fingerprint: digest of every file type's extraction version at the last
--   successful index. Routine (SkipIfUpToDate) syncs re-index a repository whose HEAD is unchanged when
--   it differs from the running indexer's fingerprint.
ALTER TABLE file_hashes
    ADD COLUMN IF NOT EXISTS extraction_version VARCHAR(255) NULL;

ALTER TABLE sync_state
    ADD COLUMN IF NOT EXISTS extraction_fingerprint VARCHAR(64) NULL;
