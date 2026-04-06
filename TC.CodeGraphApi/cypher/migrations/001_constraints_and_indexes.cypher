// Core graph constraints and indexes
// IMPORTANT: All statements must use IF NOT EXISTS to be safely re-runnable

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

// Edge records
CREATE CONSTRAINT edge_record_unique IF NOT EXISTS
FOR (e:EdgeRecord) REQUIRE (e.sourceId, e.targetId, e.type) IS UNIQUE;

CREATE INDEX edge_record_source IF NOT EXISTS
FOR (e:EdgeRecord) ON (e.sourceId);

CREATE INDEX edge_record_target IF NOT EXISTS
FOR (e:EdgeRecord) ON (e.targetId);

CREATE INDEX edge_record_project IF NOT EXISTS
FOR (e:EdgeRecord) ON (e.project);

CREATE INDEX edge_record_type IF NOT EXISTS
FOR (e:EdgeRecord) ON (e.type);

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
FOR (m:MigrationHistory) REQUIRE m.scriptName IS UNIQUE
