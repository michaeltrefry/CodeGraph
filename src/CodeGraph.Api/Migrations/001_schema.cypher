// Consolidated schema — constraints, indexes, fulltext, vector, wiki, and memory graph
// IMPORTANT: All statements use IF NOT EXISTS to be safely re-runnable

// =============================================================================
// Core graph constraints and indexes
// Main code graph facts use native Neo4j relationships between :CodeNode nodes.
// Cross-repo links remain stored as nodes because they are aggregated artifacts.
// =============================================================================

CREATE CONSTRAINT code_node_project_qn IF NOT EXISTS
FOR (n:CodeNode) REQUIRE (n.project, n.qualifiedName) IS UNIQUE;

CREATE INDEX code_node_appid IF NOT EXISTS
FOR (n:CodeNode) ON (n.appId);

CREATE INDEX code_node_label IF NOT EXISTS
FOR (n:CodeNode) ON (n.project, n.label);

CREATE INDEX code_node_name IF NOT EXISTS
FOR (n:CodeNode) ON (n.project, n.name);

CREATE INDEX code_node_file IF NOT EXISTS
FOR (n:CodeNode) ON (n.project, n.filePath);

CREATE INDEX code_node_dotnet_project IF NOT EXISTS
FOR (n:CodeNode) ON (n.project, n.dotnetProject);

// Cross-repo edges
CREATE CONSTRAINT cross_repo_edge_unique IF NOT EXISTS
FOR (e:CrossRepoEdge) REQUIRE (e.sourceNodeId, e.targetNodeId, e.type) IS UNIQUE;

CREATE INDEX cross_repo_edge_source IF NOT EXISTS
FOR (e:CrossRepoEdge) ON (e.sourceProject);

CREATE INDEX cross_repo_edge_target IF NOT EXISTS
FOR (e:CrossRepoEdge) ON (e.targetProject);

// Repositories
CREATE CONSTRAINT repository_name IF NOT EXISTS
FOR (r:Repository) REQUIRE r.name IS UNIQUE;

// Sync state
CREATE CONSTRAINT sync_state_project IF NOT EXISTS
FOR (s:SyncState) REQUIRE s.project IS UNIQUE;

// File hashes
CREATE CONSTRAINT file_hash_unique IF NOT EXISTS
FOR (f:FileHash) REQUIRE (f.project, f.relPath) IS UNIQUE;

// Sequences
CREATE CONSTRAINT sequence_name IF NOT EXISTS
FOR (s:Sequence) REQUIRE s.name IS UNIQUE;

// Migration history
CREATE CONSTRAINT migration_history_script IF NOT EXISTS
FOR (m:MigrationHistory) REQUIRE m.scriptName IS UNIQUE;

// =============================================================================
// Analysis and metrics
// =============================================================================

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
FOR (c:RepoCluster) ON (c.clusterId, c.level);

// =============================================================================
// Fulltext indexes
// =============================================================================

CREATE FULLTEXT INDEX code_node_search IF NOT EXISTS
FOR (n:CodeNode) ON EACH [n.name, n.qualifiedName, n.filePath];

// =============================================================================
// Vector indexes
// =============================================================================

CREATE CONSTRAINT embedding_unique IF NOT EXISTS
FOR (e:Embedding) REQUIRE (e.entityType, e.entityKey) IS UNIQUE;

CREATE VECTOR INDEX embedding_vector IF NOT EXISTS
FOR (e:Embedding) ON (e.vector)
OPTIONS {indexConfig: {
  `vector.dimensions`: 384,
  `vector.similarity_function`: 'cosine'
}};

// =============================================================================
// Wiki
// =============================================================================

// Sections
CREATE CONSTRAINT wiki_section_slug IF NOT EXISTS
FOR (s:WikiSection) REQUIRE s.slug IS UNIQUE;

CREATE INDEX wiki_section_appid IF NOT EXISTS
FOR (s:WikiSection) ON (s.appId);

// Pages
CREATE INDEX wiki_page_appid IF NOT EXISTS
FOR (p:WikiPage) ON (p.appId);

CREATE INDEX wiki_page_section_slug IF NOT EXISTS
FOR (p:WikiPage) ON (p.sectionId, p.slug);

CREATE INDEX wiki_page_section_parent IF NOT EXISTS
FOR (p:WikiPage) ON (p.sectionId, p.parentId);

// Revisions
CREATE INDEX wiki_revision_appid IF NOT EXISTS
FOR (r:WikiRevision) ON (r.appId);

CREATE INDEX wiki_revision_page IF NOT EXISTS
FOR (r:WikiRevision) ON (r.pageId, r.revision);

// Attachments
CREATE INDEX wiki_attachment_appid IF NOT EXISTS
FOR (a:WikiAttachment) ON (a.appId);

CREATE INDEX wiki_attachment_page IF NOT EXISTS
FOR (a:WikiAttachment) ON (a.pageId);

// Settings overrides
CREATE INDEX settings_override_appid IF NOT EXISTS
FOR (s:SettingsOverride) ON (s.appId);

// ID counters
CREATE CONSTRAINT id_counter_label IF NOT EXISTS
FOR (c:IdCounter) REQUIRE c.label IS UNIQUE;

// =============================================================================
// Memory graph
// =============================================================================

CREATE CONSTRAINT memory_entity_id IF NOT EXISTS
FOR (e:MemoryEntity) REQUIRE e.id IS UNIQUE;

CREATE CONSTRAINT memory_observation_id IF NOT EXISTS
FOR (o:MemoryObservation) REQUIRE o.id IS UNIQUE;

CREATE INDEX memory_entity_updatedAt IF NOT EXISTS
FOR (e:MemoryEntity) ON (e.updatedAt);

// Fulltext index for entity search
CREATE FULLTEXT INDEX memory_entity_fulltext IF NOT EXISTS
FOR (e:MemoryEntity) ON EACH [e.label, e.summary, e.id];

// Memory entity vector index
CREATE VECTOR INDEX memory_entity_embedding IF NOT EXISTS
FOR (e:MemoryEntity) ON (e.embedding)
OPTIONS {indexConfig: {`vector.dimensions`: 384, `vector.similarity_function`: 'cosine'}};
