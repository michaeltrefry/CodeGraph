# CodeGraph — Implementation Plan

This document details the step-by-step implementation plan for the CodeGraph proof of concept. The target is a working system that can index a handful of local C# repositories (TC.Common.ServiceStack, TC.Jarvis, TC.DomainInventoryApi and their dependencies), produce both a structural knowledge graph and natural language documentation, and answer questions via an MCP server.

**Environment**: Local Windows development, targeting .NET 9. MySQL for storage. Anthropic API for Claude analysis. No Docker. Will move to a Linux VM (AWS or hosted facility) once proven.

**Proof-of-concept scope**: Local repos only. No GitLab API, no CI integration, no webhooks. Those come after the concept is proven.

---

## Solution Structure

Follows the standard company repository convention (`TC.AppNameApi` pattern), with additional projects for extractors that have isolated heavy dependencies.

```
TC.CodeGraphApi/
├── src/
│   ├── TC.CodeGraphApi.sln
│   ├── TC.CodeGraphApi/                           # API host, MCP server, DI registration, startup
│   │   └── TC.CodeGraphApi.csproj
│   ├── TC.CodeGraphApi.Console/                   # CLI for manual indexing, queries, admin
│   │   └── TC.CodeGraphApi.Console.csproj
│   ├── TC.CodeGraphApi.Models/                    # Domain model: GraphNode, GraphEdge, enums,
│   │   └── TC.CodeGraphApi.Models.csproj          #   ExtractionResult, pipeline types, contracts
│   ├── TC.CodeGraphApi.Services/                  # Pipeline orchestrator, query engine, cross-repo
│   │   └── TC.CodeGraphApi.Services.csproj        #   linker, Claude analysis, CODEGRAPH.md gen,
│   │                                              #   ICodeExtractor interface, MCP server tools
│   ├── TC.CodeGraphApi.Data/                      # IGraphStore, MySqlGraphStore, Dapper, migrations
│   │   └── TC.CodeGraphApi.Data.csproj
│   ├── TC.CodeGraphJobs/                       # Background sync worker, scheduled re-indexing
│   │   └── TC.CodeGraphJobs.csproj
│   ├── TC.CodeGraphApi.Extractors.CSharp/         # Roslyn-based C# extractor
│   │   └── TC.CodeGraphApi.Extractors.CSharp.csproj
│   ├── TC.CodeGraphApi.Extractors.TypeScript/     # Node.js sidecar TS/Angular extractor (Phase 6+)
│   │   └── TC.CodeGraphApi.Extractors.TypeScript.csproj
│   ├── TC.CodeGraphApi.Extractors.Sql/            # ScriptDom T-SQL extractor (Phase 6+)
│   │   └── TC.CodeGraphApi.Extractors.Sql.csproj
│   └── TC.CodeGraphApi.Extractors.ColdFusion/     # Regex ColdFusion extractor (Phase 6+)
│       └── TC.CodeGraphApi.Extractors.ColdFusion.csproj
│
├── tests/
│   ├── TC.CodeGraphApi.Models.Tests/
│   ├── TC.CodeGraphApi.Data.Tests/
│   ├── TC.CodeGraphApi.Services.Tests/
│   ├── TC.CodeGraphApi.Extractors.CSharp.Tests/
│   └── TC.CodeGraphApi.Integration.Tests/
│
└── sql/
    └── migrations/                                # Sequential plain SQL migration scripts
        ├── 001_initial_schema.sql
        ├── 002_add_messaging_nodes.sql
        └── ...
```

### Project Dependencies

```
TC.CodeGraphApi.Models              → (none)
TC.CodeGraphApi.Data                → Models
TC.CodeGraphApi.Services            → Models, Data
TC.CodeGraphApi.Extractors.*        → Models, Services (for ICodeExtractor interface)
TC.CodeGraphApi                     → All of the above (host, DI registration)
TC.CodeGraphApi.Console             → Models, Data, Services, Extractors.*
TC.CodeGraphJobs                 → Models, Data, Services
```

### What Lives Where

| Project | Contains |
|---------|----------|
| **Models** | `GraphNode`, `GraphEdge`, `NodeLabel`, `EdgeType`, `ExtractionResult`, `PendingEdge`, `UnresolvedCall`, `ConfidenceLevel`, all shared records/enums |
| **Data** | `IGraphStore`, `MySqlGraphStore`, Dapper type handlers, migration runner, JSON serialization |
| **Services** | `ICodeExtractor` interface, `IndexingPipeline`, `GraphBuffer`, `CrossRepoLinker`, `GraphQueryEngine`, `ClaudeCodeAnalyzer`, `CodeGraphDocGenerator`, `CodeGraphMcpServer`, `FoundationalKnowledge` |
| **Extractors.CSharp** | `RoslynExtractor`, `SolutionAnalyzer`, `CodeGraphSyntaxWalker`, `NuGetReferenceExtractor` |
| **Extractors.TypeScript** | `TypeScriptExtractor`, Node.js sidecar scripts |
| **Extractors.Sql** | `SqlExtractor`, `SqlGraphVisitor` |
| **Extractors.ColdFusion** | `ColdFusionExtractor` |
| **Api** | `Program.cs`, DI registration, REST endpoints, startup configuration |
| **Console** | CLI commands: `migrate`, `index`, `index-all`, `analyze`, `mcp`, `stats` |
| **Jobs** | `RepositorySyncWorker`, scheduled re-indexing tasks |

---

## Phase 1 — Domain Model + Storage

### Goal
Define the graph data model and get it persisted in MySQL with efficient batch operations and recursive traversal queries.

### 1.1 — Create Solution and Project Scaffolding

```bash
mkdir -p src tests sql/migrations
cd src

dotnet new sln -n TC.CodeGraphApi

# Standard layers
dotnet new webapi -n TC.CodeGraphApi -o TC.CodeGraphApi
dotnet new console -n TC.CodeGraphApi.Console -o TC.CodeGraphApi.Console
dotnet new classlib -n TC.CodeGraphApi.Models -o TC.CodeGraphApi.Models
dotnet new classlib -n TC.CodeGraphApi.Services -o TC.CodeGraphApi.Services
dotnet new classlib -n TC.CodeGraphApi.Data -o TC.CodeGraphApi.Data
dotnet new classlib -n TC.CodeGraphJobs -o TC.CodeGraphJobs

# Extractor projects (isolated heavy dependencies)
dotnet new classlib -n TC.CodeGraphApi.Extractors.CSharp -o TC.CodeGraphApi.Extractors.CSharp

# Test projects
cd ../tests
dotnet new xunit -n TC.CodeGraphApi.Models.Tests -o TC.CodeGraphApi.Models.Tests
dotnet new xunit -n TC.CodeGraphApi.Data.Tests -o TC.CodeGraphApi.Data.Tests
dotnet new xunit -n TC.CodeGraphApi.Services.Tests -o TC.CodeGraphApi.Services.Tests
dotnet new xunit -n TC.CodeGraphApi.Extractors.CSharp.Tests -o TC.CodeGraphApi.Extractors.CSharp.Tests
dotnet new xunit -n TC.CodeGraphApi.Integration.Tests -o TC.CodeGraphApi.Integration.Tests

# Add all to solution
cd ../src
dotnet sln add **/*.csproj ../tests/**/*.csproj

# Wire up project references
dotnet add TC.CodeGraphApi.Data reference TC.CodeGraphApi.Models
dotnet add TC.CodeGraphApi.Services reference TC.CodeGraphApi.Models TC.CodeGraphApi.Data
dotnet add TC.CodeGraphApi.Extractors.CSharp reference TC.CodeGraphApi.Models TC.CodeGraphApi.Services
dotnet add TC.CodeGraphJobs reference TC.CodeGraphApi.Models TC.CodeGraphApi.Data TC.CodeGraphApi.Services
dotnet add TC.CodeGraphApi reference TC.CodeGraphApi.Models TC.CodeGraphApi.Data TC.CodeGraphApi.Services TC.CodeGraphApi.Extractors.CSharp TC.CodeGraphJobs
dotnet add TC.CodeGraphApi.Console reference TC.CodeGraphApi.Models TC.CodeGraphApi.Data TC.CodeGraphApi.Services TC.CodeGraphApi.Extractors.CSharp

# Test project references
dotnet add ../tests/TC.CodeGraphApi.Models.Tests reference TC.CodeGraphApi.Models
dotnet add ../tests/TC.CodeGraphApi.Data.Tests reference TC.CodeGraphApi.Data TC.CodeGraphApi.Models
dotnet add ../tests/TC.CodeGraphApi.Services.Tests reference TC.CodeGraphApi.Services TC.CodeGraphApi.Models TC.CodeGraphApi.Data
dotnet add ../tests/TC.CodeGraphApi.Extractors.CSharp.Tests reference TC.CodeGraphApi.Extractors.CSharp TC.CodeGraphApi.Models TC.CodeGraphApi.Services
dotnet add ../tests/TC.CodeGraphApi.Integration.Tests reference TC.CodeGraphApi.Models TC.CodeGraphApi.Data TC.CodeGraphApi.Services TC.CodeGraphApi.Extractors.CSharp
```

### 1.2 — TC.CodeGraphApi.Models

No NuGet dependencies. Pure C# records and enums.

#### NodeLabel enum

```csharp
namespace TC.CodeGraphApi.Models;

public enum NodeLabel
{
    // Structural
    Project,
    Namespace,
    Folder,
    File,

    // Code elements
    Class,
    Interface,
    Enum,
    Struct,
    Record,
    Function,
    Method,
    Property,
    Constructor,
    Delegate,

    // Infrastructure
    Route,
    Service,
    Table,
    View,
    StoredProcedure,

    // Messaging
    Event,
    Queue,
    Exchange,

    // Angular
    Component,
    Module,

    // Jobs
    Job,

    // Package
    NuGetPackage
}
```

#### EdgeType enum

```csharp
namespace TC.CodeGraphApi.Models;

public enum EdgeType
{
    // Containment
    CONTAINS_FILE,
    CONTAINS_FOLDER,
    CONTAINS_NAMESPACE,

    // Definitions
    DEFINES,
    DEFINES_METHOD,

    // References
    CALLS,
    IMPORTS,
    IMPLEMENTS,
    INHERITS,
    USES_TYPE,
    INJECTS,

    // Cross-service
    HTTP_CALLS,
    HANDLES,
    QUERIES,

    // Messaging
    PUBLISHES,
    CONSUMES,

    // Packages
    REFERENCES_PACKAGE,

    // Angular
    RENDERS,
    SUBSCRIBES,

    // Change coupling
    FILE_CHANGES_WITH,

    // Jobs
    SCHEDULES
}
```

#### GraphNode record

```csharp
namespace TC.CodeGraphApi.Models;

public record GraphNode
{
    public long Id { get; init; }
    public required string Project { get; init; }
    public required NodeLabel Label { get; init; }
    public required string Name { get; init; }
    public required string QualifiedName { get; init; }
    public string FilePath { get; init; } = "";
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public Dictionary<string, object> Properties { get; init; } = new();
}
```

#### GraphEdge record

```csharp
namespace TC.CodeGraphApi.Models;

public record GraphEdge
{
    public long Id { get; init; }
    public required string Project { get; init; }
    public long SourceId { get; init; }
    public long TargetId { get; init; }
    public required EdgeType Type { get; init; }
    public Dictionary<string, object> Properties { get; init; } = new();
}
```

#### CrossRepoEdge record

