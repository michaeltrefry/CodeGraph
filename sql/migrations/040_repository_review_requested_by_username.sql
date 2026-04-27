ALTER TABLE repository_review_runs
    ADD COLUMN requested_by_username VARCHAR(255) NULL AFTER repo;

CREATE INDEX ix_repo_review_runs_requested_by_username_created
    ON repository_review_runs (requested_by_username, created_at);
