# CodeGraph

CodeGraph is a self-maintaining .NET 10 platform that indexes source repositories into a MariaDB-backed knowledge graph, generates `CODEGRAPH.md` documentation, exposes graph and memory tooling over REST and MCP, and ships with an Angular UI for exploration, reviews, operations, and memory browsing.

It is designed around one rule: if a feature needs ongoing human babysitting to stay correct, it will rot.

## Standalone Rebase Note

This branch starts the standalone rebase from `/Users/michael/Repos/TC.CodeGraphApi` into this repository. The target runtime is `.NET 10`, and the first phase is dependency removal, donor-surface import, and `TC.CodeGraphApi` to `CodeGraph` renaming. Behavior changes should stay out of the mechanical import slices unless they are required to remove TC platform dependencies or keep the solution buildable.

The working inventory is tracked in [plans/standalone-rebase-inventory.md](/Users/michael/Repos/CodeGraph/plans/standalone-rebase-inventory.md).

## Core Principle

**Self-maintaining.** CodeGraph should discover repositories, re-index them, refresh generated docs, surface risk, and clean up stale data without requiring a person to keep the system coherent by hand.

## What CodeGraph Does

- Indexes repositories into a structural graph of code, APIs, messaging, jobs, packages, and database objects
- Extracts across multiple languages: C# via Roslyn, TypeScript/Angular via a Node sidecar, T-SQL via ScriptDom, Ansible, Terraform/HCL, ColdFusion, and Tree-sitter as a fallback
- Recognizes modern .NET repository layouts, including solution-level analysis from top-level `.sln` and `.slnx` files
- Links repositories through HTTP calls, MassTransit messaging, shared packages, and other cross-repo signals
- Generates natural-language repository and project analysis with confidence indicators and optional auto-commit/auto-push of `CODEGRAPH.md`
- Supports multiple AI backends for analysis: Anthropic, OpenAI, Gemini, and local OpenAI-compatible endpoints
- Computes repository health, hotspot metrics, security findings, .NET support posture, and repository vitality trends
- Runs project-level and repository-level AI code reviews with persisted findings and SSE streaming updates
- Stores claim-centric personal memory in MariaDB, including evidence, conflicts, bounded subgraphs, and frontier expansion
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
│   ├── CodeGraph.Data.MariaDb/          # MariaDB/MySQL-backed implementations
│   ├── CodeGraph.Data.Neo4j/            # Temporary compatibility/export provider
│   ├── CodeGraph.Jobs/                  # Background job host and embedded schedule runner
│   ├── CodeGraph.Extractors.Ansible/     # Ansible playbook/role extraction
│   ├── CodeGraph.Extractors.ColdFusion/  # ColdFusion CFM/CFC extraction
│   ├── CodeGraph.Extractors.CSharp/     # Roslyn-based extraction
│   ├── CodeGraph.Extractors.Sql/        # T-SQL extraction
│   ├── CodeGraph.Extractors.Terraform/  # Terraform/HCL extraction
│   ├── CodeGraph.Extractors.TypeScript/ # TypeScript/Angular sidecar integration
│   ├── CodeGraph.Extractors.TreeSitter/ # Fallback multi-language extraction
│   ├── CodeGraph.Tests/                 # Main test suite
│   └── CodeGraph.Jobs.Tests/            # Job-host test suite
├── CodeGraphWeb/                        # Angular frontend
└── sql/migrations/                      # MariaDB schema and feature migrations
```

### Dependency flow

```text
Models <- Data <- Services <- Extractors.*
                         <- Api
                         <- Jobs
```

`CodeGraph.Api` hosts the REST API, the MCP endpoint, and the MassTransit consumers. `CodeGraph.Jobs` runs scheduled and manual background jobs. `CodeGraphWeb` is the Angular UI. MariaDB/MySQL is the primary datastore, RabbitMQ backs the event-driven pipeline, and Neo4j remains only as a temporary compatibility/export provider during the standalone rebase.

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
- Durable indexer runs for repository processing, discovery, linking, community detection, batch processing, and database schema sync
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

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- MariaDB/MySQL 11.x-compatible server
- RabbitMQ
- [Node.js 18+](https://nodejs.org/)
- A repository source configuration: `Folder`, `GitHub`, or `GitLab`
- At least one configured analysis provider if you want AI analysis or reviews

For Docker-based local model setups, the default local provider points at `http://host.docker.internal:1234/v1`.