```csharp
namespace TC.CodeGraphApi.Models;

public record CrossRepoEdge
{
    public long Id { get; init; }
    public required string SourceProject { get; init; }
    public required string TargetProject { get; init; }
    public long SourceNodeId { get; init; }
    public long TargetNodeId { get; init; }
    public required EdgeType Type { get; init; }
    public Dictionary<string, object> Properties { get; init; } = new();
}
```

#### Pipeline types (used by extractors)

```csharp
namespace TC.CodeGraphApi.Models;

public record ExtractionResult
{
    public IReadOnlyList<GraphNode> Nodes { get; init; } = [];
    public IReadOnlyList<PendingEdge> Edges { get; init; } = [];
    public IReadOnlyList<UnresolvedCall> UnresolvedCalls { get; init; } = [];
    public IReadOnlyList<UnresolvedImport> UnresolvedImports { get; init; } = [];
}

/// Edge where target is a qualified name (not yet resolved to a node ID)
public record PendingEdge(
    string SourceQN,
    string TargetQN,
    EdgeType Type,
    Dictionary<string, object>? Properties = null);

/// Call site that needs cross-reference resolution
public record UnresolvedCall(
    string CallerQN,
    string CalleeName,
    string? ReceiverType,
    double Confidence);

/// Import/using that needs module resolution
public record UnresolvedImport(
    string FileQN,
    string ImportedNamespace);

/// Configuration for analysis confidence
public enum ConfidenceLevel
{
    High,
    Medium,
    Low
}
```

### 1.3 — SQL Migration Scripts

#### sql/migrations/001_initial_schema.sql

```sql
-- CodeGraph initial schema

CREATE TABLE IF NOT EXISTS migration_history (
    id INT AUTO_INCREMENT PRIMARY KEY,
    script_name VARCHAR(255) NOT NULL UNIQUE,
    applied_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
) ENGINE=InnoDB;

CREATE TABLE projects (
    name VARCHAR(255) PRIMARY KEY,
    repo_url VARCHAR(500),
    local_path VARCHAR(500),
    default_branch VARCHAR(100) DEFAULT 'main',
    last_commit_sha VARCHAR(40),
    indexed_at DATETIME(3),
    language VARCHAR(50),
    framework VARCHAR(100),
    is_foundational BOOLEAN DEFAULT FALSE,
    properties JSON,
    created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    updated_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3)
) ENGINE=InnoDB;

CREATE TABLE file_hashes (
    project VARCHAR(255) NOT NULL,
    rel_path VARCHAR(500) NOT NULL,
    content_hash VARCHAR(64) NOT NULL,
    PRIMARY KEY (project, rel_path),
    FOREIGN KEY (project) REFERENCES projects(name) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE nodes (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    project VARCHAR(255) NOT NULL,
    label VARCHAR(50) NOT NULL,
    name VARCHAR(255) NOT NULL,
    qualified_name VARCHAR(500) NOT NULL,
    file_path VARCHAR(500) DEFAULT '',
    start_line INT DEFAULT 0,
    end_line INT DEFAULT 0,
    properties JSON,
    UNIQUE KEY uq_node (project, qualified_name),
    INDEX idx_nodes_label (project, label),
    INDEX idx_nodes_name (project, name),
    INDEX idx_nodes_file (project, file_path),
    FOREIGN KEY (project) REFERENCES projects(name) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE edges (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    project VARCHAR(255) NOT NULL,
    source_id BIGINT NOT NULL,
    target_id BIGINT NOT NULL,
    type VARCHAR(50) NOT NULL,
    properties JSON,
    UNIQUE KEY uq_edge (source_id, target_id, type),
    INDEX idx_edges_source (source_id, type),
    INDEX idx_edges_target (target_id, type),
    INDEX idx_edges_type (project, type),
    FOREIGN KEY (source_id) REFERENCES nodes(id) ON DELETE CASCADE,
    FOREIGN KEY (target_id) REFERENCES nodes(id) ON DELETE CASCADE,
    FOREIGN KEY (project) REFERENCES projects(name) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE cross_repo_edges (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    source_project VARCHAR(255) NOT NULL,
    target_project VARCHAR(255) NOT NULL,
    source_node_id BIGINT NOT NULL,
    target_node_id BIGINT NOT NULL,
    type VARCHAR(50) NOT NULL,
    properties JSON,
    UNIQUE KEY uq_xedge (source_node_id, target_node_id, type),
    INDEX idx_xedge_source (source_project, type),
    INDEX idx_xedge_target (target_project, type),
    FOREIGN KEY (source_node_id) REFERENCES nodes(id) ON DELETE CASCADE,
    FOREIGN KEY (target_node_id) REFERENCES nodes(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE project_summaries (
    project VARCHAR(255) PRIMARY KEY,
    summary TEXT NOT NULL,
    confidence VARCHAR(10) NOT NULL DEFAULT 'medium',
    source_hash VARCHAR(64) NOT NULL,
    created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    updated_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
    FOREIGN KEY (project) REFERENCES projects(name) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE sync_state (
    project VARCHAR(255) PRIMARY KEY,
    last_sync_at DATETIME(3),
    last_commit_sha VARCHAR(40),
    status ENUM('idle', 'syncing', 'error') DEFAULT 'idle',
    error_message TEXT,
    FOREIGN KEY (project) REFERENCES projects(name) ON DELETE CASCADE
) ENGINE=InnoDB;
```

### 1.4 — TC.CodeGraphApi.Data

NuGet packages:
- `Dapper`
- `MySqlConnector`
- `System.Text.Json` (for JSON property serialization)
- `Microsoft.Extensions.Options` (for configuration)
- `Serilog.Extensions.Logging` (for logging)

#### IGraphStore interface

```csharp
namespace TC.CodeGraphApi.Data;

public interface IGraphStore
{
    // Projects
    Task UpsertProjectAsync(string name, string? localPath = null,
        string? repoUrl = null, bool isFoundational = false);
    Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync();
    Task DeleteProjectAsync(string project);

    // Nodes
    Task<long> UpsertNodeAsync(GraphNode node);
    Task<Dictionary<string, long>> UpsertNodeBatchAsync(IReadOnlyList<GraphNode> nodes);
    Task<GraphNode?> FindNodeByQualifiedNameAsync(string project, string qualifiedName);
    Task<IReadOnlyList<GraphNode>> FindNodesByNameAsync(string project, string name);
    Task<IReadOnlyList<GraphNode>> FindNodesByLabelAsync(string project, NodeLabel label);
    Task<IReadOnlyList<GraphNode>> FindNodesByFileAsync(string project, string filePath);
    Task<IReadOnlyList<GraphNode>> SearchNodesAsync(string? project, string namePattern,
        NodeLabel? label = null, string? filePattern = null,
        int limit = 50, int offset = 0);
    Task<IReadOnlyList<GraphNode>> FindAllNodesByLabelAsync(NodeLabel label);

    // Edges
    Task InsertEdgeAsync(GraphEdge edge);
    Task InsertEdgeBatchAsync(IReadOnlyList<GraphEdge> edges);
    Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceAsync(long sourceId, EdgeType? type = null);
    Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetAsync(long targetId, EdgeType? type = null);
    Task<IReadOnlyList<GraphEdge>> FindAllEdgesByTypeAsync(EdgeType type);

    // Cross-repo edges
    Task InsertCrossRepoEdgeAsync(CrossRepoEdge edge);
    Task InsertCrossRepoEdgeBatchAsync(IReadOnlyList<CrossRepoEdge> edges);
    Task<IReadOnlyList<CrossRepoEdge>> FindCrossRepoEdgesAsync(
        string project, EdgeType? type = null);

    // Traversal
    Task<IReadOnlyList<TraversalEntry>> TraverseAsync(long startNodeId,
        TraceDirection direction, int maxDepth,
        EdgeType[]? edgeFilter = null, double minConfidence = 0);

    // Bulk operations
    Task DeleteNodesByFileAsync(string project, string filePath);
    Task DeleteNodesByProjectAsync(string project);

    // File hashes (incremental indexing)
    Task<Dictionary<string, string>> GetFileHashesAsync(string project);
    Task UpsertFileHashBatchAsync(string project, Dictionary<string, string> hashes);
    Task DeleteFileHashesAsync(string project, IReadOnlyList<string> relPaths);

    // Summaries
    Task UpsertProjectSummaryAsync(string project, string summary,
        ConfidenceLevel confidence, string sourceHash);
    Task<ProjectSummary?> GetProjectSummaryAsync(string project);

    // Migrations
    Task ApplyMigrationsAsync(string migrationsPath);
}
```

#### Supporting types

```csharp
namespace TC.CodeGraphApi.Data;

public enum TraceDirection { Outbound, Inbound, Both }

public record TraversalEntry(
    GraphNode Node,
    int Depth,
    EdgeType EdgeType,
    long? ParentNodeId,
    Dictionary<string, object>? EdgeProperties);

public record ProjectInfo(
    string Name,
    string? RepoUrl,
    string? LocalPath,
    string? LastCommitSha,
    DateTime? IndexedAt,
    string? Language,
    string? Framework,
    bool IsFoundational,
    Dictionary<string, object>? Properties);

public record ProjectSummary(
    string Project,
    string Summary,
    ConfidenceLevel Confidence,
    string SourceHash,
    DateTime CreatedAt,
    DateTime UpdatedAt);
```

#### MySqlGraphStore implementation notes

Key implementation details for the Dapper-based store:

**Batch upserts** use multi-row `INSERT ... ON DUPLICATE KEY UPDATE`:
```sql
INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line, properties)
VALUES (@Project, @Label, @Name, @QualifiedName, @FilePath, @StartLine, @EndLine, @Properties),
       (@Project, @Label, @Name, @QualifiedName, @FilePath, @StartLine, @EndLine, @Properties),
       ...
ON DUPLICATE KEY UPDATE
    name = VALUES(name),
    file_path = VALUES(file_path),
    start_line = VALUES(start_line),
    end_line = VALUES(end_line),
    properties = VALUES(properties)
```

Batch size: 500 rows per statement (MySQL handles large batches fine, but keep individual statements reasonable).

**JSON properties** are serialized with `System.Text.Json.JsonSerializer.Serialize()` before insert and deserialized on read. Use a custom Dapper type handler:

```csharp
public class JsonTypeHandler : SqlMapper.TypeHandler<Dictionary<string, object>>
{
    public override void SetValue(IDbDataParameter parameter, Dictionary<string, object>? value)
    {
        parameter.Value = value is null ? DBNull.Value : JsonSerializer.Serialize(value);
    }

    public override Dictionary<string, object> Parse(object value)
    {
        return value is string json
            ? JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new()
            : new();
    }
}
```

**Recursive CTE for traversal** (outbound example):
```sql
WITH RECURSIVE traversal AS (
    SELECT e.target_id AS node_id, e.source_id AS parent_id,
           1 AS depth, e.type, e.properties AS edge_properties
    FROM edges e
    WHERE e.source_id = @startId
      AND (@edgeFilter IS NULL OR e.type IN @edgeFilter)
    UNION ALL
    SELECT e.target_id, e.source_id,
           t.depth + 1, e.type, e.properties
    FROM edges e
    JOIN traversal t ON e.source_id = t.node_id
    WHERE t.depth < @maxDepth
      AND (@edgeFilter IS NULL OR e.type IN @edgeFilter)
)
SELECT DISTINCT n.*, t.depth, t.type AS edge_type,
       t.parent_id AS parent_node_id, t.edge_properties
FROM traversal t
JOIN nodes n ON n.id = t.node_id
ORDER BY t.depth, n.name
```

