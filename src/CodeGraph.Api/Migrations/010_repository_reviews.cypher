// Persisted repository review runs, project sections, and findings.

CREATE CONSTRAINT repository_review_run_appid IF NOT EXISTS
FOR (r:RepositoryReviewRun) REQUIRE r.appId IS UNIQUE;

CREATE INDEX repository_review_run_repo_created IF NOT EXISTS
FOR (r:RepositoryReviewRun) ON (r.repo, r.createdAt);

CREATE INDEX repository_review_run_repo_commit_sha IF NOT EXISTS
FOR (r:RepositoryReviewRun) ON (r.repo, r.reviewedCommitSha, r.createdAt);

CREATE CONSTRAINT repository_review_project_section_appid IF NOT EXISTS
FOR (s:RepositoryReviewProjectSection) REQUIRE s.appId IS UNIQUE;

CREATE INDEX repository_review_project_section_run_project IF NOT EXISTS
FOR (s:RepositoryReviewProjectSection) ON (s.reviewRunId, s.projectName);

CREATE CONSTRAINT repository_review_finding_appid IF NOT EXISTS
FOR (f:RepositoryReviewFinding) REQUIRE f.appId IS UNIQUE;

CREATE INDEX repository_review_finding_run_ordinal IF NOT EXISTS
FOR (f:RepositoryReviewFinding) ON (f.reviewRunId, f.ordinal);