For repository indexing, CodeGraph expects one or more top-level solution files (`.sln` or `.slnx`) when you want full Roslyn solution analysis. The Docker API image also includes compatibility SDKs for older repositories and `libssl1.1` for legacy .NET Core 2.1/global.json scenarios.

## Quick Start

### 1. Configure settings

The main local config file is [src/CodeGraph.Api/appsettings.json](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/appsettings.json). Docker-friendly defaults live in [.env.example](/Users/michael/Repos/CodeGraph/.env.example).

Useful settings to know:

| Setting | Purpose |
|---|---|
| `CodeGraph:StorageOptions:*` | MariaDB provider, migration, encryption-key, and embedding model settings |
| `CodeGraph:AuthOptions:*` | Standalone auth settings: local dev identity by default, optional OIDC/JWT authority/audience/client/scope/CORS settings |
| `CodeGraph:McpOptions:RequirePersonalAccessToken` | Require issued MCP PATs for `/mcp`; defaults off for local dev. When OAuth auth is enabled, `/mcp` is PAT-only even if this is not set. |
| `CodeGraph:AssistantRetentionOptions:*` | Stale assistant run and assistant history/debug retention settings used by the jobs cleanup task |
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

The compose stack terminates TLS in the `codegraph-web` container and forwards the API and MCP traffic to the internal `codegraph-api` container over the Docker network.
By default it binds only to `127.0.0.1:8443` so it does not take over shared host ports like `80` or `443`.

The compose stack includes the CodeGraph application services:

- `codegraph-api`
- `jobs`
- `codegraph-web`

MariaDB and RabbitMQ are expected to be shared containers on the external `trefry-network`; this compose file does not create them. The default container hostnames are `mariadb` and `rabbitmq`, and you can override `CodeGraph__StorageOptions__MariaDbConnectionString` or `CodeGraph__RabbitMqOptions__Host` when your shared services use different names.

By default compose mounts the parent repo folder (`../`) into the containers at `/repos`, with a writable named volume at `/repos/.cache`. Override `CODEGRAPH_DOCKER_REPOS_MOUNT` when your local repositories live somewhere else.

Embeddings are expected under `/models` in containers. The default model path is `/models/embeddings/all-MiniLM-L6-v2/model.onnx`.

Docker HTTPS endpoints:

