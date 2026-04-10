# CodeGraph

A self-maintaining .NET 9 service that indexes source repositories into a queryable knowledge graph backed by Neo4j, with a personal memory graph for conversational tooling. It produces two outputs: a structural graph of code and service relationships, and generated `CODEGRAPH.md` files that describe each repository in plain language. An MCP server lets compatible clients query the graph as a domain expert for the indexed codebase.

## Core Principle

**Self-maintaining.** If it requires human attention to stay accurate, it will rot. Auto-discovery of new repos, CI-triggered updates, direct commits for doc changes, automatic cleanup of removed repos.

## Features

- **Knowledge Graph Indexing** — Extracts classes, methods, routes, events, queues, jobs, tables, and their relationships from source code into a Neo4j graph
- **Multi-Language Extraction** — C# (Roslyn semantic analysis), TypeScript/Angular (Node.js sidecar), T-SQL (ScriptDom), Tree-sitter (multi-language fallback)
- **AI-Powered Analysis** — Anthropic Batches API generates natural language summaries with confidence indicators for every project
- **Cross-Repo Linking** — Connects HTTP calls, MassTransit events, and NuGet package references across repositories
- **Codebase Health Metrics** — Tracks file churn, cyclomatic complexity, truck factor, coupling centrality, and risk scores
- **Event-Driven Pipeline** — Asynchronous processing via MassTransit consumers with independent failure isolation and automatic retry
- **Memory Graph** — Personal knowledge graph stored in Neo4j with vector search, fuzzy entity matching, conflict detection, and relationship traversal — accessible via MCP tools and REST API
- **MCP Server** — 20+ tools for compatible assistants to query the graph, trace call paths, find consumers/publishers, explore architecture, check health, analyze impact, detect clusters, store/query memory, and read conventions
- **REST API** — Full query interface for projects, nodes, edges, graph overview, health data, search, memory, and wiki
- **Angular Dashboard** — Browse repositories, explore the graph, view health hotspots, ask questions via streaming chat, and manage the wiki
- **Hierarchical Wiki** — Multi-section wiki with nested pages (up to 3 levels), file attachments, raw content support, revision history, admin-managed sections, and MCP integration
- **Global Search** — Unified search across repositories and nodes with relevance ranking
- **Service Cluster Detection** — Louvain community detection algorithm discovers tightly-coupled repository groups from the cross-repo dependency graph, with edge-type weighting, bridge repo identification, and D3.js cluster visualization
- **Blast Radius / Impact Analysis** — Given a changed code element or file, traverses inbound dependencies to identify everything affected, classifying each by risk level (Critical/High/Medium/Low) with cross-repo awareness
- **Anthropic Circuit Breaker** — Resilient API integration with exponential backoff, transient detection, and automatic circuit breaking

## Solution Structure

```
CodeGraph/
├── src/
│   ├── CodeGraph.Api/                         # ASP.NET Core API host, controllers, consumers, DI
│   │   └── Consumers/                         # MassTransit event consumers
│   ├── CodeGraph.Models/                      # Domain model, request/response DTOs (zero dependencies)
│   │   ├── Messages/                          # MassTransit event messages
│   │   ├── Memory/                            # Memory graph models (entities, relationships, observations)
│   │   ├── Requests/                          # API request models
│   │   └── Responses/                         # API response models
│   ├── CodeGraph.Services/                    # All business logic, query engine, AI analysis, MCP tools
│   │   └── Memory/                            # Memory normalization, retrieval, embedding services
│   ├── CodeGraph.Data/                        # Store interfaces (IGraphStore, IMemoryGraphStore, etc.)
│   ├── CodeGraph.Data.Neo4j/                  # Neo4j implementations of all store interfaces
│   ├── CodeGraph.Extractors.CSharp/           # Roslyn semantic extraction
│   ├── CodeGraph.Extractors.TypeScript/       # TypeScript compiler API sidecar
│   ├── CodeGraph.Extractors.Sql/              # T-SQL ScriptDom parsing
│   ├── CodeGraph.Extractors.TreeSitter/       # Tree-sitter multi-language extraction
│   └── CodeGraph.Jobs/                        # Scheduled jobs (batch processing, discovery)
├── tests/
│   ├── CodeGraph.Tests/                       # Unit tests (xUnit + Shouldly)
│   └── CodeGraph.Jobs.Tests/                  # Job-specific tests
├── CodeGraphWeb/                              # Angular dashboard
└── src/CodeGraph.Api/Migrations/              # Neo4j schema migrations bundled with the API
```