**Migration runner** — simple: read files from `sql/migrations/`, check `migration_history` table, apply unapplied scripts in order:

```csharp
public async Task ApplyMigrationsAsync(string migrationsPath)
{
    // Ensure migration_history table exists (CREATE TABLE IF NOT EXISTS)
    // Read all .sql files from migrationsPath, sorted by name
    // For each file not in migration_history:
    //   Execute the SQL
    //   Insert into migration_history
    //   Log the migration
}
```

#### Configuration

```csharp
namespace TC.CodeGraphApi.Data;

public class CodeGraphStorageOptions
{
    public string ConnectionString { get; set; } = "";
    public string MigrationsPath { get; set; } = "sql/migrations";
    public int BatchSize { get; set; } = 500;
}
```

### 1.5 — Storage Tests

Test against a real MySQL instance (not mocked). Use a test database that gets created/dropped per test run.

```csharp
public class MySqlGraphStoreTests : IAsyncLifetime
{
    private MySqlGraphStore _store;
    private const string TestDb = "codegraph_test";

    public async Task InitializeAsync()
    {
        // Create test database
        // Apply migrations
        // Initialize store
    }

    public async Task DisposeAsync()
    {
        // Drop test database
    }

    [Fact]
    public async Task UpsertNode_InsertsNewNode()
    {
        // Insert a node, verify it's returned by FindByQualifiedName
    }

    [Fact]
    public async Task UpsertNode_UpdatesExistingNode()
    {
        // Insert, then upsert with changed properties, verify update
    }

    [Fact]
    public async Task UpsertNodeBatch_ReturnsQualifiedNameToIdMapping()
    {
        // Batch insert 100 nodes, verify all IDs returned
    }

    [Fact]
    public async Task InsertEdgeBatch_CreatesEdges()
    {
        // Insert nodes, then edges, verify traversal works
    }

    [Fact]
    public async Task Traverse_Outbound_FollowsCallChain()
    {
        // Create A -> B -> C -> D call chain
        // Traverse from A with depth 3, verify all found
    }

    [Fact]
    public async Task Traverse_Inbound_FindsCallers()
    {
        // Create A -> C, B -> C
        // Traverse inbound from C, verify A and B found
    }

    [Fact]
    public async Task DeleteProject_CascadesEverything()
    {
        // Insert project, nodes, edges, file hashes
        // Delete project, verify everything gone
    }

    [Fact]
    public async Task SearchNodes_MatchesPattern()
    {
        // Insert nodes with various names
        // Search with pattern, verify correct matches
    }

    [Fact]
    public async Task FileHashes_IncrementalTracking()
    {
        // Upsert hashes, verify round-trip
        // Update some, verify changes
    }
}
```

### Phase 1 Deliverable

- `dotnet build` compiles the solution
- `dotnet test` passes storage tests against a real MySQL instance
- CLI can apply migrations: `dotnet run --project src/TC.CodeGraphApi.Console -- migrate`
- Domain model is complete and ready for extractors

---

## Phase 2 — Local Repository Scanning

### Goal
Discover and catalog local repositories. Walk file systems, detect solution/project structures, identify languages, and build the structural skeleton of the graph (Project, Folder, File nodes).

### 2.1 — TC.CodeGraphApi.Services (Pipeline)

NuGet packages:
- `Microsoft.Extensions.FileSystemGlobbing` (for skip patterns)
- `System.IO.Hashing` (for XXH3 content hashing)
- `Serilog`

#### ICodeExtractor interface

```csharp
namespace TC.CodeGraphApi.Services;

public interface ICodeExtractor
{
    IReadOnlySet<string> SupportedExtensions { get; }
    Task<ExtractionResult> ExtractAsync(string filePath, string content,
        ExtractorContext context, CancellationToken ct = default);
}

/// Shared context available to all extractors — foundational knowledge, project info
public class ExtractorContext
{
    public required string ProjectName { get; init; }
    public required string RootPath { get; init; }

    /// Known foundational types and their meanings (populated after analyzing framework repos)
    public FoundationalKnowledge? FoundationalKnowledge { get; init; }
}

public class FoundationalKnowledge
{
    /// Attribute types that indicate message publishing and their queue name properties
    public Dictionary<string, string> PublishAttributes { get; init; } = new();

    /// Attribute types that indicate message consuming
    public Dictionary<string, string> ConsumeAttributes { get; init; } = new();

    /// Base classes that indicate specific patterns (e.g., ServiceBus base class)
    public Dictionary<string, string> PatternBaseClasses { get; init; } = new();

    /// DI extension methods that register services
    public HashSet<string> DIRegistrationMethods { get; init; } = new();
}
```

#### GraphBuffer

In-memory buffer for accumulating extraction results before batch flush:

```csharp
namespace TC.CodeGraphApi.Services;

public class GraphBuffer
{
    private readonly ConcurrentDictionary<string, GraphNode> _nodes = new(); // keyed by QN
    private readonly ConcurrentBag<PendingEdge> _pendingEdges = new();
    private readonly ConcurrentBag<UnresolvedCall> _unresolvedCalls = new();
    private readonly ConcurrentBag<UnresolvedImport> _unresolvedImports = new();
    private readonly ConcurrentDictionary<string, string> _fileHashes = new();

    public void AddNode(GraphNode node) => _nodes[node.QualifiedName] = node;
    public void AddEdge(PendingEdge edge) => _pendingEdges.Add(edge);
    public void AddUnresolvedCall(UnresolvedCall call) => _unresolvedCalls.Add(call);
    public void AddUnresolvedImport(UnresolvedImport import) => _unresolvedImports.Add(import);
    public void AddFileHash(string relPath, string hash) => _fileHashes[relPath] = hash;

    public GraphNode? FindByQN(string qualifiedName)
        => _nodes.TryGetValue(qualifiedName, out var n) ? n : null;

    public IReadOnlyList<GraphNode> FindByName(string name)
        => _nodes.Values.Where(n => n.Name == name).ToList();

    public IReadOnlyList<GraphNode> FindByLabel(NodeLabel label)
        => _nodes.Values.Where(n => n.Label == label).ToList();

    public IReadOnlyCollection<GraphNode> AllNodes => _nodes.Values;
    public IReadOnlyCollection<PendingEdge> AllPendingEdges => _pendingEdges;
    public IReadOnlyCollection<UnresolvedCall> AllUnresolvedCalls => _unresolvedCalls;
    public IReadOnlyCollection<UnresolvedImport> AllUnresolvedImports => _unresolvedImports;
    public IReadOnlyDictionary<string, string> AllFileHashes => _fileHashes;

    /// Resolve pending edges: map QN references to node IDs
    public IReadOnlyList<GraphEdge> ResolveEdges(
        string project, Dictionary<string, long> qnToId)
    {
        var resolved = new List<GraphEdge>();
        foreach (var pending in _pendingEdges)
        {
            if (qnToId.TryGetValue(pending.SourceQN, out var sourceId) &&
                qnToId.TryGetValue(pending.TargetQN, out var targetId))
            {
                resolved.Add(new GraphEdge
                {
                    Project = project,
                    SourceId = sourceId,
                    TargetId = targetId,
                    Type = pending.Type,
                    Properties = pending.Properties ?? new()
                });
            }
            // Log unresolved edges at debug level — some will fail
            // (references to framework types, external packages, etc.)
        }
        return resolved;
    }

    public void Clear()
    {
        _nodes.Clear();
        _pendingEdges.Clear();
        _unresolvedCalls.Clear();
        _unresolvedImports.Clear();
        _fileHashes.Clear();
    }
}
```

#### IndexingPipeline

```csharp
namespace TC.CodeGraphApi.Services;

public class IndexingPipeline
{
    private readonly IGraphStore _store;
    private readonly IEnumerable<ICodeExtractor> _extractors;
    private readonly IndexingOptions _options;
    private readonly ILogger _logger;

    public async Task IndexProjectAsync(string projectName, string rootPath,
        FoundationalKnowledge? knowledge = null,
        IReadOnlyList<string>? changedFilesOnly = null,
        CancellationToken ct = default)
    {
        _logger.Information("Indexing {Project} at {Path}", projectName, rootPath);
        var buffer = new GraphBuffer();
        var context = new ExtractorContext
        {
            ProjectName = projectName,
            RootPath = rootPath,
            FoundationalKnowledge = knowledge
        };

        // Load existing file hashes for incremental indexing
        var existingHashes = await _store.GetFileHashesAsync(projectName);

        // Phase 1 — Discovery + Extraction
        var files = DiscoverFiles(rootPath, changedFilesOnly);
        var filesToProcess = FilterByHash(files, rootPath, existingHashes, buffer);

        _logger.Information("Found {Total} files, {Changed} changed",
            files.Count, filesToProcess.Count);

        // Pass 1: Structural nodes (Project, Folder, File)
        CreateStructuralNodes(projectName, rootPath, files, buffer);

        // Pass 2: Run extractors on changed files (parallel per file)
        await ExtractFilesAsync(filesToProcess, rootPath, context, buffer, ct);

        // Phase 2 — Resolution
        // Pass 3: Resolve imports
        ResolveImports(buffer);

        // Pass 4: Resolve calls
        ResolveCalls(buffer);

        // Pass 5: Resolve type references
        ResolveTypeReferences(buffer);

        // Phase 3 — Flush
        // Pass 6: Batch upsert all nodes
        var qnToId = await _store.UpsertNodeBatchAsync(buffer.AllNodes.ToList());

        // Pass 7: Resolve pending edges to IDs and batch insert
        var resolvedEdges = buffer.ResolveEdges(projectName, qnToId);
        await _store.InsertEdgeBatchAsync(resolvedEdges);

        // Pass 8: Store file hashes
        await _store.UpsertFileHashBatchAsync(projectName,
            buffer.AllFileHashes.ToDictionary(kv => kv.Key, kv => kv.Value));

        // Update project metadata
        await _store.UpsertProjectAsync(projectName, localPath: rootPath);

        _logger.Information("Indexed {Project}: {Nodes} nodes, {Edges} edges",
            projectName, buffer.AllNodes.Count, resolvedEdges.Count);
    }

    private List<string> DiscoverFiles(string rootPath,
        IReadOnlyList<string>? changedFilesOnly)
    {
        if (changedFilesOnly != null)
            return changedFilesOnly
                .Select(f => Path.Combine(rootPath, f))
                .Where(File.Exists)
                .ToList();

        var matcher = new Matcher();
        matcher.AddInclude("**/*");
        foreach (var skip in _options.SkipPatterns)
            matcher.AddExclude(skip);

        return matcher.GetResultsInFullPath(rootPath)
            .Where(f => _extractors.Any(e =>
                e.SupportedExtensions.Contains(Path.GetExtension(f))))
            .ToList();
    }

    private List<string> FilterByHash(List<string> files, string rootPath,
        Dictionary<string, string> existingHashes, GraphBuffer buffer)
    {
        var changed = new List<string>();
        foreach (var file in files)
        {
            var relPath = Path.GetRelativePath(rootPath, file);
            var hash = ComputeHash(file);
            buffer.AddFileHash(relPath, hash);

            if (!existingHashes.TryGetValue(relPath, out var existing) ||
                existing != hash)
            {
                changed.Add(file);
            }
        }
        return changed;
    }

    private static string ComputeHash(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var hash = System.IO.Hashing.XxHash3.Hash(bytes);
        return Convert.ToHexString(hash);
    }

    private async Task ExtractFilesAsync(List<string> files, string rootPath,
        ExtractorContext context, GraphBuffer buffer, CancellationToken ct)
    {
        await Parallel.ForEachAsync(files,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxParallelFiles,
                CancellationToken = ct
            },
            async (filePath, ct2) =>
            {
                var ext = Path.GetExtension(filePath);
                var extractor = _extractors.FirstOrDefault(e =>
                    e.SupportedExtensions.Contains(ext));
                if (extractor is null) return;

                try
                {
                    var content = await File.ReadAllTextAsync(filePath, ct2);

                    // Skip files over size limit
                    if (content.Length > _options.MaxFileSizeKb * 1024) return;

                    var result = await extractor.ExtractAsync(filePath, content,
                        context, ct2);

                    foreach (var node in result.Nodes)
                        buffer.AddNode(node);
                    foreach (var edge in result.Edges)
                        buffer.AddEdge(edge);
                    foreach (var call in result.UnresolvedCalls)
                        buffer.AddUnresolvedCall(call);
                    foreach (var import in result.UnresolvedImports)
                        buffer.AddUnresolvedImport(import);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to extract {File}", filePath);
                    // Continue — don't let one file break the pipeline
                }
            });
    }

    private void CreateStructuralNodes(string projectName, string rootPath,
        List<string> files, GraphBuffer buffer)
    {
        // Project node
        buffer.AddNode(new GraphNode
        {
            Project = projectName,
            Label = NodeLabel.Project,
            Name = projectName,
            QualifiedName = projectName
        });

        // Folder and File nodes, with CONTAINS edges
        var folders = new HashSet<string>();
        foreach (var file in files)
        {
            var relPath = Path.GetRelativePath(rootPath, file);
            var relDir = Path.GetDirectoryName(relPath) ?? "";

            // File node
            buffer.AddNode(new GraphNode
            {
                Project = projectName,
                Label = NodeLabel.File,
                Name = Path.GetFileName(file),
                QualifiedName = $"{projectName}:{relPath}",
                FilePath = relPath
            });

            // Folder nodes (walk up the directory tree)
            var dir = relDir;
            while (!string.IsNullOrEmpty(dir) && folders.Add(dir))
            {
                buffer.AddNode(new GraphNode
                {
                    Project = projectName,
                    Label = NodeLabel.Folder,
                    Name = Path.GetFileName(dir),
                    QualifiedName = $"{projectName}:{dir}"
                });

                var parentDir = Path.GetDirectoryName(dir) ?? "";
                var parentQN = string.IsNullOrEmpty(parentDir)
                    ? projectName
                    : $"{projectName}:{parentDir}";
                buffer.AddEdge(new PendingEdge(
                    parentQN,
                    $"{projectName}:{dir}",
                    EdgeType.CONTAINS_FOLDER));

                dir = parentDir;
            }

            // File containment edge
            var folderQN = string.IsNullOrEmpty(relDir)
                ? projectName
                : $"{projectName}:{relDir}";
            buffer.AddEdge(new PendingEdge(
                folderQN,
                $"{projectName}:{relPath}",
                EdgeType.CONTAINS_FILE));
        }
    }
}
```

