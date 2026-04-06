# CodeGraph

A self-maintaining .NET 9 service that indexes ~620 GitLab repositories into a queryable knowledge graph (MySQL), producing two outputs: a structural graph of all connections between services, and generated `CODEGRAPH.md` files committed to each repo describing business intent. An MCP server lets Claude act as the domain expert for the entire codebase.

## Core Principle

**Self-maintaining.** If it requires human attention to stay accurate, it will rot. Auto-discovery of new repos, CI-triggered updates, direct commits for doc changes, automatic cleanup of removed repos.

## Features

- **Knowledge Graph Indexing** — Extracts classes, methods, routes, events, queues, jobs, tables, and their relationships from source code into a MySQL graph
- **Multi-Language Extraction** — C# (Roslyn semantic analysis), TypeScript/Angular (Node.js sidecar), T-SQL (ScriptDom), ColdFusion (regex), Ansible (YAML parser), Terraform (HCL regex)
- **Claude-Powered Analysis** — Anthropic Batches API generates natural language summaries with confidence indicators for every project
- **Cross-Repo Linking** — Connects HTTP calls, MassTransit events, NuGet package references, and IaC deployments across repositories
- **Codebase Health Metrics** — Tracks file churn, cyclomatic complexity, truck factor, coupling centrality, and risk scores
- **Event-Driven Pipeline** — Asynchronous processing via MassTransit consumers with independent failure isolation and automatic retry
- **MCP Server** — 20+ tools for Claude to query the graph, trace call paths, find consumers/publishers, explore architecture, check health, analyze impact, detect clusters, and read conventions
- **REST API** — Full query interface for projects, nodes, edges, graph overview, health data, search, and wiki
- **Angular Dashboard** — Browse repositories, explore the graph, view health hotspots, ask questions via streaming Claude chat, and manage the wiki
- **Hierarchical Wiki** — Multi-section wiki with nested pages (up to 3 levels), file attachments, raw content support, revision history, admin-managed sections, and MCP integration
- **Global Search** — Unified search across repositories and nodes with relevance ranking
- **Service Cluster Detection** — Louvain community detection algorithm discovers tightly-coupled repository groups from the cross-repo dependency graph, with edge-type weighting, bridge repo identification, and D3.js cluster visualization
- **Blast Radius / Impact Analysis** — Given a changed code element or file, traverses inbound dependencies to identify everything affected, classifying each by risk level (Critical/High/Medium/Low) with cross-repo awareness
- **Anthropic Circuit Breaker** — Resilient API integration with exponential backoff, transient detection, and automatic circuit breaking

## Solution Structure

```
TC.CodeGraphApi/
├── src/
│   ├── TC.CodeGraphApi/                       # ASP.NET Core API host, controllers, consumers, DI
│   │   └── Consumers/                         # MassTransit event consumers (7 consumers)
│   ├── TC.CodeGraphApi.Console/               # CLI: migrate
│   ├── TC.CodeGraphApi.Models/                # Domain model, request/response DTOs (zero dependencies)
│   │   ├── Messages/                          # MassTransit event messages (7 message types)
│   │   ├── Requests/                          # API request models
│   │   └── Responses/                         # API response models (no entity references)
│   ├── TC.CodeGraphApi.Services/              # All business logic, query engine, Claude analysis, MCP tools
│   ├── TC.CodeGraphApi.Data/                  # IGraphStore, EF Core + Dapper, entities, migrations
│   ├── TC.CodeGraphApi.Extractors.CSharp/     # Roslyn semantic extraction
│   ├── TC.CodeGraphApi.Extractors.TypeScript/ # TypeScript compiler API sidecar
│   ├── TC.CodeGraphApi.Extractors.Sql/        # T-SQL ScriptDom parsing
│   ├── TC.CodeGraphApi.Extractors.ColdFusion/ # Regex-based extraction
│   ├── TC.CodeGraphApi.Extractors.Ansible/    # YAML-based Ansible extraction
│   ├── TC.CodeGraphApi.Extractors.Terraform/  # HCL regex-based Terraform extraction
│   └── TC.CodeGraphJobs/                      # Scheduled jobs (batch processing, discovery)
├── tests/
│   └── TC.CodeGraphApi.Tests/                 # Unit tests (xUnit + Shouldly)
├── CodeGraphWeb/                              # Angular 21 dashboard
├── sql/migrations/                            # MySQL schema migrations
├── CodeGraph-Architecture.md                  # Detailed system design
└── CodeGraph-Implementation.md                # Build order and code patterns
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
| `AdminController` | `IAdminService` | Repo processing, re-indexing, cross-repo linking, GitLab discovery |
| `WikiController` | `IWikiService` | Hierarchical wiki with sections, nested pages, attachments, revisions |
| `GraphController` | `IGraphOverviewService` | Cross-repo graph overview aggregation |
| `NodesController` | `INodeQueryService` | Node detail with edge resolution, search |
| `SearchController` | `ISearchService` | Unified search across repositories and nodes |
| `ClustersController` | `ICommunityDetectionService` | Service cluster detection, cluster graph, cluster detail |
| `AskController` | `GraphAssistant` | Streaming Claude chat with graph tools |

**Request/response models** live in `TC.CodeGraphApi.Models` under `Requests/` and `Responses/`. Response models are simple POCOs — data entities are mapped to response DTOs in the service layer, never exposed through controllers.

## Event-Driven Pipeline

The indexing and analysis pipeline is fully event-driven via MassTransit/RabbitMQ. Each stage is an independent consumer with its own retry policy — failures in one stage don't block others.

```
ProcessRepository (message)
  │
  ▼