### Dependency Flow

```
Models ← Data ← Services ← Extractors.*
                          ← Api (hosts consumers)
                          ← Jobs
```

No references flow upward. Models has zero dependencies.

### Controller / Service Architecture

Controllers are thin — they handle HTTP concerns (validation, status codes) and delegate all business logic to dedicated services. No controller references the Data layer directly.

| Controller | Service | Responsibility |
|---|---|---|
| `ProjectsController` | `IProjectQueryService` | Project listing, detail, health metrics, hotspots, nodes, readme, impact analysis, batch status |
| `ProjectsController` | `IProjectService` | Re-analysis orchestration (index + analyze pipeline) |
| `AdminController` | `IAdminService` | Repo processing, re-indexing, cross-repo linking, and repository discovery |
| `WikiController` | `IWikiService` | Hierarchical wiki with sections, nested pages, attachments, revisions |
| `GraphController` | `IGraphOverviewService` | Cross-repo graph overview aggregation |
| `NodesController` | `INodeQueryService` | Node detail with edge resolution, search |
| `SearchController` | `ISearchService` | Unified search across repositories and nodes |
| `ClustersController` | `ICommunityDetectionService` | Service cluster detection, cluster graph, cluster detail |
| `MemoryController` | `MemoryService` | Memory graph store, query, entity detail |
| `AskController` | `GraphAssistant` | Streaming assistant chat with graph tools |

**Request/response models** live in `CodeGraph.Models` under `Requests/` and `Responses/`. Response models are simple POCOs — data entities are mapped to response DTOs in the service layer, never exposed through controllers.

## Data Layer

All persistent storage uses **Neo4j**. The `CodeGraph.Data` project defines store interfaces; `CodeGraph.Data.Neo4j` implements them.

| Store | Responsibility |
|-------|----------------|
| `IGraphStore` | Code nodes, edges, cross-repo edges, repositories, sync state, file hashes |
| `IAnalysisStore` | AI analysis batches, project analyses, repo summaries |
| `IMetricsStore` | File-level health metrics, project health summaries |
| `IMemoryGraphStore` | Memory entities, relationships, observations, vector/text search |
| `IWikiStore` | Wiki sections, pages, revisions, attachments |
| `IAdminStore` | Admin users and exclusion rules |
| `IVectorStore` | Embedding storage and vector similarity search |
| `IMigrationRunner` | Schema migration execution |

### Schema Migrations

Located in `src/CodeGraph.Api/Migrations/` and copied into API publish output:

| Migration | Description |
|-----------|-------------|
| `001_schema.cypher` | All constraints, indexes, fulltext indexes, vector indexes, wiki schema, and memory graph schema |
| `002_wiki_seed_sections.cypher` | Seed default wiki sections (general, conventions, skills, agents, mcp-documentation) |
| `003_analysis_batch_provider_batch_id_backfill.cypher` | Backfill `providerBatchId` on legacy analysis batch records |

## Event-Driven Pipeline

The indexing and analysis pipeline is fully event-driven via MassTransit/RabbitMQ. Each stage is an independent consumer with its own retry policy — failures in one stage don't block others.

```
ProcessRepository (message)
  │
  ▼
ProcessRepositoryConsumer
  └─→ Index repo → publish RepositoryIndexingCompleted
                        ├─→ CrossRepoLinker (incremental linking)
                        ├─→ VitalsAnalyzer (health metrics + AI analysis)
                        └─→ BatchAnalysisService (submit to Anthropic)
                                └─→ publish AnalysisBatchSubmitted
                                        │
                              [delayed redelivery: 1m, 5m, 10m, 15m]
                                        │
                                        ▼
                              AnalysisBatchSubmittedConsumer (poll Anthropic)
                                ├─ Done → publish ProjectAnalysisResultsProcessed
                                │               └─→ SynthesizeRepoSummary
                                │                       └─→ publish AnalysisSynthesisCompleted
                                │                               └─→ Write CODEGRAPH.md files
                                └─ Still processing → throw BatchNotReadyException (retries)
```