#### IndexingOptions

```csharp
namespace TC.CodeGraphApi.Services;

public class IndexingOptions
{
    public int MaxParallelFiles { get; set; } = 8;
    public int MaxFileSizeKb { get; set; } = 512;
    public string[] SkipPatterns { get; set; } =
    [
        "**/bin/**", "**/obj/**", "**/node_modules/**",
        "**/wwwroot/lib/**", "**/*.min.js", "**/.git/**",
        "**/packages/**", "**/TestResults/**"
    ];
}
```

### 2.2 — CLI: Scan and Index Commands

```csharp
// src/TC.CodeGraphApi.Console/Program.cs
// Using System.CommandLine for CLI commands

var rootCommand = new RootCommand("CodeGraph CLI");

var migrateCommand = new Command("migrate", "Apply database migrations");
migrateCommand.SetHandler(async () =>
{
    var store = BuildStore(config);
    await store.ApplyMigrationsAsync(config.MigrationsPath);
    Console.WriteLine("Migrations applied.");
});

var indexCommand = new Command("index", "Index a local repository");
indexCommand.AddArgument(new Argument<string>("path", "Path to repository root"));
indexCommand.AddOption(new Option<string>("--name", "Project name (defaults to directory name)"));
indexCommand.AddOption(new Option<bool>("--foundational", "Mark as foundational repo"));
indexCommand.SetHandler(async (string path, string? name, bool foundational) =>
{
    var projectName = name ?? Path.GetFileName(path);
    var pipeline = BuildPipeline(config);

    if (foundational)
        await store.UpsertProjectAsync(projectName, localPath: path, isFoundational: true);

    await pipeline.IndexProjectAsync(projectName, path);
    Console.WriteLine($"Indexed {projectName}: check database for results.");
}, pathArg, nameOpt, foundationalOpt);

var indexAllCommand = new Command("index-all", "Index all configured repos in order");
indexAllCommand.AddArgument(new Argument<string>("base-path",
    "Base directory containing cloned repos"));
indexAllCommand.SetHandler(async (string basePath) =>
{
    var pipeline = BuildPipeline(config);

    // Step 1: Index foundational repos first
    foreach (var foundational in config.FoundationalRepos)
    {
        var path = Path.Combine(basePath, foundational);
        if (!Directory.Exists(path))
        {
            Console.WriteLine($"SKIP (not found): {foundational}");
            continue;
        }
        Console.WriteLine($"Indexing foundational: {foundational}");
        await store.UpsertProjectAsync(foundational, localPath: path,
            isFoundational: true);
        await pipeline.IndexProjectAsync(foundational, path);
    }

    // Step 2: Build foundational knowledge from indexed framework repos
    var knowledge = await BuildFoundationalKnowledge(store);

    // Step 3: Index remaining repos
    var repoDirs = Directory.GetDirectories(basePath)
        .Where(d => !config.FoundationalRepos.Contains(Path.GetFileName(d)));
    foreach (var repoDir in repoDirs)
    {
        var projectName = Path.GetFileName(repoDir);
        Console.WriteLine($"Indexing: {projectName}");
        await pipeline.IndexProjectAsync(projectName, repoDir,
            knowledge: knowledge);
    }

    // Step 4: Cross-repo linking
    Console.WriteLine("Running cross-repo linking...");
    await RunCrossRepoLinking(store);
});

var statsCommand = new Command("stats", "Show graph statistics");
statsCommand.SetHandler(async () =>
{
    // Query and display: project count, node count by label, edge count by type
});

rootCommand.AddCommand(migrateCommand);
rootCommand.AddCommand(indexCommand);
rootCommand.AddCommand(indexAllCommand);
rootCommand.AddCommand(statsCommand);

await rootCommand.InvokeAsync(args);
```

### Phase 2 Deliverable

- CLI can scan a local repo directory and create structural nodes (Project, Folder, File)
- File hashing works for incremental indexing
- Pipeline orchestration framework is in place
- Foundational repo ordering is implemented
- `dotnet run --project src/TC.CodeGraphApi.Console -- index /path/to/repo` works

---

## Phase 3 — C# Extraction (Roslyn)

### Goal
Extract all meaningful code elements from C# repositories using Roslyn's semantic analysis. This is the core of the structural graph.

### 3.1 — CodeGraph.Extractors.CSharp

NuGet packages:
- `Microsoft.CodeAnalysis.CSharp.Workspaces`
- `Microsoft.Build.Locator`
- `Serilog`

#### RoslynExtractor

```csharp
namespace TC.CodeGraphApi.Extractors.CSharp;

public class RoslynExtractor : ICodeExtractor
{
    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string> { ".cs" };

    /// For individual file extraction (fallback when no solution available)
    public async Task<ExtractionResult> ExtractAsync(string filePath,
        string content, ExtractorContext context, CancellationToken ct)
    {
        var tree = CSharpSyntaxTree.ParseText(content, path: filePath);
        var root = await tree.GetRootAsync(ct);

        // Without a compilation, we get syntax-only analysis
        // Still useful for structure, but no resolved types
        var walker = new CodeGraphSyntaxWalker(context, semanticModel: null);
        walker.Visit(root);
        return walker.GetResult();
    }
}
```

#### SolutionAnalyzer

This is where the real power is — loading full solutions for semantic analysis:

```csharp
namespace TC.CodeGraphApi.Extractors.CSharp;

public class SolutionAnalyzer
{
    private readonly ILogger _logger;

    static SolutionAnalyzer()
    {
        // Must be called once before using MSBuildWorkspace
        MSBuildLocator.RegisterDefaults();
    }

    public async Task<IReadOnlyList<ExtractionResult>> AnalyzeSolutionAsync(
        string solutionPath, ExtractorContext context, CancellationToken ct)
    {
        using var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
            _logger.Warning("Workspace warning: {Message}", e.Diagnostic.Message);

        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
        var results = new ConcurrentBag<ExtractionResult>();

        await Parallel.ForEachAsync(solution.Projects, ct, async (project, ct2) =>
        {
            var compilation = await project.GetCompilationAsync(ct2);
            if (compilation is null)
            {
                _logger.Warning("Could not compile project {Project}", project.Name);
                return;
            }

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                try
                {
                    var model = compilation.GetSemanticModel(syntaxTree);
                    var walker = new CodeGraphSyntaxWalker(context, model);
                    walker.Visit(await syntaxTree.GetRootAsync(ct2));
                    results.Add(walker.GetResult());
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to analyze {File}",
                        syntaxTree.FilePath);
                }
            }
        });

        return results.ToList();
    }
}
```

#### CodeGraphSyntaxWalker

The main Roslyn walker that extracts everything:

