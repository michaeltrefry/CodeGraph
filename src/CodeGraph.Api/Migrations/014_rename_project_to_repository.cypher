// sc-27867: "project" meant a repository. A project is now a multi-repo work scope, so every
// property, label, constraint and index that held a repository name says repository.
// dotnetProject (a .NET project file inside a repository) and projectName on the project-review and
// project-analysis nodes (the .NET project a node is about) keep their names.
// Every statement is re-runnable: an interrupted run is not recorded as applied and resumes here.

// Drop the schema objects that are defined on the old property names.
DROP CONSTRAINT code_node_project_qn IF EXISTS;

DROP INDEX code_node_label IF EXISTS;

DROP INDEX code_node_name IF EXISTS;

DROP INDEX code_node_file IF EXISTS;

DROP INDEX code_node_dotnet_project IF EXISTS;

DROP INDEX cross_repo_edge_source IF EXISTS;

DROP INDEX cross_repo_edge_target IF EXISTS;

DROP CONSTRAINT sync_state_project IF EXISTS;

DROP CONSTRAINT file_hash_unique IF EXISTS;

DROP CONSTRAINT repo_summary_project IF EXISTS;

DROP CONSTRAINT file_metrics_unique IF EXISTS;

DROP INDEX file_metrics_health IF EXISTS;

DROP CONSTRAINT health_summary_unique IF EXISTS;

DROP CONSTRAINT health_analysis_unique IF EXISTS;

DROP INDEX security_finding_project IF EXISTS;

DROP INDEX security_finding_severity IF EXISTS;

DROP CONSTRAINT security_summary_unique IF EXISTS;

DROP INDEX project_review_run_project_name_created IF EXISTS;

DROP INDEX project_review_run_commit_sha IF EXISTS;

DROP CONSTRAINT project_diagnostic_key IF EXISTS;

DROP INDEX project_diagnostic_project_dotnet_severity IF EXISTS;

DROP CONSTRAINT project_index_lock_unique IF EXISTS;

// Labels that only ever described a repository.
MATCH (n:ProjectSecuritySummary)
SET n:RepositorySecuritySummary
REMOVE n:ProjectSecuritySummary;

MATCH (n:ProjectIndexLock)
SET n:RepositoryIndexLock
REMOVE n:ProjectIndexLock;

// Properties.
MATCH (n:CodeNode) WHERE n.project IS NOT NULL
SET n.repository = n.project
REMOVE n.project;

MATCH ()-[e]->() WHERE e.project IS NOT NULL
SET e.repository = e.project
REMOVE e.project;

MATCH (n:SyncState) WHERE n.project IS NOT NULL
SET n.repository = n.project
REMOVE n.project;

MATCH (n:FileHash) WHERE n.project IS NOT NULL
SET n.repository = n.project
REMOVE n.project;

MATCH (n:RepositorySummary) WHERE n.project IS NOT NULL
SET n.repository = n.project
REMOVE n.project;

MATCH (n:FileMetrics) WHERE n.project IS NOT NULL
SET n.repository = n.project
REMOVE n.project;

MATCH (n:ProjectHealthSummary) WHERE n.project IS NOT NULL
SET n.repository = n.project
REMOVE n.project;

MATCH (n:ProjectHealthAnalysis) WHERE n.project IS NOT NULL
SET n.repository = n.project
REMOVE n.project;

MATCH (n:SecurityFinding) WHERE n.project IS NOT NULL
SET n.repository = n.project
REMOVE n.project;

MATCH (n:RepositorySecuritySummary) WHERE n.project IS NOT NULL
SET n.repository = n.project
REMOVE n.project;

MATCH (n:ProjectDiagnostic) WHERE n.project IS NOT NULL
SET n.repository = n.project
REMOVE n.project;

MATCH (n:ProjectReviewRun) WHERE n.project IS NOT NULL
SET n.repository = n.project
REMOVE n.project;

MATCH (n:RepositoryIndexLock) WHERE n.project IS NOT NULL
SET n.repository = n.project
REMOVE n.project;