### Messages & Consumers

| Message | Consumer | Trigger |
|---------|----------|---------|
| `ProcessRepository` | `ProcessRepositoryConsumer` | Admin API, scheduled jobs, repository discovery |
| `RepositoryIndexingCompleted` | `RepositoryIndexingCompletedConsumer` | After successful indexing |
| `AnalysisBatchSubmitted` | `AnalysisBatchSubmittedConsumer` | After Anthropic batch submission |
| `ProjectAnalysisResultsProcessed` | `ProjectAnalysisResultsProcessedConsumer` | After batch results stored |
| `AnalysisSynthesisCompleted` | `AnalysisSynthesisCompletedConsumer` | After repo-level synthesis |
| `ConventionUpdated` | `ConventionUpdatedConsumer` | Wiki page created/updated |
| `RepositoryRemoved` | `RepositoryRemovedConsumer` | Repo removed from the configured source provider (cascading cleanup) |
| `StoreMemory` | `StoreMemoryConsumer` | Memory graph store request (from MCP or API) |

## Memory Graph

A personal knowledge graph for storing and querying memories across assistant conversations. Entities, relationships, and observations are stored in Neo4j with vector embeddings for semantic search.

### Features

- **Fuzzy entity matching** — New entities are matched against existing ones using fuzzy string matching to prevent duplicates
- **Vector search** — Entities are embedded (384-dim) for semantic similarity search with automatic overfetch/filter
- **Fulltext search** — Lucene-backed fallback when vector search is unavailable
- **Subgraph traversal** — Query returns seed entities plus N-hop neighborhood with relationship context
- **Conflict detection** — Edges marked as conflicting create observations that surface during queries
- **Relationship embeddings** — Edges are also embedded for relevance-ranked relationship display

### API Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/memory/store` | Store entities and relationships (async via MassTransit) |
| GET | `/api/memory/query?topic=...` | Semantic search with subgraph expansion |
| GET | `/api/memory/graph` | Full graph snapshot (paginated) |
| GET | `/api/memory/entities/{id}` | Entity detail with relationships |

### MCP Tools

| Tool | Description |
|------|-------------|
| `store_memory` | Store structured entities and relationships |
| `query_memory` | Semantic search across the memory graph |

### Migrating Legacy MemoryGraph Data

If you have memories stored in the older `MemoryGraph` project, use the one-off migration tool in [`tools/memory-migration/`](./tools/memory-migration):

```bash
# Dry-run against the old Docker volume
./tools/memory-migration/run-memorygraph-migration.sh --source-username michael

# Apply the migration into the current CodeGraph Neo4j
./tools/memory-migration/run-memorygraph-migration.sh --source-username michael --apply
```