```csharp
namespace TC.CodeGraphApi.Extractors.CSharp;

public class CodeGraphSyntaxWalker : CSharpSyntaxWalker
{
    private readonly ExtractorContext _context;
    private readonly SemanticModel? _model;
    private readonly List<GraphNode> _nodes = new();
    private readonly List<PendingEdge> _edges = new();
    private readonly List<UnresolvedCall> _calls = new();
    private readonly List<UnresolvedImport> _imports = new();

    // Track current scope for qualified name construction
    private readonly Stack<string> _scopeStack = new();

    public ExtractionResult GetResult() => new()
    {
        Nodes = _nodes,
        Edges = _edges,
        UnresolvedCalls = _calls,
        UnresolvedImports = _imports
    };

    // --- Namespace declarations ---
    public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node) { ... }
    public override void VisitFileScopedNamespaceDeclaration(
        FileScopedNamespaceDeclarationSyntax node) { ... }

    // --- Type declarations ---
    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var symbol = _model?.GetDeclaredSymbol(node);
        var qn = symbol?.ToDisplayString() ?? BuildQualifiedName(node.Identifier.Text);

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            Label = NodeLabel.Class,
            Name = symbol?.Name ?? node.Identifier.Text,
            QualifiedName = qn,
            FilePath = GetRelativePath(node),
            StartLine = GetStartLine(node),
            EndLine = GetEndLine(node),
            Properties = new()
            {
                ["is_abstract"] = node.Modifiers.Any(SyntaxKind.AbstractKeyword),
                ["is_static"] = node.Modifiers.Any(SyntaxKind.StaticKeyword),
                ["is_generic"] = symbol?.IsGenericType ?? false,
                ["base_types"] = GetBaseTypes(symbol)
            }
        });

        // INHERITS edges
        if (symbol?.BaseType is { SpecialType: not SpecialType.System_Object })
        {
            _edges.Add(new PendingEdge(qn,
                symbol.BaseType.ToDisplayString(), EdgeType.INHERITS));
        }

        // IMPLEMENTS edges
        if (symbol is not null)
        {
            foreach (var iface in symbol.AllInterfaces)
                _edges.Add(new PendingEdge(qn,
                    iface.ToDisplayString(), EdgeType.IMPLEMENTS));
        }

        // Check if this is a Consumer<T> — MassTransit consumer detection
        if (symbol is not null)
            DetectConsumer(symbol, qn);

        _scopeStack.Push(qn);
        base.VisitClassDeclaration(node);
        _scopeStack.Pop();
    }

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) { ... }
    public override void VisitRecordDeclaration(RecordDeclarationSyntax node) { ... }
    public override void VisitStructDeclaration(StructDeclarationSyntax node) { ... }
    public override void VisitEnumDeclaration(EnumDeclarationSyntax node) { ... }
    public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node) { ... }

    // --- Members ---
    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _model?.GetDeclaredSymbol(node);
        var qn = symbol?.ToDisplayString() ?? BuildQualifiedName(node.Identifier.Text);

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            Label = NodeLabel.Method,
            Name = symbol?.Name ?? node.Identifier.Text,
            QualifiedName = qn,
            FilePath = GetRelativePath(node),
            StartLine = GetStartLine(node),
            EndLine = GetEndLine(node),
            Properties = new()
            {
                ["signature"] = symbol?.ToDisplayString(
                    SymbolDisplayFormat.MinimallyQualifiedFormat) ?? node.ToString(),
                ["return_type"] = symbol?.ReturnType.ToDisplayString() ?? "unknown",
                ["is_async"] = symbol?.IsAsync ?? node.Modifiers.Any(SyntaxKind.AsyncKeyword),
                ["is_static"] = symbol?.IsStatic ?? node.Modifiers.Any(SyntaxKind.StaticKeyword),
                ["complexity"] = ComputeCyclomaticComplexity(node),
                ["parameter_count"] = symbol?.Parameters.Length ?? node.ParameterList.Parameters.Count,
                ["is_entry_point"] = HasRouteAttribute(node, symbol),
                ["is_test"] = HasTestAttribute(node, symbol)
            }
        });

        // DEFINES_METHOD edge from enclosing type
        if (_scopeStack.Count > 0)
            _edges.Add(new PendingEdge(_scopeStack.Peek(), qn, EdgeType.DEFINES_METHOD));

        // Check for HTTP route attributes → Route node
        DetectRouteEndpoint(node, symbol, qn);

        _scopeStack.Push(qn);
        base.VisitMethodDeclaration(node);
        _scopeStack.Pop();
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node) { ... }
    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        // Extract constructor, detect DI injection via parameters
        var symbol = _model?.GetDeclaredSymbol(node);
        // For each parameter that's an interface type → INJECTS edge
        if (symbol is not null)
        {
            foreach (var param in symbol.Parameters)
            {
                if (param.Type.TypeKind == TypeKind.Interface)
                {
                    var ctorQN = symbol.ToDisplayString();
                    _edges.Add(new PendingEdge(ctorQN,
                        param.Type.ToDisplayString(), EdgeType.INJECTS,
                        new() { ["parameter_name"] = param.Name }));
                }
            }
        }
        base.VisitConstructorDeclaration(node);
    }

    // --- Invocations ---
    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var symbolInfo = _model?.GetSymbolInfo(node);
        if (symbolInfo?.Symbol is IMethodSymbol targetMethod)
        {
            var callerQN = _scopeStack.Count > 0 ? _scopeStack.Peek() : null;
            if (callerQN is not null)
            {
                // Fully resolved call
                _edges.Add(new PendingEdge(callerQN,
                    targetMethod.ToDisplayString(), EdgeType.CALLS,
                    new()
                    {
                        ["confidence"] = 1.0,
                        ["confidence_band"] = "high"
                    }));

                // Check for special patterns
                DetectServiceBusPublish(node, targetMethod, callerQN);
                DetectHttpClientCall(node, targetMethod, callerQN);
                DetectDIRegistration(node, targetMethod, callerQN);
            }
        }
        else if (_scopeStack.Count > 0)
        {
            // Unresolved — record for later matching
            var methodName = GetInvokedMethodName(node);
            if (methodName is not null)
            {
                _calls.Add(new UnresolvedCall(
                    _scopeStack.Peek(),
                    methodName,
                    GetReceiverType(node),
                    0.5));
            }
        }

        base.VisitInvocationExpression(node);
    }

    // --- Using directives ---
    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        var ns = node.Name?.ToString();
        if (ns is not null)
        {
            var fileQN = $"{_context.ProjectName}:{GetRelativePath(node)}";
            _imports.Add(new UnresolvedImport(fileQN, ns));
        }
        base.VisitUsingDirective(node);
    }

    // --- Pattern Detection Methods ---

    private void DetectRouteEndpoint(MethodDeclarationSyntax node,
        IMethodSymbol? symbol, string methodQN)
    {
        // Look for [HttpGet], [HttpPost], [Route] attributes
        // Also check parent class for [ApiController], [Route("api/[controller]")]
        // Create Route node with http_method, route_template properties
        // Create HANDLES edge from Route → Method
    }

    private void DetectServiceBusPublish(InvocationExpressionSyntax node,
        IMethodSymbol method, string callerQN)
    {
        // Detect: serviceBus.Publish(someEvent)
        // or:     _bus.Publish(new OrderCreatedEvent { ... })
        // Extract the event type from the generic argument or argument type
        // Create PUBLISHES edge from caller → event type QN
    }

    private void DetectConsumer(INamedTypeSymbol classSymbol, string classQN)
    {
        // Check if class implements Consumer<T> (or IConsumer<T> from MassTransit)
        // Extract T — that's the event type
        // Create CONSUMES edge from this class → event type QN
        // Check event type for queue routing attributes
    }

    private void DetectHttpClientCall(InvocationExpressionSyntax node,
        IMethodSymbol method, string callerQN)
    {
        // Detect: _httpClient.GetAsync("/api/...")
        //         _httpClient.PostAsJsonAsync("/api/...", payload)
        // Extract URL pattern from string argument
        // Extract HTTP method from the method name (GetAsync → GET)
        // Create HTTP_CALLS edge with url_pattern and http_method properties
    }

    private void DetectDIRegistration(InvocationExpressionSyntax node,
        IMethodSymbol method, string callerQN)
    {
        // Detect: services.AddScoped<IFoo, Foo>()
        //         services.AddTransient<IBar, Bar>()
        //         services.AddSingleton<IBaz, Baz>()
        // Also detect custom registration extension methods from foundational repos
        // Create Service node with lifetime, interface, implementation properties
    }

    // --- Utility Methods ---

    private static int ComputeCyclomaticComplexity(MethodDeclarationSyntax node)
    {
        // Count: if, else if, while, for, foreach, case, catch,
        //        &&, ||, ??, ternary ?:
        // Start at 1 (base path)
        var complexity = 1;
        foreach (var descendant in node.DescendantNodes())
        {
            complexity += descendant switch
            {
                IfStatementSyntax => 1,
                WhileStatementSyntax => 1,
                ForStatementSyntax => 1,
                ForEachStatementSyntax => 1,
                CaseSwitchLabelSyntax => 1,
                CatchClauseSyntax => 1,
                ConditionalExpressionSyntax => 1,
                _ => 0
            };
        }
        foreach (var token in node.DescendantTokens())
        {
            complexity += token.Kind() switch
            {
                SyntaxKind.AmpersandAmpersandToken => 1,
                SyntaxKind.BarBarToken => 1,
                SyntaxKind.QuestionQuestionToken => 1,
                _ => 0
            };
        }
        return complexity;
    }

    private bool HasRouteAttribute(MethodDeclarationSyntax node,
        IMethodSymbol? symbol)
    {
        // Check for [HttpGet], [HttpPost], [HttpPut], [HttpDelete],
        //           [HttpPatch], [Route]
        if (symbol is not null)
        {
            return symbol.GetAttributes().Any(a =>
                a.AttributeClass?.Name is "HttpGetAttribute" or
                    "HttpPostAttribute" or "HttpPutAttribute" or
                    "HttpDeleteAttribute" or "HttpPatchAttribute" or
                    "RouteAttribute");
        }
        // Fallback: check syntax
        return node.AttributeLists.SelectMany(al => al.Attributes)
            .Any(a => a.Name.ToString() is "HttpGet" or "HttpPost" or
                "HttpPut" or "HttpDelete" or "HttpPatch" or "Route");
    }

    private bool HasTestAttribute(MethodDeclarationSyntax node,
        IMethodSymbol? symbol)
    {
        var attrNames = new[] { "Fact", "Theory", "Test", "TestMethod",
            "FactAttribute", "TheoryAttribute", "TestAttribute",
            "TestMethodAttribute" };

        if (symbol is not null)
            return symbol.GetAttributes()
                .Any(a => attrNames.Contains(a.AttributeClass?.Name));

        return node.AttributeLists.SelectMany(al => al.Attributes)
            .Any(a => attrNames.Contains(a.Name.ToString()));
    }
}
```

### 3.2 — NuGet Package Reference Extraction

Separate from Roslyn — parse `.csproj` files directly for `<PackageReference>` elements:

```csharp
namespace TC.CodeGraphApi.Extractors.CSharp;

public class NuGetReferenceExtractor
{
    public IReadOnlyList<(string PackageName, string Version)> ExtractFromProject(
        string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);
        return doc.Descendants("PackageReference")
            .Select(pr => (
                PackageName: pr.Attribute("Include")?.Value ?? "",
                Version: pr.Attribute("Version")?.Value ?? ""))
            .Where(p => !string.IsNullOrEmpty(p.PackageName))
            .ToList();
    }
}
```

For each `TC.*` package reference, create a `REFERENCES_PACKAGE` edge. This is how we detect cross-repo dependencies via shared Models packages.

### 3.3 — Cross-Repo Linker

Runs after all repos are indexed individually:

```csharp
namespace TC.CodeGraphApi.Services;

public class CrossRepoLinker
{
    private readonly IGraphStore _store;
    private readonly ILogger _logger;

    public async Task LinkAsync(CancellationToken ct)
    {
        _logger.Information("Starting cross-repo linking");

        // 1. Match HTTP client calls to controller routes
        await LinkHttpRoutesAsync(ct);

        // 2. Match event publishers to consumers via shared type names
        await LinkMessagingAsync(ct);

        // 3. Match NuGet package references to source repos
        await LinkNuGetPackagesAsync(ct);
    }

    private async Task LinkHttpRoutesAsync(CancellationToken ct)
    {
        // Collect all Route nodes across all projects
        var routes = await _store.FindAllNodesByLabelAsync(NodeLabel.Route);

        // Collect all HTTP_CALLS edges (currently pointing to URL patterns)
        var httpCallEdges = await _store.FindAllEdgesByTypeAsync(EdgeType.HTTP_CALLS);

        // For each HTTP call, try to match URL pattern to a route template
        // Consider: /api/wallet/{id} matches /api/wallet/123
        // Write CrossRepoEdge when source and target are in different projects
    }

    private async Task LinkMessagingAsync(CancellationToken ct)
    {
        // Find all PUBLISHES edges → get the event type QN
        // Find all CONSUMES edges → get the event type QN
        // Where publisher and consumer reference the same event type QN
        //   but are in different projects → CrossRepoEdge

        // The TC.*.Models shared package means the QN is identical
        // e.g., both reference TC.OrdersApi.Models.OrderCreatedEvent
    }

    private async Task LinkNuGetPackagesAsync(CancellationToken ct)
    {
        // Find all NuGetPackage nodes
        // Find all REFERENCES_PACKAGE edges
        // Match package names to project names
        //   (TC.OrdersApi.Models package → TC.OrdersApi project)
        // Create cross-repo dependency edges
    }
}
```

