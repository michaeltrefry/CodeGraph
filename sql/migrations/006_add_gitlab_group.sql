ALTER TABLE repositories ADD COLUMN IF NOT EXISTS gitlab_group VARCHAR(500) NULL AFTER repo_url;
CREATE INDEX IF NOT EXISTS idx_repositories_gitlab_group ON repositories (gitlab_group);
