ALTER TABLE analysis_batches
    ADD COLUMN requested_by_username VARCHAR(255) NULL AFTER provider_name;

CREATE INDEX ix_analysis_batches_requested_by_username
    ON analysis_batches (requested_by_username);
