ALTER TABLE repositories ADD COLUMN gitlab_group VARCHAR(500) NULL AFTER repo_url;
CREATE INDEX idx_repositories_gitlab_group ON repositories (gitlab_group);
