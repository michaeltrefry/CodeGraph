# CodeGraph

CodeGraph is a self-maintaining .NET 9 platform that indexes source repositories into a Neo4j-backed knowledge graph, generates `CODEGRAPH.md` documentation, exposes graph and memory tooling over REST and MCP, and ships with an Angular UI for exploration, reviews, operations, and memory browsing.

It is designed around one rule: if a feature needs ongoing human babysitting to stay correct, it will rot.

## Core Principle

**Self-maintaining.** CodeGraph should discover repositories, re-index them, refresh generated docs, surface risk, and clean up stale data without requiring a person to keep the system coherent by hand.

## What CodeGraph Does

- Indexes repositories into a structural graph of code, APIs, messaging, jobs, packages, and database objects
- Extracts across multiple languages: C# via Roslyn, TypeScript/Angular via a Node sidecar, T-SQL via ScriptDom, and Tree-sitter as a fallback
- Links repositories through HTTP calls, MassTransit messaging, shared packages, and other cross-repo signals
- Generates natural-language repository and project analysis with confidence indicators and optional auto-commit/auto-push of `CODEGRAPH.md`
- Supports multiple AI backends for analysis: Anthropic, OpenAI, Gemini, and local OpenAI-compatible endpoints
- Computes repository health, hotspot metrics, security findings, .NET support posture, and repository vitality trends
- Runs project-level and repository-level AI code reviews with persisted findings and SSE streaming updates
- Stores claim-centric personal memory in Neo4j, including evidence, conflicts, bounded subgraphs, and frontier expansion
- Serves a web UI for repositories, graph views, reviews, impact analysis, wiki pages, schedules, and a dedicated memory browser
- Exposes graph and memory capabilities over MCP for assistants and other compatible clients

## Solution Structure

```text
CodeGraph/
├── src/
│   ├── CodeGraph.Api/                   # ASP.NET Core API host, MCP host, controllers, MassTransit consumers
│   ├── CodeGraph.Models/                # Domain models, request/response DTOs, messages, memory contracts
│   ├── CodeGraph.Services/              # Analysis, indexing, query engine, assistant, reviews, memory, wiki
│   ├── CodeGraph.Data/                  # Store interfaces and shared entities
│   ├── CodeGraph.Data.Neo4j/            # Neo4j-backed implementations
│   ├── CodeGraph.Jobs/                  # Background job host and embedded schedule runner
│   ├── CodeGraph.Extractors.CSharp/     # Roslyn-based extraction
│   ├── CodeGraph.Extractors.TypeScript/ # TypeScript/Angular sidecar integration
│   ├── CodeGraph.Extractors.Sql/        # T-SQL extraction
│   ├── CodeGraph.Extractors.TreeSitter/ # Fallback multi-language extraction
│   ├── CodeGraph.Tests/                 # Main test suite
│   └── CodeGraph.Jobs.Tests/            # Job-host test suite
├── CodeGraphWeb/                        # Angular frontend
└── src/CodeGraph.Api/Migrations/        # Neo4j schema and feature migrations
```

### Dependency flow

```text
Models <- Data <- Services <- Extractors.*
                         <- Api
                         <- Jobs
```

`CodeGraph.Api` hosts the REST API, the MCP endpoint, and the MassTransit consumers. `CodeGraph.Jobs` runs scheduled and manual background jobs. `CodeGraphWeb` is the Angular UI. Neo4j is the primary datastore, and RabbitMQ backs the event-driven pipeline.

## Key Capabilities

### Knowledge graph and discovery

- Repository, namespace, file, class, method, route, service, event, queue, exchange, table, component, and job indexing
- Cross-repo traversal for callers, consumers, publishers, data lineage, impact analysis, and service clustering
- Trust-aware search and source lookup for graph nodes

### Analysis and docs

- Repository and project summaries with confidence indicators
- Automatic `CODEGRAPH.md` generation and optional git commit/push
- Multi-provider analysis configuration with provider-specific settings
- Assistant model settings independent from batch-analysis settings

### Health, security, and vitality

- File-level hotspot scoring and repository health summaries
- Security analysis across three pillars: secrets, vulnerable packages, and attack-surface checks
- .NET SDK / target framework support reporting
- Repository vitality trends such as activity velocity, dormant periods, and firefighting rates

### Reviews