### 3.4 — Pipeline Integration for Solutions

The pipeline needs special handling for `.sln` files — use `SolutionAnalyzer` instead of per-file extraction:

```csharp
// In IndexingPipeline, detect .sln files:
public async Task IndexProjectAsync(...)
{
    // Check for .sln file
    var slnFiles = Directory.GetFiles(rootPath, "*.sln", SearchOption.TopDirectoryOnly);
    if (slnFiles.Length > 0 && _csharpExtractor is not null)
    {
        // Use solution-level analysis for full semantic resolution
        var analyzer = new SolutionAnalyzer(_logger);
        var results = await analyzer.AnalyzeSolutionAsync(
            slnFiles[0], context, ct);

        foreach (var result in results)
        {
            foreach (var node in result.Nodes) buffer.AddNode(node);
            foreach (var edge in result.Edges) buffer.AddEdge(edge);
            foreach (var call in result.UnresolvedCalls) buffer.AddUnresolvedCall(call);
            foreach (var import in result.UnresolvedImports) buffer.AddUnresolvedImport(import);
        }
    }
    else
    {
        // Fallback to per-file extraction
        await ExtractFilesAsync(filesToProcess, rootPath, context, buffer, ct);
    }

    // Also extract NuGet references from .csproj files
    var nugetExtractor = new NuGetReferenceExtractor();
    foreach (var csproj in Directory.GetFiles(rootPath, "*.csproj",
        SearchOption.AllDirectories))
    {
        var refs = nugetExtractor.ExtractFromProject(csproj);
        foreach (var (packageName, version) in refs)
        {
            buffer.AddNode(new GraphNode
            {
                Project = projectName,
                Label = NodeLabel.NuGetPackage,
                Name = packageName,
                QualifiedName = $"nuget:{packageName}",
                Properties = new() { ["version"] = version }
            });
            buffer.AddEdge(new PendingEdge(
                projectName,
                $"nuget:{packageName}",
                EdgeType.REFERENCES_PACKAGE,
                new() { ["version"] = version }));
        }
    }

    // Continue with resolution and flush...
}
```

### 3.5 — Tests

```csharp
public class RoslynExtractorTests
{
    [Fact]
    public async Task Extracts_ClassDeclaration()
    {
        var code = """
            namespace MyApp.Services;
            public class WalletService : IWalletService
            {
                public async Task<decimal> GetBalanceAsync(int walletId) { ... }
            }
            """;
        // Verify: Class node with correct QN, Method node, IMPLEMENTS edge,
        //         DEFINES_METHOD edge
    }

    [Fact]
    public async Task Detects_ControllerRoute()
    {
        var code = """
            [ApiController]
            [Route("api/[controller]")]
            public class WalletController : ControllerBase
            {
                [HttpGet("{id}")]
                public async Task<ActionResult<WalletDto>> Get(int id) { ... }
            }
            """;
        // Verify: Route node with http_method=GET,
        //         route_template=api/wallet/{id}, HANDLES edge
    }

    [Fact]
    public async Task Detects_DIRegistration()
    {
        var code = """
            services.AddScoped<IWalletService, WalletService>();
            """;
        // Verify: Service node with lifetime=Scoped
    }

    [Fact]
    public async Task Detects_ConstructorInjection()
    {
        var code = """
            public class OrderService
            {
                public OrderService(IWalletService wallet, ILogger<OrderService> logger)
                { }
            }
            """;
        // Verify: INJECTS edges for IWalletService (not ILogger — filter framework types)
    }

    [Fact]
    public async Task Detects_MassTransitConsumer()
    {
        var code = """
            public class OrderCreatedConsumer : Consumer<OrderCreatedEvent>
            {
                public Task Consume(ConsumeContext<OrderCreatedEvent> context) { ... }
            }
            """;
        // Verify: CONSUMES edge to OrderCreatedEvent
    }

    [Fact]
    public async Task Detects_ServiceBusPublish()
    {
        var code = """
            await _serviceBus.Publish(new OrderCreatedEvent { OrderId = order.Id });
            """;
        // Verify: PUBLISHES edge with event type
    }

    [Fact]
    public async Task Detects_HttpClientCall()
    {
        var code = """
            var result = await _httpClient.GetAsync("/api/wallet/123");
            """;
        // Verify: HTTP_CALLS edge with url_pattern and http_method
    }

    [Fact]
    public async Task Computes_CyclomaticComplexity()
    {
        var code = """
            public int Complex(int x, bool flag)
            {
                if (x > 0 && flag)
                    return x;
                else if (x < 0 || !flag)
                    return -x;
                for (int i = 0; i < x; i++) { }
                return x ?? 0;
            }
            """;
        // Verify: complexity = 7 (1 base + 2 if + 2 logical + 1 for + 1 ??)
    }
}

public class NuGetReferenceExtractorTests
{
    [Fact]
    public void Extracts_PackageReferences()
    {
        var csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="TC.OrdersApi.Models" Version="1.2.3" />
                <PackageReference Include="Dapper" Version="2.1.0" />
              </ItemGroup>
            </Project>
            """;
        // Verify: two packages extracted with correct names and versions
    }
}

public class CrossRepoLinkerTests
{
    [Fact]
    public async Task Links_HttpCalls_ToRoutes_AcrossProjects()
    {
        // Setup: Project A has Route node for GET /api/wallet/{id}
        //        Project B has HTTP_CALLS edge to /api/wallet/123
        // Verify: CrossRepoEdge created linking B → A
    }

    [Fact]
    public async Task Links_EventPublisher_ToConsumer_AcrossProjects()
    {
        // Setup: Project A publishes TC.Orders.Models.OrderCreatedEvent
        //        Project B consumes TC.Orders.Models.OrderCreatedEvent
        // Verify: CrossRepoEdge created
    }
}
```

### Phase 3 Deliverable

- Index TC.Common.ServiceStack and understand its patterns
- Index TC.DomainInventoryApi with full Roslyn semantic analysis
- Graph contains: classes, methods, calls, DI, routes, consumers, publishers, NuGet refs
- Cross-repo linking connects shared types across repos
- CLI: `dotnet run --project src/TC.CodeGraphApi.Console -- index-all /path/to/repos`

---

## Phase 4 — Claude Analysis + CODEGRAPH.md

### Goal
Use the Anthropic API to analyze each repository's code and generate natural language documentation with confidence indicators.

### 4.1 — TC.CodeGraphApi.Services (Claude Analysis)

NuGet packages:
- `Anthropic.SDK` (official .NET SDK)
- `Serilog`

#### ICodeAnalyzer interface

```csharp
namespace TC.CodeGraphApi.Services;

public interface ICodeAnalyzer
{
    /// Analyze an entire repository and produce a repo-level summary
    Task<RepoAnalysis> AnalyzeRepositoryAsync(string projectName,
        string rootPath, CancellationToken ct = default);

    /// Analyze a single project within a repo
    Task<ProjectAnalysis> AnalyzeProjectAsync(string projectName,
        string projectPath, string repoContext, CancellationToken ct = default);

    /// Re-analyze based on a diff (incremental update)
    Task<AnalysisUpdate?> AnalyzeChangesAsync(string projectName,
        string rootPath, string diff, string commitMessage,
        string existingSummary, CancellationToken ct = default);
}

public record RepoAnalysis(
    string Summary,
    ConfidenceLevel Confidence,
    IReadOnlyList<ProjectAnalysis> Projects);

public record ProjectAnalysis(
    string ProjectName,
    string Summary,
    ConfidenceLevel Confidence,
    IReadOnlyList<EndpointDescription> Endpoints,
    IReadOnlyList<ServiceDescription> Services,
    IReadOnlyList<string> ExternalDependencies,
    IReadOnlyList<string> DatabaseTables);

public record EndpointDescription(
    string Route,
    string HttpMethod,
    string Description,
    string? RequestModel,
    string? ResponseModel);

public record ServiceDescription(
    string Name,
    string Description,
    string? InterfaceName,
    string Lifetime);

public record AnalysisUpdate(
    string UpdatedSummary,
    ConfidenceLevel Confidence,
    string ChangeDescription);
```

#### ClaudeCodeAnalyzer

```csharp
namespace TC.CodeGraphApi.Services;

public class ClaudeCodeAnalyzer : ICodeAnalyzer
{
    private readonly AnthropicClient _client;
    private readonly AnalysisOptions _options;
    private readonly IGraphStore _store;
    private readonly ILogger _logger;

    public async Task<RepoAnalysis> AnalyzeRepositoryAsync(
        string projectName, string rootPath, CancellationToken ct)
    {
        // 1. Gather context from the graph (already indexed structural data)
        var nodes = await _store.SearchNodesAsync(projectName, "%");
        var graphContext = BuildGraphContext(nodes);

        // 2. Read key files for the repo
        var keyFiles = await GatherKeyFiles(rootPath);

        // 3. Build the prompt
        var prompt = BuildRepoAnalysisPrompt(projectName, graphContext, keyFiles);

        // 4. Call Claude
        var response = await _client.Messages.CreateAsync(new()
        {
            Model = _options.Model,
            MaxTokens = _options.MaxTokensPerAnalysis,
            Messages = [new() { Role = "user", Content = prompt }],
            System = GetSystemPrompt()
        }, ct);

        // 5. Parse structured response
        return ParseRepoAnalysis(response.Content);
    }

    private string GetSystemPrompt() => """
        You are analyzing source code for a domain name reseller and auctioneer.
        The company operates HugeDomains.com (domain resale), DropCatch.com
        (domain backorders and auctions), and NameBright.com (full domain management).

        Key business concepts:
        - Drop catching: competing to register expiring domains at the moment they
          become available
        - Domain valuation: AI-augmented scoring of domain value before purchase
        - EPP: Extensible Provisioning Protocol for registry communication
        - Backorders: customer requests to catch specific expiring domains
        - Auctions: competitive bidding on caught or listed domains

        When describing code, use domain industry business terms.
        Be specific about what the code does, not just its structure.
        If you cannot determine the business purpose with confidence, say so.

        Respond in JSON format matching the provided schema.
        """;

    private async Task<IReadOnlyList<(string Path, string Content)>> GatherKeyFiles(
        string rootPath)
    {
        var files = new List<(string, string)>();

        // Priority files that reveal intent:
        // 1. Program.cs / Startup.cs — DI registration, middleware, configuration
        // 2. *Controller.cs — API surface
        // 3. *Service.cs — Business logic
        // 4. *Consumer.cs — Event handlers
        // 5. *.Models/**/*.cs — Public contracts
        // 6. appsettings.json — Configuration structure (redact secrets)

        var patterns = new[]
        {
            ("**/Program.cs", 1),
            ("**/Startup.cs", 1),
            ("**/*Controller*.cs", 2),
            ("**/*Service*.cs", 3),
            ("**/*Consumer*.cs", 4),
            ("**/*Handler*.cs", 4),
            ("**/Models/**/*.cs", 5),
            ("**/appsettings*.json", 6)
        };

        foreach (var (pattern, _) in patterns.OrderBy(p => p.Item2))
        {
            var matcher = new Matcher();
            matcher.AddInclude(pattern);
            foreach (var skip in new[] { "**/bin/**", "**/obj/**" })
                matcher.AddExclude(skip);

            foreach (var match in matcher.GetResultsInFullPath(rootPath).Take(20))
            {
                var content = await File.ReadAllTextAsync(match);
                if (content.Length <= _options.MaxFileSizeKb * 1024)
                {
                    var relPath = Path.GetRelativePath(rootPath, match);
                    files.Add((relPath, content));
                }
            }
        }

        // Respect token budget — truncate if total content is too large
        return TruncateToTokenBudget(files, _options.MaxContextTokens);
    }

    private string BuildRepoAnalysisPrompt(string projectName,
        string graphContext, IReadOnlyList<(string Path, string Content)> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Analyze Repository: {projectName}");
        sb.AppendLine();
        sb.AppendLine("## Graph Context (already extracted)");
        sb.AppendLine(graphContext);
        sb.AppendLine();
        sb.AppendLine("## Source Files");
        foreach (var (path, content) in files)
        {
            sb.AppendLine($"### {path}");
            sb.AppendLine("```csharp");
            sb.AppendLine(content);
            sb.AppendLine("```");
            sb.AppendLine();
        }
        sb.AppendLine("## Instructions");
        sb.AppendLine("""
            Analyze this repository and produce:
            1. A repo-level summary (2-4 paragraphs) describing what this service does
               in business terms, what it depends on, and what depends on it.
            2. A confidence level (high/medium/low) for your analysis.
            3. For each project/assembly in the solution, a project-level summary including:
               - What it does
               - Its public endpoints (if any) with descriptions
               - Its services with descriptions
               - External dependencies (databases, other APIs, message queues)
               - Database tables it accesses

            Respond as JSON matching this schema:
            {
              "summary": "string",
              "confidence": "high|medium|low",
              "projects": [
                {
                  "projectName": "string",
                  "summary": "string",
                  "confidence": "high|medium|low",
                  "endpoints": [
                    { "route": "string", "httpMethod": "string",
                      "description": "string",
                      "requestModel": "string|null",
                      "responseModel": "string|null" }
                  ],
                  "services": [
                    { "name": "string", "description": "string",
                      "interfaceName": "string|null", "lifetime": "string" }
                  ],
                  "externalDependencies": ["string"],
                  "databaseTables": ["string"]
                }
              ]
            }
            """);
        return sb.ToString();
    }
}
```

#### AnalysisOptions

```csharp
namespace TC.CodeGraphApi.Services;

