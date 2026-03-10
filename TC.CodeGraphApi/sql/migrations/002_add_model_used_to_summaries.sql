ALTER TABLE project_summaries
    ADD COLUMN model_used VARCHAR(100) NULL AFTER source_hash;