- Project-scoped AI reviews for a specific `.csproj` or project surface
- Repository-level code reviews that synthesize findings across projects
- Persisted review runs with latest-run lookup and SSE progress streams

### Memory

- Claim-centric memory model built on entities, claims, evidence, and observations
- Structured search, bounded subgraph retrieval, claim/entity bundle inspection, and frontier expansion
- Durable queued writes with receipt polling
- Memory browser UI with graph snapshots and focused entity drill-in

### Operations and wiki

- Repository discovery from local folders, GitHub, or GitLab
- Embedded job scheduling and manual job execution from the settings surface
- Hierarchical wiki with sections, nested pages, attachments, revisions, and auto-generated MCP documentation

## AI Provider Support

CodeGraph no longer assumes a single analysis backend.

- `AnalysisOptions:DefaultProvider` selects the backend for repository/project analysis
- Supported built-in providers are `anthropic`, `openai`, `gemini`, and `local`
- `AnalysisOptions:Assistant` has its own provider/model settings for the Ask experience
- Local analysis targets OpenAI-compatible endpoints by default and is wired for tools like LM Studio

Current config includes provider blocks for:

- `CodeGraph:AnalysisOptions:Anthropic`
- `CodeGraph:AnalysisOptions:OpenAi`
- `CodeGraph:AnalysisOptions:Gemini`
- `CodeGraph:AnalysisOptions:Local`
- `CodeGraph:AnalysisOptions:Assistant`

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Neo4j 5.x
- RabbitMQ
- [Node.js 18+](https://nodejs.org/)
- A repository source configuration: `Folder`, `GitHub`, or `GitLab`
- At least one configured analysis provider if you want AI analysis or reviews

For Docker-based local model setups, the default local provider points at `http://host.docker.internal:1234/v1`.

## Quick Start

### 1. Configure settings

The main local config file is [src/CodeGraph.Api/appsettings.json](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/appsettings.json). Docker-friendly defaults live in [.env.example](/Users/michael/Repos/CodeGraph/.env.example).

Useful settings to know:

| Setting | Purpose |
|---|---|
| `CodeGraph:StorageOptions:*` | Neo4j connection and embedding model settings |
| `CodeGraph:RepositorySource:Provider` | Choose `Folder`, `GitHub`, or `GitLab` |
| `CodeGraph:RepositorySource:Folder:RootPath` | Local repo root when using the folder provider |
| `CodeGraph:AnalysisOptions:DefaultProvider` | Default analysis backend |
| `CodeGraph:AnalysisOptions:Assistant:Provider` | Ask/assistant backend |
| `CodeGraph:AnalysisOptions:OpenAi:*` | OpenAI analysis provider settings |
| `CodeGraph:AnalysisOptions:Gemini:*` | Gemini analysis provider settings |
| `CodeGraph:AnalysisOptions:Local:*` | Local OpenAI-compatible provider settings |
| `CodeGraph:AnalysisOptions:AutoCommitDocs` | Auto-commit generated `CODEGRAPH.md` files |
| `CodeGraph:AnalysisOptions:AutoPushDocs` | Auto-push generated doc commits |
| `CodeGraph:TsPort` | TypeScript analyzer sidecar port |

### 2. Run locally

```bash
dotnet build CodeGraph.sln

# API + MCP host
dotnet run --project src/CodeGraph.Api

# Jobs host / schedule runner
dotnet run --project src/CodeGraph.Jobs

# Angular UI
cd CodeGraphWeb
npm install
npm start
```

Default endpoints:

- API: [http://localhost:5037](http://localhost:5037)
- Swagger: [http://localhost:5037/swagger](http://localhost:5037/swagger)
- MCP: [http://localhost:5037/mcp](http://localhost:5037/mcp)
- Web UI: [http://localhost:4200](http://localhost:4200)

### 3. Run with Docker Compose

```bash
cp .env.example .env
docker compose up --build
```

To run the Docker stack over HTTPS locally, generate a certificate before starting compose:

```bash
mkcert -install
mkdir -p CodeGraphWeb/certs
mkcert -cert-file CodeGraphWeb/certs/localhost.pem -key-file CodeGraphWeb/certs/localhost-key.pem localhost 127.0.0.1 ::1
```

The compose stack terminates TLS in the `web` container and forwards the API and MCP traffic to the internal `api` container over the Docker network.
By default it binds only to `127.0.0.1:8443` so it does not take over shared host ports like `80` or `443`.

The compose stack includes:

- `api`
- `jobs`
- `web`
- `neo4j`
- `rabbitmq`

Embeddings are expected under `/models` in containers. The default model path is `/models/embeddings/all-MiniLM-L6-v2/model.onnx`.

Docker HTTPS endpoints:

- Web UI: [https://localhost:8443](https://localhost:8443)
- API: [https://localhost:8443/api](https://localhost:8443/api)
- Swagger: [https://localhost:8443/swagger](https://localhost:8443/swagger)
- MCP: [https://localhost:8443/mcp](https://localhost:8443/mcp)

## Event-Driven Pipeline

Repository processing is asynchronous and message-driven via MassTransit and RabbitMQ.

Typical flow:

1. A repository is queued for processing.
2. Indexing extracts graph data and publishes completion events.
3. Cross-repo linking, health/vitals, security, and analysis follow as downstream stages.
4. Analysis results are synthesized into repository summaries and `CODEGRAPH.md`.
5. Memory writes are queued separately through their own consumer path.

The API host currently registers consumers for:

- `ProcessRepository`
- `RepositoryIndexingCompleted`
- `AnalysisBatchSubmitted`
- `ProjectAnalysisResultsProcessed`
- `AnalysisSynthesisCompleted`
- `RepositoryRemoved`
- `StoreMemoryClaims`

## REST API Highlights

The authoritative REST contract is the Swagger UI at [http://localhost:5037/swagger](http://localhost:5037/swagger). The routes below are the main surfaces that tend to matter day to day.

### Repository and graph data

| Route | Purpose |
|---|---|
| `GET /api/projects` | List repositories with search, grouping, and pagination |
| `GET /api/projects/{name}` | Repository detail |
| `GET /api/projects/{name}/health` | Health, hotspots, security summary, .NET support, vitality |
| `GET /api/projects/{name}/security` | Expanded security findings |
| `GET /api/projects/{name}/metrics` | File metrics |
| `GET /api/projects/{name}/hotspots` | Top hotspots |
| `GET /api/projects/{name}/nodes` | Repository nodes by label/project |
| `GET /api/projects/{name}/readme` | Stored README content for an indexed repo |
| `GET /api/projects/{name}/impact` | Impact analysis by node name |
| `GET /api/projects/{name}/impact/file` | Impact analysis by file path |
| `POST /api/projects/ReAnalyze` | Trigger re-analysis for a repository |
| `DELETE /api/projects/{name}` | Remove an indexed repository |
| `GET /api/graph/overview` | Cross-repo graph overview |
| `GET /api/search` | Search repositories and nodes |
| `GET /api/nodes/by-file` | Nodes for a file |
| `GET /api/nodes/{id}` | Node detail |
| `GET /api/nodes/{id}/source` | Source for a node |
| `PUT /api/nodes/{id}/do-not-trust` | Mark a node as untrusted |
| `GET /api/nodes/search` | Node search |
| `GET /api/clusters`, `/api/clusters/graph`, `/api/clusters/{id}` | Service cluster views |

### Reviews and diagnostics

| Route | Purpose |
|---|---|
| `POST /api/projects/{repo}/reviews` | Start a project-level review |
| `GET /api/projects/{repo}/reviews/latest?projectName=...` | Latest project review |
| `GET /api/projects/{repo}/reviews/{reviewRunId}/stream` | SSE stream for a project review |
| `GET /api/projects/{repo}/diagnostics` | Project diagnostics and review prep data |
| `POST /api/projects/{repo}/code-review` | Start a repository-level review |
| `GET /api/projects/{repo}/code-review/latest` | Latest repository review |
| `GET /api/projects/{repo}/code-review/{reviewRunId}` | Repository review detail |
| `GET /api/projects/{repo}/code-review/{reviewRunId}/stream` | SSE stream for a repository review |

### Memory

| Route | Purpose |
|---|---|
| `POST /api/memory/claims/store` | Queue claim-centric memory writes |
| `GET /api/memory/writes/{receiptId}` | Check durable write status |
| `GET /api/memory/search` | Search memory claims/entities |
| `POST /api/memory/subgraph` | Fetch a bounded structured subgraph |
| `GET /api/memory/entities/{id}/bundle` | Entity bundle with nearby claims |
| `GET /api/memory/entities/{id}/graph` | Focused graph snapshot for an entity |
| `GET /api/memory/claims/{id}` | Claim bundle |
| `POST /api/memory/frontier/expand` | Expand memory frontier |
| `POST /api/memory/render-summary` | Render a human-readable summary |
| `GET /api/memory/query` | Convenience query returning structure plus summary |
| `GET /api/memory/graph` | Paginated global memory graph snapshot |
| `POST /api/memory/migrate-legacy` | Legacy relationship migration |
| `POST /api/memory/migrate-observations` | Observation migration |

### Settings, operations, and wiki

CodeGraph has largely moved operational endpoints under `api/settings` rather than the older `api/admin` naming.

| Route | Purpose |
|---|---|
| `POST /api/settings/processRepos` | Queue one or more repositories |
| `POST /api/settings/reIndexAll` | Re-index all known repos |
| `POST /api/settings/link` | Run cross-repo linking |
| `POST /api/settings/detectCommunities` | Re-run community detection |
| `POST /api/settings/linkAndDetect` | Link then detect communities |
| `POST /api/settings/processBatchAnalysis` | Resume/process pending batch analysis |
| `POST /api/settings/discover` | Discover repositories from the configured source |
| `GET /api/settings/db-health` | Neo4j health and index diagnostics |
| `GET/POST/PUT/DELETE /api/settings/schedules...` | Embedded job scheduling |
| `GET/POST/PUT/DELETE /api/settings/sections...` | Wiki section management |
| `GET/POST/PUT/DELETE /api/settings/exclusions...` | Exclusion rule management |
| `POST /api/settings/mcp/regenerate` | Rebuild generated MCP documentation pages |
| `GET /api/wiki/...` | Wiki tree, page, revision, and attachment operations |
| `POST /api/ask` | Streaming Ask experience |

## Angular UI

`CodeGraphWeb/` exposes the main product surfaces:

- `/repos` and `/repos/:name` for repository browsing, health, security, vitality, and review workflows
- `/graph` for the repository dependency graph
- `/clusters` for service cluster visualization
- `/impact` for blast-radius analysis
- `/search` for global search
- `/ask` for streaming assistant interactions
- `/memory` for the memory browser and entity-focused graph exploration
- `/wiki/...` for the conventions/wiki system
- `/settings/...` for operations, schedules, DB health, sections, and exclusions

## MCP Server

The API hosts an MCP server at [http://localhost:5037/mcp](http://localhost:5037/mcp).

Example client config:

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

### Graph tools

- `get_graph_schema`
- `list_projects`
- `search_graph`
- `get_service_summary`
- `trace_call_path`
- `trace_data_lineage`
- `find_consumers`
- `find_publishers`
- `get_architecture`
- `get_project_health`
- `get_fleet_health`
- `find_archival_candidates`
- `get_service_clusters`
- `get_cluster_detail`
- `analyze_impact`
- `read_node_source`
- `get_code_snippet`
- `list_conventions`
- `get_convention`

### Memory tools

- `store_memory_v2`
- `get_memory_write_status`
- `query_memory`
- `search_memory`
- `get_memory_subgraph`
- `get_entity_bundle`
- `get_claim_bundle`
- `expand_memory_frontier`
- `render_memory_summary`
- `migrate_legacy_memory_graph`
- `migrate_memory_observations`

The API also exposes MCP resources and can regenerate wiki-backed MCP documentation from current tool metadata.

## Optional Git Hooks

Versioned git hooks live in [.githooks](/Users/michael/Repos/CodeGraph/.githooks), with an installer at [tools/install-git-hooks.sh](/Users/michael/Repos/CodeGraph/tools/install-git-hooks.sh).

```bash
./tools/install-git-hooks.sh
```

By default:

- `post-commit` triggers repository indexing
- `post-merge` triggers repository indexing plus analysis

Both hooks call `POST /api/settings/processRepos` asynchronously.

## Testing

```bash
dotnet test CodeGraph.sln
dotnet test src/CodeGraph.Tests
dotnet test src/CodeGraph.Jobs.Tests
dotnet test --filter "FullyQualifiedName~TestMethodName"
```

Tests use xUnit and Shouldly, with extensive in-memory fakes for graph, metrics, reviews, jobs, and memory flows.