ProcessRepositoryConsumer
  └─→ Index repo → publish RepositoryIndexingCompleted
                        ├─→ CrossRepoLinker (incremental linking)
                        ├─→ VitalsAnalyzer (health metrics + Claude analysis)
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
| `ProcessRepository` | `ProcessRepositoryConsumer` | Admin API, scheduled jobs, GitLab discovery |
| `RepositoryIndexingCompleted` | `RepositoryIndexingCompletedConsumer` | After successful indexing |
| `AnalysisBatchSubmitted` | `AnalysisBatchSubmittedConsumer` | After Anthropic batch submission |
| `ProjectAnalysisResultsProcessed` | `ProjectAnalysisResultsProcessedConsumer` | After batch results stored |
| `AnalysisSynthesisCompleted` | `AnalysisSynthesisCompletedConsumer` | After repo-level synthesis |
| `ConventionUpdated` | `ConventionUpdatedConsumer` | Wiki page created/updated |
| `RepositoryRemoved` | `RepositoryRemovedConsumer` | Repo removed from GitLab (cascading cleanup) |

All messages use the `[TcServiceBusEvent(TcQueueHosts.Enterprise)]` attribute and consumers inherit from `TcConsumer<TMessage, TConsumer>`.

### Re-Analysis Process

Re-analysis is triggered per-repository via `POST /api/projects/ReAnalyze` with `{ "repo": "TC.OrdersApi" }`:

1. **Controller** validates the request and delegates to `IProjectService.ReAnalyzeRepository`
2. **ProjectService** checks if an analysis batch is already in-progress (returns it if so)
3. **Indexing** — `IndexingPipeline` re-extracts the code graph from the local repo (all 6 extractors)
4. **Event published** — `RepositoryIndexingCompleted` triggers downstream work asynchronously:
   - **Cross-repo linking** — `CrossRepoLinker.LinkForProjectAsync` connects HTTP, messaging, NuGet, and IaC edges
   - **Vitals** — `VitalsAnalyzer` computes file-level health metrics and Claude-powered health analysis
   - **Analysis submission** — `BatchAnalysisService` groups nodes by .csproj and submits to Anthropic Batches API
5. **Batch polling** — `AnalysisBatchSubmittedConsumer` checks Anthropic via delayed redelivery (1m → 5m → 10m → 15m)
6. **Results processing** — Per-project summaries stored, repo-level synthesis via Claude, CODEGRAPH.md files written