MATCH (n:CrossRepoEdge) WHERE n.sourceProject IS NOT NULL
SET n.sourceRepository = n.sourceProject
REMOVE n.sourceProject;

MATCH (n:CrossRepoEdge) WHERE n.targetProject IS NOT NULL
SET n.targetRepository = n.targetProject
REMOVE n.targetProject;

MATCH (n:RepoCluster) WHERE n.projectName IS NOT NULL
SET n.repositoryName = n.projectName
REMOVE n.projectName;

// Schema on the new names.
CREATE CONSTRAINT code_node_repository_qn IF NOT EXISTS
FOR (n:CodeNode) REQUIRE (n.repository, n.qualifiedName) IS UNIQUE;

CREATE INDEX code_node_label IF NOT EXISTS
FOR (n:CodeNode) ON (n.repository, n.label);

CREATE INDEX code_node_name IF NOT EXISTS
FOR (n:CodeNode) ON (n.repository, n.name);

CREATE INDEX code_node_file IF NOT EXISTS
FOR (n:CodeNode) ON (n.repository, n.filePath);

CREATE INDEX code_node_dotnet_project IF NOT EXISTS
FOR (n:CodeNode) ON (n.repository, n.dotnetProject);

CREATE INDEX cross_repo_edge_source IF NOT EXISTS
FOR (e:CrossRepoEdge) ON (e.sourceRepository);

CREATE INDEX cross_repo_edge_target IF NOT EXISTS
FOR (e:CrossRepoEdge) ON (e.targetRepository);

CREATE CONSTRAINT sync_state_repository IF NOT EXISTS
FOR (s:SyncState) REQUIRE s.repository IS UNIQUE;

CREATE CONSTRAINT file_hash_unique IF NOT EXISTS
FOR (f:FileHash) REQUIRE (f.repository, f.relPath) IS UNIQUE;

CREATE CONSTRAINT repo_summary_repository IF NOT EXISTS
FOR (s:RepositorySummary) REQUIRE s.repository IS UNIQUE;

CREATE CONSTRAINT file_metrics_unique IF NOT EXISTS
FOR (fm:FileMetrics) REQUIRE (fm.repository, fm.filePath) IS UNIQUE;

CREATE INDEX file_metrics_health IF NOT EXISTS
FOR (fm:FileMetrics) ON (fm.repository, fm.healthScore);

CREATE CONSTRAINT health_summary_unique IF NOT EXISTS
FOR (h:ProjectHealthSummary) REQUIRE (h.repository, h.dotnetProject) IS UNIQUE;

CREATE CONSTRAINT health_analysis_unique IF NOT EXISTS
FOR (h:ProjectHealthAnalysis) REQUIRE (h.repository, h.dotnetProject) IS UNIQUE;

CREATE INDEX security_finding_repository IF NOT EXISTS
FOR (f:SecurityFinding) ON (f.repository);

CREATE INDEX security_finding_severity IF NOT EXISTS
FOR (f:SecurityFinding) ON (f.repository, f.severity);

CREATE CONSTRAINT security_summary_unique IF NOT EXISTS
FOR (s:RepositorySecuritySummary) REQUIRE s.repository IS UNIQUE;

CREATE INDEX project_review_run_project_name_created IF NOT EXISTS
FOR (r:ProjectReviewRun) ON (r.repository, r.projectName, r.createdAt);

CREATE INDEX project_review_run_commit_sha IF NOT EXISTS
FOR (r:ProjectReviewRun) ON (r.repository, r.projectName, r.reviewedCommitSha, r.createdAt);

CREATE CONSTRAINT project_diagnostic_key IF NOT EXISTS
FOR (d:ProjectDiagnostic) REQUIRE (d.repository, d.diagnosticKey) IS UNIQUE;

CREATE INDEX project_diagnostic_project_dotnet_severity IF NOT EXISTS
FOR (d:ProjectDiagnostic) ON (d.repository, d.dotnetProject, d.severity);

CREATE CONSTRAINT repository_index_lock_unique IF NOT EXISTS
FOR (l:RepositoryIndexLock) REQUIRE l.repository IS UNIQUE;