public class AnalysisOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-4-6";
    public int MaxTokensPerAnalysis { get; set; } = 8192;
    public int MaxContextTokens { get; set; } = 100_000;
    public int MaxFileSizeKb { get; set; } = 512;
}
```

### 4.2 — CODEGRAPH.md Generator

```csharp
namespace TC.CodeGraphApi.Services;

public class CodeGraphDocGenerator
{
    /// Generate repo-level CODEGRAPH.md
    public string GenerateRepoDoc(string projectName, RepoAnalysis analysis,
        IReadOnlyList<CrossRepoEdge> inboundDeps,
        IReadOnlyList<CrossRepoEdge> outboundDeps)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {projectName}");
        sb.AppendLine();
        sb.AppendLine($"> **Confidence**: {analysis.Confidence}");
        sb.AppendLine($"> **Last analyzed**: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"> *This file is auto-generated by CodeGraph. Do not edit manually.*");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine(analysis.Summary);
        sb.AppendLine();

        if (outboundDeps.Count > 0)
        {
            sb.AppendLine("## Dependencies (this service calls)");
            sb.AppendLine();
            foreach (var dep in outboundDeps.GroupBy(d => d.TargetProject))
            {
                sb.AppendLine($"- **{dep.Key}**");
                foreach (var edge in dep)
                    sb.AppendLine($"  - {edge.Type}: {FormatEdgeProperties(edge)}");
            }
            sb.AppendLine();
        }

        if (inboundDeps.Count > 0)
        {
            sb.AppendLine("## Dependents (these services call us)");
            sb.AppendLine();
            foreach (var dep in inboundDeps.GroupBy(d => d.SourceProject))
            {
                sb.AppendLine($"- **{dep.Key}**");
                foreach (var edge in dep)
                    sb.AppendLine($"  - {edge.Type}: {FormatEdgeProperties(edge)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Projects");
        sb.AppendLine();
        foreach (var project in analysis.Projects)
        {
            sb.AppendLine($"### {project.ProjectName}");
            sb.AppendLine();
            sb.AppendLine(project.Summary);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// Generate project-level CODEGRAPH.md
    public string GenerateProjectDoc(ProjectAnalysis analysis)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {analysis.ProjectName}");
        sb.AppendLine();
        sb.AppendLine($"> **Confidence**: {analysis.Confidence}");
        sb.AppendLine($"> **Last analyzed**: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"> *This file is auto-generated by CodeGraph. Do not edit manually.*");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine(analysis.Summary);
        sb.AppendLine();

        if (analysis.Endpoints.Count > 0)
        {
            sb.AppendLine("## Endpoints");
            sb.AppendLine();
            sb.AppendLine("| Method | Route | Description | Request | Response |");
            sb.AppendLine("|--------|-------|-------------|---------|----------|");
            foreach (var ep in analysis.Endpoints)
            {
                sb.AppendLine($"| {ep.HttpMethod} | `{ep.Route}` | {ep.Description} " +
                    $"| {ep.RequestModel ?? "-"} | {ep.ResponseModel ?? "-"} |");
            }
            sb.AppendLine();
        }

        if (analysis.Services.Count > 0)
        {
            sb.AppendLine("## Services");
            sb.AppendLine();
            foreach (var svc in analysis.Services)
            {
                sb.AppendLine($"### {svc.Name}");
                if (svc.InterfaceName is not null)
                    sb.AppendLine($"*Implements `{svc.InterfaceName}` ({svc.Lifetime})*");
                sb.AppendLine();
                sb.AppendLine(svc.Description);
                sb.AppendLine();
            }
        }

        if (analysis.ExternalDependencies.Count > 0)
        {
            sb.AppendLine("## External Dependencies");
            sb.AppendLine();
            foreach (var dep in analysis.ExternalDependencies)
                sb.AppendLine($"- {dep}");
            sb.AppendLine();
        }

        if (analysis.DatabaseTables.Count > 0)
        {
            sb.AppendLine("## Database Tables");
            sb.AppendLine();
            foreach (var table in analysis.DatabaseTables)
                sb.AppendLine($"- `{table}`");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
```

### 4.3 — CLI Commands for Analysis

```csharp
var analyzeCommand = new Command("analyze", "Run Claude analysis on a repository");
analyzeCommand.AddArgument(new Argument<string>("path", "Path to repository root"));
analyzeCommand.AddOption(new Option<string>("--name", "Project name"));
analyzeCommand.AddOption(new Option<bool>("--write-docs",
    "Write CODEGRAPH.md files to the repository"));
analyzeCommand.SetHandler(async (string path, string? name, bool writeDocs) =>
{
    var projectName = name ?? Path.GetFileName(path);
    var analyzer = BuildAnalyzer(config);
    var docGenerator = new CodeGraphDocGenerator();

    Console.WriteLine($"Analyzing {projectName} with Claude...");
    var analysis = await analyzer.AnalyzeRepositoryAsync(projectName, path);

    Console.WriteLine($"Confidence: {analysis.Confidence}");
    Console.WriteLine(analysis.Summary);

    if (writeDocs)
    {
        // Write repo-level CODEGRAPH.md
        var inbound = await store.FindCrossRepoEdgesAsync(projectName);
        var outbound = await store.FindCrossRepoEdgesAsync(projectName);
        var repoDoc = docGenerator.GenerateRepoDoc(projectName, analysis,
            inbound, outbound);
        await File.WriteAllTextAsync(Path.Combine(path, "CODEGRAPH.md"), repoDoc);

        // Write project-level CODEGRAPH.md files
        foreach (var project in analysis.Projects)
        {
            var projectPath = FindProjectDirectory(path, project.ProjectName);
            if (projectPath is not null)
            {
                var projectDoc = docGenerator.GenerateProjectDoc(project);
                await File.WriteAllTextAsync(
                    Path.Combine(projectPath, "CODEGRAPH.md"), projectDoc);
            }
        }

        Console.WriteLine("CODEGRAPH.md files written.");
    }

    // Store summary in database
    var sourceHash = ComputeRepoHash(path);
    await store.UpsertProjectSummaryAsync(projectName, analysis.Summary,
        analysis.Confidence, sourceHash);
});
```

### Phase 4 Deliverable

- `dotnet run --project src/TC.CodeGraphApi.Console -- analyze /path/to/repo --write-docs` works
- CODEGRAPH.md files are generated with business-level descriptions
- Confidence indicators reflect actual analysis quality
- Summaries are stored in the database for MCP server queries

---

## Phase 5 — MCP Server + Query Engine

### Goal
Make the knowledge graph queryable through an MCP server that Claude can use during conversations.

### 5.1 — TC.CodeGraphApi.Services (Query Engine)

```csharp
namespace TC.CodeGraphApi.Services;

public class GraphQueryEngine
{
    private readonly IGraphStore _store;

    /// Search for nodes by name pattern, label, project
    public async Task<SearchResult> SearchAsync(SearchRequest request) { ... }

    /// Trace call path (callers/callees) from a function
    public async Task<IReadOnlyList<TraversalEntry>> TraceCallPathAsync(
        string functionName, string? project, TraceDirection direction,
        int maxDepth = 3) { ... }

    /// Trace data lineage: follow a model from origin through all services
    public async Task<DataLineageResult> TraceDataLineageAsync(
        string modelName, string? project) { ... }

    /// Find all consumers of an event/endpoint/model
    public async Task<IReadOnlyList<ConsumerInfo>> FindConsumersAsync(
        string name, string? project) { ... }

    /// Find all publishers to a queue/exchange
    public async Task<IReadOnlyList<PublisherInfo>> FindPublishersAsync(
        string name, string? project) { ... }

    /// Get architecture overview for a project
    public async Task<ArchitectureReport> GetArchitectureAsync(
        string project) { ... }

    /// Find repos with no inbound or outbound dependencies
    public async Task<IReadOnlyList<ProjectInfo>> FindArchivalCandidatesAsync() { ... }

    /// Find repos not updated within a given timeframe
    public async Task<IReadOnlyList<ProjectInfo>> FindStaleReposAsync(
        TimeSpan threshold) { ... }
}
```

### 5.2 — TC.CodeGraphApi.Services (MCP Server)

NuGet packages:
- `ModelContextProtocol`

```csharp
namespace TC.CodeGraphApi.Services;

public class CodeGraphMcpServer
{
    private readonly GraphQueryEngine _query;
    private readonly IGraphStore _store;
    private readonly ILogger _logger;

    [McpTool("search_graph",
        Description = "Search for services, endpoints, models, events, or any code element by name pattern. Supports filtering by type (class, method, route, event, etc.) and project.")]
    public async Task<string> SearchGraph(
        [McpParameter(Description = "Name pattern to search for (supports % wildcards)")] string namePattern,
        [McpParameter(Description = "Filter by node type: class, method, route, service, event, queue, table, etc.")] string? label = null,
        [McpParameter(Description = "Filter by project/repository name")] string? project = null,
        [McpParameter(Description = "Max results")] int limit = 20)
    {
        var result = await _query.SearchAsync(new SearchRequest(
            namePattern, label, project, limit));
        return FormatSearchResults(result);
    }

    [McpTool("trace_call_path",
        Description = "Trace callers or callees of a function/method. Shows the call chain across services. Use direction 'inbound' to find callers, 'outbound' to find callees, 'both' for both.")]
    public async Task<string> TraceCallPath(
        [McpParameter(Required = true, Description = "Function or method name to trace")] string functionName,
        [McpParameter(Description = "Trace direction: inbound, outbound, or both")] string direction = "both",
        [McpParameter(Description = "How many levels deep to trace")] int depth = 3,
        [McpParameter(Description = "Filter by project")] string? project = null)
    {
        var dir = Enum.Parse<TraceDirection>(direction, ignoreCase: true);
        var result = await _query.TraceCallPathAsync(functionName, project, dir, depth);
        return FormatTraversalResults(result);
    }

    [McpTool("trace_data_lineage",
        Description = "Follow a data model from its database origin through all services that produce, transform, or consume it. Shows the complete data flow across the system.")]
    public async Task<string> TraceDataLineage(
        [McpParameter(Required = true, Description = "Model/DTO/event class name to trace")] string modelName,
        [McpParameter(Description = "Filter by project")] string? project = null)
    {
        var result = await _query.TraceDataLineageAsync(modelName, project);
        return FormatDataLineage(result);
    }

    [McpTool("find_consumers",
        Description = "Find all services/methods that consume a given event, endpoint, or model. Shows cross-repo dependencies.")]
    public async Task<string> FindConsumers(
        [McpParameter(Required = true, Description = "Event, endpoint, or model name")] string name,
        [McpParameter(Description = "Filter by project")] string? project = null)
    {
        var result = await _query.FindConsumersAsync(name, project);
        return FormatConsumers(result);
    }

    [McpTool("find_publishers",
        Description = "Find all services that publish to a given queue, exchange, or event type.")]
    public async Task<string> FindPublishers(
        [McpParameter(Required = true, Description = "Queue, exchange, or event name")] string name,
        [McpParameter(Description = "Filter by project")] string? project = null)
    {
        var result = await _query.FindPublishersAsync(name, project);
        return FormatPublishers(result);
    }

    [McpTool("get_service_summary",
        Description = "Get the natural language description of a service/repository, including what it does, its endpoints, dependencies, and what depends on it.")]
    public async Task<string> GetServiceSummary(
        [McpParameter(Required = true, Description = "Project/repository name")] string project)
    {
        var summary = await _store.GetProjectSummaryAsync(project);
        if (summary is null)
            return $"No analysis available for '{project}'. Run analysis first.";
        return $"# {project}\n\nConfidence: {summary.Confidence}\n\n{summary.Summary}";
    }

    [McpTool("get_architecture",
        Description = "Get architecture overview for a project — hotspots, dependency analysis, complexity metrics.")]
    public async Task<string> GetArchitecture(
        [McpParameter(Required = true, Description = "Project name")] string project)
    {
        var report = await _query.GetArchitectureAsync(project);
        return FormatArchitectureReport(report);
    }

    [McpTool("find_archival_candidates",
        Description = "Find repositories with no inbound or outbound dependencies — candidates for archival.")]
    public async Task<string> FindArchivalCandidates()
    {
        var candidates = await _query.FindArchivalCandidatesAsync();
        return FormatArchivalCandidates(candidates);
    }

    [McpTool("list_projects",
        Description = "List all indexed repositories with metadata: last indexed date, language, staleness, whether foundational.")]
    public async Task<string> ListProjects()
    {
        var projects = await _store.ListProjectsAsync();
        return FormatProjectList(projects);
    }

    [McpTool("get_code_snippet",
        Description = "Read actual source code from a repository. Use when the graph and summaries don't provide enough detail.")]
    public async Task<string> GetCodeSnippet(
        [McpParameter(Required = true, Description = "Project name")] string project,
        [McpParameter(Required = true, Description = "File path relative to repo root")] string filePath,
        [McpParameter(Description = "Start line (0 for beginning)")] int startLine = 0,
        [McpParameter(Description = "End line (0 for entire file)")] int endLine = 0)
    {
        // Read from local repo path stored in project metadata
        var projectInfo = (await _store.ListProjectsAsync())
            .FirstOrDefault(p => p.Name == project);
        if (projectInfo?.LocalPath is null)
            return $"Project '{project}' not found or has no local path.";

        var fullPath = Path.Combine(projectInfo.LocalPath, filePath);
        if (!File.Exists(fullPath))
            return $"File not found: {filePath}";

        var lines = await File.ReadAllLinesAsync(fullPath);
        var start = startLine > 0 ? startLine - 1 : 0;
        var end = endLine > 0 ? Math.Min(endLine, lines.Length) : lines.Length;

        return string.Join('\n', lines[start..end]);
    }

    [McpTool("index_repository",
        Description = "Trigger indexing of a repository. Use for manual re-indexing.")]
    public async Task<string> IndexRepository(
        [McpParameter(Required = true, Description = "Path to repository")] string repoPath,
        [McpParameter(Description = "Project name (defaults to directory name)")] string? name = null)
    {
        // Delegate to pipeline
        var projectName = name ?? Path.GetFileName(repoPath);
        await _pipeline.IndexProjectAsync(projectName, repoPath);
        return $"Indexed {projectName} successfully.";
    }

    [McpTool("get_graph_schema",
        Description = "Describe the available node types, edge types, and their properties in the knowledge graph.")]
    public Task<string> GetGraphSchema()
    {
        return Task.FromResult("""
            ## Node Types
            Project, Namespace, Folder, File, Class, Interface, Enum, Struct,
            Record, Function, Method, Property, Constructor, Delegate, Route,
            Service, Table, View, StoredProcedure, Event, Queue, Exchange,
            Component, Module, Job, NuGetPackage

            ## Edge Types
            CONTAINS_FILE, CONTAINS_FOLDER, CONTAINS_NAMESPACE, DEFINES,
            DEFINES_METHOD, CALLS, IMPORTS, IMPLEMENTS, INHERITS, USES_TYPE,
            INJECTS, HTTP_CALLS, HANDLES, QUERIES, PUBLISHES, CONSUMES,
            REFERENCES_PACKAGE, RENDERS, SUBSCRIBES, FILE_CHANGES_WITH, SCHEDULES

            ## Key Properties
            - Route: http_method, route_template, handler
            - Service: lifetime, interface, implementation
            - Event: queue_name, exchange_name
            - Method: signature, return_type, is_async, complexity, is_entry_point
            - CALLS edge: confidence, confidence_band
            - HTTP_CALLS edge: url_pattern, http_method, source_repo, target_repo
            """);
    }
}
```

### 5.3 — MCP Server Hosting

MCP server runs as a CLI subcommand of the Console project:

```csharp
var mcpCommand = new Command("mcp", "Start MCP server (stdio transport)");
mcpCommand.SetHandler(async () =>
{
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddSingleton<IGraphStore>(store);
    builder.Services.AddSingleton<GraphQueryEngine>();
    builder.Services.AddMcpServer()
        .WithStdioTransport()
        .WithTools<CodeGraphMcpServer>();

    var host = builder.Build();
    await host.RunAsync();
});
```

MCP client configuration (for Claude Code or other IDE):
```json
{
  "mcpServers": {
    "codegraph": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/src/TC.CodeGraphApi.Console", "--", "mcp"],
      "env": {
        "CODEGRAPH_CONNECTION": "Server=localhost;Database=codegraph;..."
      }
    }
  }
}
```

### Phase 5 Deliverable

- MCP server running and connectable from Claude Code
- All 12 tools functional
- Ask Claude "what services consume OrderCreatedEvent?" and get a correct answer
- Ask Claude "describe TC.DomainInventoryApi" and get the CODEGRAPH.md summary
- Ask Claude "what would break if we changed the WalletDto model?" and get dependency analysis

---

## Phase 6 — Post Proof-of-Concept (Not Detailed Here)

Once the proof of concept is validated with TC.Common.ServiceStack, TC.Jarvis, and TC.DomainInventoryApi:

- **GitLab API integration** — Replace local repo scanning with GitLab discovery, clone/pull
- **CI webhook integration** — Trigger re-index and doc updates on push
- **CODEGRAPH.md commit-back** — Write docs to repos via GitLab API
- **Auto-discovery** — Periodic scan for new repos
- **TypeScript/Angular extractor** — Node.js sidecar
- **SQL extractor** — ScriptDom
- **ColdFusion extractor** — Regex best-effort
- **Job scheduler integration** — Read from scheduler database
- **TC.CodeGraphApi** — Full REST API + hosted MCP server
- **Scale to all repos** — Index everything, deprioritize svn_archive

---

## Configuration (appsettings.json)

```json
{
  "ConnectionStrings": {
    "CodeGraph": "Server=localhost;Port=3306;Database=codegraph;User=codegraph;Password=codegraph;AllowUserVariables=true;UseAffectedRows=false"
  },
  "Indexing": {
    "FoundationalRepos": [
      "TC.Common.ServiceStack"
    ],
    "MaxParallelFiles": 8,
    "MaxFileSizeKb": 512,
    "SkipPatterns": [
      "**/bin/**", "**/obj/**", "**/node_modules/**",
      "**/wwwroot/lib/**", "**/*.min.js", "**/.git/**",
      "**/packages/**", "**/TestResults/**"
    ],
    "MigrationsPath": "sql/migrations"
  },
  "Analysis": {
    "ApiKey": "",
    "Model": "claude-sonnet-4-6",
    "MaxTokensPerAnalysis": 8192,
    "MaxContextTokens": 100000,
    "MaxFileSizeKb": 512
  }
}
```

---

## Development Order Summary

| Step | What | Project | Depends On | Key Output |
|------|------|---------|-----------|------------|
| 1.1 | Solution scaffolding | All | — | Compiling solution with all project references |
| 1.2 | Domain model | Models | — | Enums, records, pipeline types |
| 1.3 | SQL migrations | sql/ | — | Database schema |
| 1.4 | Storage (Dapper) | Data | 1.2, 1.3 | Working IGraphStore with batch ops and traversal |
| 1.5 | Storage tests | Data.Tests | 1.4 | Verified storage layer |
| 2.1 | Pipeline framework | Services | 1.2, 1.4 | GraphBuffer, IndexingPipeline, ICodeExtractor |
| 2.2 | CLI commands | Console | 2.1 | `migrate`, `index`, `index-all`, `stats` commands |
| 3.1 | Roslyn extractor | Extractors.CSharp | 2.1 | Classes, methods, calls, DI extraction |
| 3.2 | NuGet ref extraction | Extractors.CSharp | 2.1 | Package dependency nodes and edges |
| 3.3 | Cross-repo linker | Services | 3.1, 3.2 | HTTP, messaging, and NuGet cross-repo edges |
| 3.4 | Solution-level analysis | Extractors.CSharp | 3.1 | Full semantic resolution via MSBuildWorkspace |
| 3.5 | Extractor tests | Extractors.CSharp.Tests | 3.1-3.4 | Verified extraction accuracy |
| 4.1 | Claude analyzer | Services | 1.4 | RepoAnalysis and ProjectAnalysis from Claude |
| 4.2 | Doc generator | Services | 4.1 | CODEGRAPH.md file generation |
| 4.3 | Analyze command | Console | 4.1, 4.2 | `analyze --write-docs` command |
| 5.1 | Query engine | Services | 1.4 | Search, traversal, data lineage, archival queries |
| 5.2 | MCP server | Services | 5.1 | 12 MCP tools |
| 5.3 | MCP hosting | Console | 5.2 | Running MCP server connectable from Claude Code |
