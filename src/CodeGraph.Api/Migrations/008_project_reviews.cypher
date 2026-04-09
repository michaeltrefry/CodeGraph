// Persisted project diagnostics and project review runs/findings.

CREATE CONSTRAINT project_review_run_appid IF NOT EXISTS
FOR (r:ProjectReviewRun) REQUIRE r.appId IS UNIQUE;

CREATE INDEX project_review_run_project_name_created IF NOT EXISTS
FOR (r:ProjectReviewRun) ON (r.project, r.projectName, r.createdAt);

CREATE CONSTRAINT project_diagnostic_key IF NOT EXISTS
FOR (d:ProjectDiagnostic) REQUIRE (d.project, d.diagnosticKey) IS UNIQUE;

CREATE INDEX project_diagnostic_project_dotnet_severity IF NOT EXISTS
FOR (d:ProjectDiagnostic) ON (d.project, d.dotnetProject, d.severity);

CREATE CONSTRAINT project_review_finding_appid IF NOT EXISTS
FOR (f:ProjectReviewFinding) REQUIRE f.appId IS UNIQUE;

CREATE INDEX project_review_finding_run_ordinal IF NOT EXISTS
FOR (f:ProjectReviewFinding) ON (f.reviewRunId, f.ordinal);