The same pipeline runs automatically when repos are discovered via `POST /api/admin/discover` or processed via `POST /api/admin/processRepos`. The `SkipIfUpToDate` flag (default: true) compares the HEAD commit SHA to avoid redundant work.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- MySQL 8.0+
- RabbitMQ (for MassTransit event-driven pipeline)
- [Node.js 18+](https://nodejs.org/) (for Angular dashboard and TypeScript extractor)
- An [Anthropic API key](https://console.anthropic.com/) (for Claude analysis features)
- GitLab instance with API access (for repo discovery and sync)

## Quick Start

### 1. Database Setup

Create a MySQL database and apply migrations:

```bash
# Set your connection string
export CODEGRAPH_MYSQL="Server=localhost;Database=codegraph;User=root;Password=yourpassword;"

# Apply migrations
dotnet run --project src/TC.CodeGraphApi.Console -- migrate
```

### 2. Configuration

Create a `.env` file or set environment variables:

```env
CODEGRAPH_MYSQL=Server=localhost;Database=codegraph;User=root;Password=yourpassword;
ANTHROPIC_API_KEY=sk-ant-...
```

Key settings in `appsettings.json`:

| Setting | Default | Description |
|---------|---------|-------------|
| `ConnectionStrings:CodeGraph` | — | MySQL connection string |
| `CodeGraph:Storage:BatchSize` | 500 | Batch insert size |
| `CodeGraph:Analysis:Model` | claude-sonnet-4-6 | Claude model for analysis |
| `CodeGraph:Indexing:FoundationalRepos` | TC.Common.ServiceStack, etc. | Framework repos indexed first |
| `CodeGraph:GitLab:BaseUrl` | — | GitLab instance URL |
| `CodeGraph:GitLab:PrivateToken` | — | GitLab PAT (read_api, read_repository, write_repository) |
| `CodeGraph:GitLab:ReposCachePath` | — | Local cache for cloned repos |
| `CODEGRAPH_TS_PORT` | 3100 | TypeScript extractor sidecar port |
| `CODEGRAPH_MAX_PARALLEL_ANALYSES` | — | Max parallel Claude requests |

### 3. Build & Run

```bash
# Build the solution
dotnet build src/TC.CodeGraphApi.sln

# Start the API server (http://localhost:5037)
dotnet run --project src/TC.CodeGraphApi

# Start the Angular dashboard (http://localhost:4200)
cd CodeGraphWeb && npm install && npm start
```

## CLI Commands

The Console project handles database migrations:

```bash
# Apply database migrations
dotnet run --project src/TC.CodeGraphApi.Console -- migrate
```

All other operations (indexing, analysis, MCP) are handled by the API host.

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

### Ask

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/ask` | SSE stream — Claude conversation with graph tools |

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
| POST | `/api/admin/discover` | Auto-discover repos from GitLab |

## MCP Server

The MCP server is hosted within the API project via HTTP transport at `http://localhost:5037`. Add to your Claude configuration:

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
| `get_service_summary` | Claude analysis summary for a service |
| `trace_call_path` | Trace inbound/outbound calls and dependencies |
| `trace_data_lineage` | Follow a model through publishers/consumers |
| `find_consumers` | What consumes this event/model/endpoint |
| `find_publishers` | What publishes to a queue/exchange |
| `get_architecture` | Architecture overview and dependency analysis |
| `find_archival_candidates` | Repos with no inbound/outbound dependencies |
| `get_code_snippet` | Read source code from a repository |
| `get_project_health` | Health scores, hotspots, and Claude analysis per project |
| `get_fleet_health` | Fleet-wide health overview, repos ranked by score |
| `read_node_source` | Read source code for a node with surrounding context |
| `list_conventions` | List all convention pages from the wiki |
| `get_convention` | Read a convention page by slug (supports fuzzy matching) |
| `get_service_clusters` | Discover tightly-coupled repository groups |
| `get_cluster_detail` | Detailed view of a specific cluster |
| `analyze_impact` | Blast radius analysis for a changed code element |
| `index_repository` | Trigger re-indexing of a repository |

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
| Ansible | Playbook, Role, AnsibleTask, AnsibleHandler, AnsibleVariable |
| Terraform | TerraformResource, TerraformModule, TerraformVariable, TerraformOutput, TerraformDataSource |

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
| IaC | DEPLOYS, CONFIGURES, INCLUDES_ROLE, INCLUDES_MODULE, DEPENDS_ON, NOTIFIES_HANDLER |

## Database

MySQL 8.0+ with EF Core (CRUD) + Dapper (recursive CTEs and batch operations).

### Migrations

Located in `sql/migrations/`:

| Migration | Description |
|-----------|-------------|
| `001_initial_schema.sql` | Core tables: repositories, nodes, edges, cross_repo_edges, file_hashes, summaries, analyses |
| `002_file_metrics.sql` | Vitals: file_metrics with churn, complexity, risk scores |
| `003_health_analyses.sql` | Health: project_health_summaries, project_health_analyses |
| `004_conventions.sql` | Legacy: convention_pages, convention_revisions (migrated to wiki tables) |
| `005_widen_node_name.sql` | Widen nodes.name to VARCHAR(1000) for IaC/ColdFusion names |
| `006_add_gitlab_group.sql` | Add gitlab_group column for namespace filtering |
| `008_wiki_system.sql` | Wiki: wiki_sections, wiki_pages, wiki_revisions, wiki_attachments + data migration from conventions |
| `009_wiki_seed_data.sql` | Seed 5 default sections: general, conventions, skills, agents, mcp-documentation |
| `010_raw_content.sql` | Add raw_content fields for dual-editor mode (Skills, Agents sections) |
| `014_repo_clusters.sql` | Repo clusters: cluster membership, modularity score, betweenness centrality |

## Angular Dashboard

The `CodeGraphWeb/` directory contains an Angular 21 application with:

- **Repository list** — Browse all indexed repos with search and pagination
- **Repository detail** — Node counts, health scores, Claude analysis, dependency graph
- **Node browser** — Filter by type, project, search pattern
- **Node detail** — View edges, cross-repo connections
- **Graph visualization** — D3.js interactive dependency graph
- **Ask Claude** — Streaming chat interface backed by Claude + graph tools
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
dotnet test tests/TC.CodeGraphApi.Tests

# Run a single test
dotnet test --filter "FullyQualifiedName~TestMethodName"
```

Tests use **xUnit** with **Shouldly** assertions and include an `InMemoryGraphStore` for data layer testing without MySQL.

## Architecture Decisions

| Decision | Rationale |
|----------|-----------|
| EF Core + Dapper hybrid | EF Core for CRUD, Dapper for recursive CTEs and batch operations |
| MySQL over graph DB | Company already runs MySQL; recursive CTEs handle traversal |
| Roslyn for C# | Semantic analysis far exceeds tree-sitter's syntactic parsing |
| Node.js sidecar for TypeScript | TypeScript compiler API understands Angular natively |
| Direct commits, not MRs | MRs add friction and would be ignored — self-maintaining |
| Confidence indicators | "I don't know" is better than a confident wrong answer |
| NuGet qualified names as linking keys | Canonical cross-repo identifiers (e.g. `TC.OrdersApi.Models.OrderCreatedEvent`) |
| Foundational-first indexing | Framework repos (ServiceStack, ServiceBus) analyzed first to provide context for all other repos |
| Anthropic Batches API | 50% cost reduction for bulk analysis across hundreds of repos |
| Event-driven pipeline | Independent failure isolation — linking failures don't block analysis, vitals failures don't block indexing |
| Delayed redelivery for batch polling | MassTransit redelivery replaces scheduled polling jobs for faster results without tying up threads |
| Incremental cross-repo linking | Re-links only the newly indexed repo's edges instead of full graph rebuild |

## Key Technologies

| Layer | Technologies |
|-------|-------------|
| Runtime | .NET 9, ASP.NET Core |
| Database | MySQL 8.0, EF Core (Pomelo), Dapper |
| Messaging | RabbitMQ, MassTransit, TcServiceBus (in-house abstraction) |
| Code Analysis | Roslyn (C#), ScriptDom (T-SQL), TypeScript Compiler API, YamlDotNet (Ansible), Regex (ColdFusion, Terraform HCL) |
| AI | Anthropic Claude API, Anthropic Batches API, AnthropicCircuitBreaker |
| Integration | MCP SDK, LibGit2Sharp |
| DI | Autofac |
| Frontend | Angular 21, TypeScript 5.9, D3.js, RxJS, marked |
| Testing | xUnit, Shouldly |
