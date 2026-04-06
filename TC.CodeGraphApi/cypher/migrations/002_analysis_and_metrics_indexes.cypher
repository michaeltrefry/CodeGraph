// Analysis entities
CREATE CONSTRAINT repo_summary_project IF NOT EXISTS
FOR (s:RepositorySummary) REQUIRE s.project IS UNIQUE;

CREATE CONSTRAINT project_analysis_unique IF NOT EXISTS
FOR (a:ProjectAnalysis) REQUIRE (a.repo, a.projectName) IS UNIQUE;

CREATE INDEX analysis_batch_repo IF NOT EXISTS
FOR (b:AnalysisBatch) ON (b.repo);

CREATE INDEX analysis_batch_status IF NOT EXISTS
FOR (b:AnalysisBatch) ON (b.status);

CREATE INDEX analysis_batch_request_batch IF NOT EXISTS
FOR (br:AnalysisBatchRequest) ON (br.batchId);

CREATE INDEX analysis_batch_request_custom IF NOT EXISTS
FOR (br:AnalysisBatchRequest) ON (br.customId);

CREATE CONSTRAINT node_analysis_nodeid IF NOT EXISTS
FOR (na:NodeAnalysis) REQUIRE na.nodeId IS UNIQUE;

// File metrics
CREATE CONSTRAINT file_metrics_unique IF NOT EXISTS
FOR (fm:FileMetrics) REQUIRE (fm.project, fm.filePath) IS UNIQUE;

CREATE INDEX file_metrics_health IF NOT EXISTS
FOR (fm:FileMetrics) ON (fm.project, fm.healthScore);

// Health summaries
CREATE CONSTRAINT health_summary_unique IF NOT EXISTS
FOR (h:ProjectHealthSummary) REQUIRE (h.project, h.dotnetProject) IS UNIQUE;

// Health analyses
CREATE CONSTRAINT health_analysis_unique IF NOT EXISTS
FOR (h:ProjectHealthAnalysis) REQUIRE (h.project, h.dotnetProject) IS UNIQUE;

// Security findings
CREATE INDEX security_finding_project IF NOT EXISTS
FOR (f:SecurityFinding) ON (f.project);

CREATE INDEX security_finding_severity IF NOT EXISTS
FOR (f:SecurityFinding) ON (f.project, f.severity);

CREATE CONSTRAINT security_summary_unique IF NOT EXISTS
FOR (s:ProjectSecuritySummary) REQUIRE s.project IS UNIQUE;

// Exclusion rules
CREATE CONSTRAINT exclusion_rule_unique IF NOT EXISTS
FOR (e:ExclusionRule) REQUIRE (e.targetType, e.targetValue) IS UNIQUE;

// Clusters
CREATE INDEX cluster_level IF NOT EXISTS
FOR (c:RepoCluster) ON (c.level);

CREATE INDEX cluster_id_level IF NOT EXISTS
FOR (c:RepoCluster) ON (c.clusterId, c.level)
