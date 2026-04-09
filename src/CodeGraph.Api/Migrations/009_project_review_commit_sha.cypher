// Capture the commit SHA a project review was performed against so future
// incremental review updates can compare against an exact baseline.

CREATE INDEX project_review_run_commit_sha IF NOT EXISTS
FOR (r:ProjectReviewRun) ON (r.project, r.projectName, r.reviewedCommitSha, r.createdAt);