The wrapper script starts a temporary Neo4j container against the old `memorygraph_neo4j_data` Docker volume, then runs a dry-run or import into the current CodeGraph memory graph. The migration is dry-run by default and aborts if overlapping target ids are detected, unless you explicitly allow that.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Neo4j 5.x (with vector index support)
- RabbitMQ (for MassTransit event-driven pipeline)
- [Node.js 18+](https://nodejs.org/) (for Angular dashboard and TypeScript extractor)
- An [Anthropic API key](https://console.anthropic.com/) (for AI analysis features)
- A configured repository source: GitHub, GitLab, or a local folder root

## Quick Start

### 1. Configuration

Key settings in `appsettings.json`:

| Setting | Default | Description |
|---------|---------|-------------|
| `CodeGraph:StorageOptions:Neo4jUri` | `bolt://localhost:7687` | Neo4j connection URI |
| `CodeGraph:StorageOptions:Neo4jUsername` | `neo4j` | Neo4j username |
| `CodeGraph:StorageOptions:Neo4jPassword` | — | Neo4j password |
| `CodeGraph:AnalysisOptions:Model` | `claude-sonnet-4-6` | Default analysis model |
| `CodeGraph:IndexingOptions:FoundationalRepos` | — | Repositories to index first during bootstrap |
| `CodeGraph:RepositorySource:Provider` | `Folder` | Source provider: Folder, GitHub, or GitLab |
| `CodeGraph:RepositorySource:ReposCachePath` | `~/repos/.cache` | Local cache for cloned remote repos |
| `CodeGraph:RepositorySource:Folder:RootPath` | `~/repos/` | Local folder root used by the folder provider |
| `CodeGraph:TsPort` | `3100` | TypeScript extractor sidecar port |

### 2. Build & Run

```bash
# Build the solution
dotnet build CodeGraph.sln

# Start the API server (http://localhost:5037)
# Pending Neo4j migrations are applied automatically at startup.
dotnet run --project src/CodeGraph.Api

# Start the Angular dashboard (http://localhost:4200)
cd CodeGraphWeb && npm install && npm start
```

## API Endpoints

### Projects

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/projects` | List repositories (pagination, search) |
| GET | `/api/projects/{name}` | Project detail with node counts and cross-repo edges |
| GET | `/api/projects/{name}/health` | Health summary + hotspots |
| GET | `/api/projects/{name}/metrics` | File metrics sorted by risk |
| GET | `/api/projects/{name}/hotspots` | Top risky files |
| GET | `/api/projects/{name}/nodes` | Nodes by label with pagination |
| GET | `/api/projects/{name}/batch-status` | Latest analysis batch status |
| GET | `/api/projects/{name}/readme` | Project README.md content |
| GET | `/api/projects/{name}/impact` | Blast radius impact analysis for a node (`?node=QualifiedName&depth=3`) |
| GET | `/api/projects/{name}/impact/file` | Blast radius impact analysis for a file (`?path=relative/file.cs&depth=3`) |
| POST | `/api/projects/ReAnalyze` | Trigger full re-index + analysis for a repo |

### Search

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/search?q={query}` | Unified search across repos and nodes |

### Graph

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/graph/overview` | All repos + aggregated cross-repo edges |

### Nodes

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/nodes/{id}` | Node detail with edges |
| GET | `/api/nodes/search` | Search by pattern, label, project |

### Clusters

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/clusters` | Cluster overview with members, edge counts, density, bridge repos |
| GET | `/api/clusters/graph` | Graph representation for D3.js visualization |
| GET | `/api/clusters/{id}` | Cluster detail with cross-cluster connections |

### Memory

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/memory/store` | Store entities and relationships (async) |
| GET | `/api/memory/query?topic=...` | Semantic search with subgraph expansion |
| GET | `/api/memory/graph` | Full graph snapshot (paginated) |
| GET | `/api/memory/entities/{id}` | Entity detail with relationships |

### Ask

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/ask` | SSE stream — assistant conversation with graph tools |

### Wiki

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/wiki/sections` | List all wiki sections |
| GET | `/api/wiki/{section}/tree` | Hierarchical page tree for a section |
| GET | `/api/wiki/{section}/{**path}` | Get page by nested path |
| POST | `/api/wiki/{section}` | Create a root page in a section |
| POST | `/api/wiki/{section}/{**path}` | Create a child page or upload attachment |
| PUT | `/api/wiki/{section}/{**path}` | Update a page (creates revision) |
| DELETE | `/api/wiki/{section}/{**path}` | Delete a page |
| PATCH | `/api/wiki/{section}/{**path}/move` | Move a page between sections/parents |
| GET | `/api/wiki/{section}/{**path}/revisions` | List revision history |
| GET | `/api/wiki/{section}/{**path}/revisions/{rev}` | Get a specific revision |
| GET | `/api/wiki/{section}/{**path}/attachments` | List page attachments |
| POST | `/api/wiki/{section}/{**path}/attachments` | Upload an attachment |
| GET | `/api/wiki/attachments/{id}/{filename}` | Download an attachment |

### Admin

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/admin/processRepos` | Publish indexing messages for repos |
| POST | `/api/admin/reIndexAll` | Re-index all repos |
| POST | `/api/admin/link` | Run cross-repo linking |
| POST | `/api/admin/processBatchAnalysis` | Process completed Anthropic batches |
| POST | `/api/admin/discover` | Auto-discover repos from the configured source provider |

### Optional Git Hooks

This repo includes versioned Git hooks in `.githooks/` plus an installer:

```bash
./tools/install-git-hooks.sh
```

The hooks are gated to `main` by default:

- `post-commit` triggers repository indexing only
- `post-merge` triggers repository indexing plus analysis

Both hooks publish to the current `POST /api/settings/processRepos` endpoint asynchronously so they do not block Git for long.

Optional environment overrides:

- `CODEGRAPH_API_URL` — defaults to `http://localhost:5037`
- `CODEGRAPH_HOOK_MAIN_BRANCH` — defaults to `main`
- `CODEGRAPH_HOOK_REPO_NAME` — defaults to the repo folder name
- `CODEGRAPH_HOOK_TIMEOUT_SECONDS` — defaults to `5`

## MCP Server

The MCP server is hosted within the API project via HTTP transport at `http://localhost:5037`. Add it to your MCP client configuration:

```json
{
  "mcpServers": {
    "codegraph": {
      "type": "http",
      "url": "http://localhost:5037/mcp"
    }
  }
}
```

### Available Tools

| Tool | Description |
|------|-------------|
| `get_graph_schema` | Describe node/edge types and properties |
| `list_projects` | List all indexed repositories |
| `search_graph` | Search by name pattern, node type, project |
| `get_service_summary` | AI analysis summary for a service |
| `trace_call_path` | Trace inbound/outbound calls and dependencies |
| `trace_data_lineage` | Follow a model through publishers/consumers |
| `find_consumers` | What consumes this event/model/endpoint |
| `find_publishers` | What publishes to a queue/exchange |
| `get_architecture` | Architecture overview and dependency analysis |
| `find_archival_candidates` | Repos with no inbound/outbound dependencies |
| `get_code_snippet` | Read source code from a repository |
| `get_project_health` | Health scores, hotspots, and AI analysis per project |
| `get_fleet_health` | Fleet-wide health overview, repos ranked by score |
| `read_node_source` | Read source code for a node with surrounding context |
| `list_conventions` | List all convention pages from the wiki |
| `get_convention` | Read a convention page by slug (supports fuzzy matching) |
| `get_service_clusters` | Discover tightly-coupled repository groups |
| `get_cluster_detail` | Detailed view of a specific cluster |
| `analyze_impact` | Blast radius analysis for a changed code element |
| `index_repository` | Trigger re-indexing of a repository |
| `store_memory` | Store entities and relationships in the memory graph |
| `query_memory` | Semantic search across the memory graph |

## Graph Model

### Node Types

| Category | Node Types |
|----------|-----------|
| Code | Project, Namespace, File, Class, Interface, Struct, Record, Enum, Method, Function, Property, Constructor, Delegate |
| API | Route, Service |
| Data | Table, View, StoredProcedure |
| Messaging | Event, Queue, Exchange |
| UI | Component, Module |
| Operations | Job, NuGetPackage |

### Edge Types

| Category | Edge Types |
|----------|-----------|
| Structure | CONTAINS_FILE, CONTAINS_FOLDER, CONTAINS_NAMESPACE, CONTAINS_PROJECT, DEFINES, DEFINES_METHOD |
| Code | CALLS, IMPORTS, IMPLEMENTS, INHERITS, USES_TYPE, INJECTS |
| HTTP | HTTP_CALLS, HANDLES |
| Messaging | PUBLISHES, CONSUMES, SUBSCRIBES |
| Data | QUERIES |
| Packages | REFERENCES_PACKAGE |
| UI | RENDERS |
| Operations | SCHEDULES, FILE_CHANGES_WITH |

## Angular Dashboard

The `CodeGraphWeb/` directory contains an Angular application with:

- **Repository list** — Browse all indexed repos with search and pagination
- **Repository detail** — Node counts, health scores, AI analysis, dependency graph
- **Node browser** — Filter by type, project, search pattern
- **Node detail** — View edges, cross-repo connections
- **Graph visualization** — D3.js interactive dependency graph
- **Ask** — Streaming chat interface backed by the assistant + graph tools
- **Wiki** — Multi-section hierarchical wiki with sidebar navigation, tree view, inline editing, file attachments, raw content editors, and revision history
- **Health dashboard** — Hotspots, risk scores, file metrics with rendered markdown analysis
- **Service clusters** — D3.js force-directed cluster visualization with color-coded groups, bridge repo highlighting, and cluster detail panel
- **Impact analysis** — Blast radius explorer with typeahead search, risk-grouped results, cross-repo impact table, and configurable traversal depth
- **Global search** — Unified search across repos and nodes with relevance ranking

```bash
cd CodeGraphWeb
npm install
npm start          # Dev server at http://localhost:4200
npm run build      # Production build
```

## Testing

```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test src/CodeGraph.Tests

# Run a single test
dotnet test --filter "FullyQualifiedName~TestMethodName"
```

Tests use **xUnit** with **Shouldly** assertions and include an `InMemoryGraphStore` for data layer testing without Neo4j.

## Architecture Decisions

| Decision | Rationale |
|----------|-----------|
| Neo4j for all storage | Graph-native queries replace recursive CTEs; single data store simplifies ops |
| Roslyn for C# | Semantic analysis far exceeds tree-sitter's syntactic parsing |
| Tree-sitter for other languages | Multi-language fallback replaces separate Ansible/ColdFusion/Terraform extractors |
| Node.js sidecar for TypeScript | TypeScript compiler API understands Angular natively |
| Direct commits, not MRs | MRs add friction and would be ignored — self-maintaining |
| Confidence indicators | "I don't know" is better than a confident wrong answer |
| NuGet qualified names as linking keys | Canonical cross-repo identifiers (e.g. `OrdersApi.Models.OrderCreatedEvent`) |
| Foundational-first indexing | Framework repos (ServiceStack, ServiceBus) analyzed first to provide context for all other repos |
| Anthropic Batches API | 50% cost reduction for bulk analysis across hundreds of repos |
| Event-driven pipeline | Independent failure isolation — linking failures don't block analysis, vitals failures don't block indexing |
| Delayed redelivery for batch polling | MassTransit redelivery replaces scheduled polling jobs for faster results without tying up threads |
| Incremental cross-repo linking | Re-links only the newly indexed repo's edges instead of full graph rebuild |
| Single-user memory graph | No per-user scoping — streamlined for personal use |

## Key Technologies

| Layer | Technologies |
|-------|-------------|
| Runtime | .NET 9, ASP.NET Core |
| Database | Neo4j 5.x (Cypher, vector indexes, fulltext indexes) |
| Messaging | RabbitMQ, MassTransit |
| Code Analysis | Roslyn (C#), ScriptDom (T-SQL), TypeScript Compiler API, Tree-sitter |
| AI | Anthropic Claude API, Anthropic Batches API, AnthropicCircuitBreaker |
| Embeddings | ONNX Runtime (all-MiniLM-L6-v2, 384-dim) |
| Integration | MCP SDK, LibGit2Sharp |
| DI | Autofac |
| Frontend | Angular, TypeScript, D3.js, RxJS, marked |
| Testing | xUnit, Shouldly |

For Docker runs, the API now expects embeddings under `/models`. The default ONNX path is `/models/embeddings/all-MiniLM-L6-v2/model.onnx`, with `vocab.txt` in the same directory. By default Docker mounts `./.cache/models` to `/models`, and also mounts the legacy sibling checkout `../llm/models` to `/models-legacy` so older local setups keep working. Override `CODEGRAPH_DOCKER_MODELS_MOUNT` or `CODEGRAPH_DOCKER_LEGACY_MODELS_MOUNT` if your host layout differs.