- Web UI: [https://localhost:8443](https://localhost:8443)
- API: [https://localhost:8443/api](https://localhost:8443/api)
- Swagger: [https://localhost:8443/swagger](https://localhost:8443/swagger)
- MCP: [https://localhost:8443/mcp](https://localhost:8443/mcp)

### Public trefry.net OAuth deployment

For the public deployment, expose the services as:

- Web UI: `https://codegraph.trefry.net`
- API: `https://codegraph-api.trefry.net/api`
- MCP: `https://codegraph-mcp.trefry.net/mcp`
- Identity provider: `https://identity.trefry.net`

The Angular app uses Authorization Code + PKCE. The API validates bearer JWTs from the configured authority. MCP access is restricted to user-issued personal access tokens and should not accept browser OAuth tokens.

Required public auth settings:

```bash
CodeGraph__AuthOptions__Enabled=true
CodeGraph__AuthOptions__Authority=https://identity.trefry.net/realms/trefry
CodeGraph__AuthOptions__Audience=codegraph-api
CodeGraph__AuthOptions__ClientId=codegraph-web
CodeGraph__AuthOptions__Scope="openid profile email"
CodeGraph__AuthOptions__AuthorizationUrl=https://identity.trefry.net/realms/trefry/protocol/openid-connect/auth
CodeGraph__AuthOptions__TokenUrl=https://identity.trefry.net/realms/trefry/protocol/openid-connect/token
CodeGraph__AuthOptions__EndSessionUrl=https://identity.trefry.net/realms/trefry/protocol/openid-connect/logout
CodeGraph__AuthOptions__AllowedOrigins__0=https://codegraph.trefry.net
CodeGraph__AuthOptions__RequireHttpsMetadata=true
CodeGraph__McpOptions__RequirePersonalAccessToken=true
```

Register the SPA client in `identity.trefry.net` with redirect URI `https://codegraph.trefry.net/auth/callback`, post-logout redirect URI `https://codegraph.trefry.net`, and allow CORS/token calls from `https://codegraph.trefry.net`.

### GitHub Actions CI/CD

GitHub Actions workflows live under `.github/workflows`:

- `ci.yml` runs on pull requests and pushes to `main`/`codex/**`. It restores, builds, and tests the .NET solution; builds and tests the Angular app; runs the browser shell smoke; and validates Docker image builds for API, jobs, and web.
- `deploy.yml` runs on `main` and can be started manually. It publishes API, jobs, and web images to GHCR, then deploys through SSH when the repository or environment variable `CODEGRAPH_DEPLOY_ENABLED=true`.

Deployment uses `docker-compose.yml` plus `deploy/docker-compose.production.yml` on the host. Required GitHub environment secrets are documented in [deploy/README.md](/Users/michael/Repos/CodeGraph/deploy/README.md).

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
| `GET /api/settings/db-health` | MariaDB schema/index health diagnostics |
| `GET/POST/PUT/DELETE /api/settings/schedules...` | Embedded job scheduling |
| `GET/POST/PUT/DELETE /api/settings/sections...` | Wiki section management |
| `GET/POST/PUT/DELETE /api/settings/exclusions...` | Exclusion rule management |
| `POST /api/settings/mcp/regenerate` | Rebuild generated MCP documentation pages |
| `GET /api/wiki/...` | Wiki tree, page, revision, and attachment operations |
| `POST /api/ask` | Streaming Ask experience |
| `POST/GET /api/ask/runs...` | Persisted Ask runs, SSE replay, cancellation, and debug exchange inspection |
| `GET/POST/DELETE /api/user/mcp-tokens...` | User MCP personal access token list/create/revoke |
| `GET/POST/DELETE /api/admin/admins...` | Standalone admin-user management backed by MariaDB |
| `GET/PUT/DELETE /api/admin/prompts...` | Admin prompt catalog and prompt override management for analysis, review, and Ask assistant system prompts |
| `GET /api/admin/reports...` | Assistant, MCP, review, and repository-analysis usage reports |
| `GET /api/migration/neo4j-to-mariadb/dry-run` | Ordered Neo4j-to-MariaDB migration plan with live counts for the repositories/graph slice |
| `POST /api/migration/neo4j-to-mariadb/repositories-graph/run` | Execute the first Neo4j-to-MariaDB migration slice for repositories, graph nodes/edges, and cross-repo edges with returned checkpoints |
| `GET/POST/PUT/DELETE /api/database-sources...` | Database source configuration with masked connection strings |
| `POST /api/database-sources/{id}/sync` and `POST /api/database-sources/sync-all` | Queue and start durable schema-sync indexer runs |
| `POST /api/indexer/repositories/process` | Queue durable processing for specific repositories |
| `POST /api/indexer/repositories/reindex-all` | Queue durable re-indexing for all known repositories |
| `POST /api/indexer/repositories/discover` | Queue durable repository discovery and processing |
| `POST /api/indexer/link`, `/api/indexer/communities/detect`, and `/api/indexer/link-and-detect` | Queue durable graph linking and community operations |
| `POST /api/indexer/batch-analysis/process` | Queue durable batch-analysis result processing |
| `GET /api/indexer/runs` and `GET /api/indexer/runs/{runId}` | List recent durable indexer runs and inspect run status |

## Angular UI

`CodeGraphWeb/` exposes the main product surfaces:

- `/repos` and `/repos/:name` for repository browsing, health, security, vitality, and review workflows
- `/graph` for the repository dependency graph
- `/clusters` for service cluster visualization
- `/impact` for blast-radius analysis
- `/search` for global search
- `/ask` for streaming assistant interactions
- `/access-tokens` for user-managed MCP personal access tokens
- `/memory` for the memory browser and entity-focused graph exploration
- `/wiki/...` for the conventions/wiki system
- `/settings/...` for operations, schedules, DB health, sections, exclusions, admin users, prompt overrides, database sources, reports, and assistant debug inspection

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
