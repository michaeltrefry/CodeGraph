// Repair schema objects that may be missing when 001_schema.cypher was marked
// applied before later constraints and indexes were added to that file.
// Every statement is safe to re-run because it uses IF NOT EXISTS.

// =============================================================================
// Core graph constraints and indexes
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

CREATE CONSTRAINT cross_repo_edge_unique IF NOT EXISTS
FOR (e:CrossRepoEdge) REQUIRE (e.sourceNodeId, e.targetNodeId, e.type) IS UNIQUE;

CREATE INDEX cross_repo_edge_source IF NOT EXISTS
FOR (e:CrossRepoEdge) ON (e.sourceProject);

CREATE INDEX cross_repo_edge_target IF NOT EXISTS
FOR (e:CrossRepoEdge) ON (e.targetProject);

CREATE CONSTRAINT repository_name IF NOT EXISTS
FOR (r:Repository) REQUIRE r.name IS UNIQUE;

CREATE CONSTRAINT repository_record_name IF NOT EXISTS
FOR (r:RepositoryRecord) REQUIRE r.name IS UNIQUE;

CREATE CONSTRAINT sync_state_project IF NOT EXISTS
FOR (s:SyncState) REQUIRE s.project IS UNIQUE;

CREATE CONSTRAINT file_hash_unique IF NOT EXISTS
FOR (f:FileHash) REQUIRE (f.project, f.relPath) IS UNIQUE;

MATCH (s:Sequence)
WITH s.name AS name, collect(s) AS seqs, max(coalesce(s.value, 0)) AS maxValue
WHERE size(seqs) > 1
FOREACH (seq IN tail(seqs) | DETACH DELETE seq)
SET head(seqs).value = maxValue;

CREATE CONSTRAINT sequence_name IF NOT EXISTS
FOR (s:Sequence) REQUIRE s.name IS UNIQUE;

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

CREATE CONSTRAINT file_metrics_unique IF NOT EXISTS
FOR (fm:FileMetrics) REQUIRE (fm.project, fm.filePath) IS UNIQUE;

CREATE INDEX file_metrics_health IF NOT EXISTS
FOR (fm:FileMetrics) ON (fm.project, fm.healthScore);

CREATE CONSTRAINT health_summary_unique IF NOT EXISTS
FOR (h:ProjectHealthSummary) REQUIRE (h.project, h.dotnetProject) IS UNIQUE;

CREATE CONSTRAINT health_analysis_unique IF NOT EXISTS
FOR (h:ProjectHealthAnalysis) REQUIRE (h.project, h.dotnetProject) IS UNIQUE;

CREATE INDEX security_finding_project IF NOT EXISTS
FOR (f:SecurityFinding) ON (f.project);

CREATE INDEX security_finding_severity IF NOT EXISTS
FOR (f:SecurityFinding) ON (f.project, f.severity);

CREATE CONSTRAINT security_summary_unique IF NOT EXISTS
FOR (s:ProjectSecuritySummary) REQUIRE s.project IS UNIQUE;

CREATE CONSTRAINT exclusion_rule_unique IF NOT EXISTS
FOR (e:ExclusionRule) REQUIRE (e.targetType, e.targetValue) IS UNIQUE;

CREATE INDEX cluster_level IF NOT EXISTS
FOR (c:RepoCluster) ON (c.level);

CREATE INDEX cluster_id_level IF NOT EXISTS
FOR (c:RepoCluster) ON (c.clusterId, c.level);

// =============================================================================
// Search and embeddings
// =============================================================================

CREATE FULLTEXT INDEX code_node_search IF NOT EXISTS
FOR (n:CodeNode) ON EACH [n.name, n.qualifiedName, n.filePath];

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

CREATE CONSTRAINT wiki_section_slug IF NOT EXISTS
FOR (s:WikiSection) REQUIRE s.slug IS UNIQUE;

CREATE INDEX wiki_section_appid IF NOT EXISTS
FOR (s:WikiSection) ON (s.appId);

CREATE INDEX wiki_page_appid IF NOT EXISTS
FOR (p:WikiPage) ON (p.appId);

CREATE INDEX wiki_page_section_slug IF NOT EXISTS
FOR (p:WikiPage) ON (p.sectionId, p.slug);

CREATE INDEX wiki_page_section_parent IF NOT EXISTS
FOR (p:WikiPage) ON (p.sectionId, p.parentId);

CREATE INDEX wiki_revision_appid IF NOT EXISTS
FOR (r:WikiRevision) ON (r.appId);

CREATE INDEX wiki_revision_page IF NOT EXISTS
FOR (r:WikiRevision) ON (r.pageId, r.revision);

CREATE INDEX wiki_attachment_appid IF NOT EXISTS
FOR (a:WikiAttachment) ON (a.appId);

CREATE INDEX wiki_attachment_page IF NOT EXISTS
FOR (a:WikiAttachment) ON (a.pageId);

CREATE INDEX settings_override_appid IF NOT EXISTS
FOR (s:SettingsOverride) ON (s.appId);

MATCH (c:IdCounter)
WITH c.label AS label, collect(c) AS counters, max(coalesce(c.current, 0)) AS maxCurrent
WHERE size(counters) > 1
FOREACH (counter IN tail(counters) | DETACH DELETE counter)
SET head(counters).current = maxCurrent;

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

CREATE FULLTEXT INDEX memory_entity_fulltext IF NOT EXISTS
FOR (e:MemoryEntity) ON EACH [e.label, e.summary, e.id];

CREATE VECTOR INDEX memory_entity_embedding IF NOT EXISTS
FOR (e:MemoryEntity) ON (e.embedding)
OPTIONS {indexConfig: {`vector.dimensions`: 384, `vector.similarity_function`: 'cosine'}};
