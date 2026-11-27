# CodeGraph

CodeGraph indexes source repositories into a MariaDB-backed knowledge graph and gives people and agents a shared place to plan, track, and validate work across those repositories. The .NET 10 backend serves REST and MCP. The Angular web app brings together repository exploration, code reviews, project work, knowledge, memory, and operations.

CodeGraph discovers and re-indexes repositories, refreshes generated `CODEGRAPH.md` summaries, and prunes expired operational data on a schedule.

[Quick start](#quick-start) · [Work tracking](#work-tracking-and-agent-coordination) · [Memory and knowledge](#memory-and-project-knowledge) · [MCP server](#mcp-server) · [Production deployment](deploy/README.md)

## What CodeGraph Does

- Indexes repositories into a structural graph of code, APIs, messaging, jobs, packages, and database objects
- Extracts C# via Roslyn, JavaScript and TypeScript via a Node sidecar, T-SQL via ScriptDom, Rust via rust-analyzer/SCIP with a Tree-sitter fallback, Swift via Tree-sitter, and Ansible, Terraform, ColdFusion, and other supported languages
- Recognizes modern .NET repository layouts, including solution-level analysis from top-level `.sln` and `.slnx` files
- Follows frontend routes, renders, and calls to backend HTTP endpoints and Tauri commands, and links repositories through messaging and shared packages
- Generates natural-language repository and project analysis with confidence indicators and optional auto-commit/auto-push of `CODEGRAPH.md`
- Supports multiple AI backends for analysis: Anthropic, OpenAI, and LM Studio/OpenAI-compatible endpoints
- Computes repository health, hotspot metrics, security findings, .NET support posture, and repository vitality trends
- Runs AI reviews for individual .NET projects and whole repositories, with persisted findings, paged history, and live updates
- Tracks projects, features, items, dependencies, acceptance, and GitHub evidence. Sessions bind claims, handoffs, messages, and resource holds to named people or agents
- Stores claims, evidence, and conflicts in a browsable memory graph. Project knowledge tracks decisions, findings, and lessons, with scheduled curation through Dreaming
- Exposes graph, memory, knowledge, and work tools over MCP alongside the Angular UI

## Solution Structure

```text
CodeGraph/
├── src/
│   ├── CodeGraph.Api/                   # ASP.NET Core API host, MCP host, controllers
│   ├── CodeGraph.Host.Shared/           # Shared host/auth/health infrastructure for split services
│   ├── CodeGraph.Indexer.Client/        # Typed HTTP client for indexer operations
│   ├── CodeGraph.Indexer.Host/          # Standalone indexing execution host and consumers
│   ├── CodeGraph.Memory.Client/         # Typed HTTP client for memory operations
│   ├── CodeGraph.Memory.Host/           # Standalone memory API and async write consumer
│   ├── CodeGraph.Metrics/               # Standalone telemetry consumer and persistence host
│   ├── CodeGraph.Models/                # Domain models, request/response DTOs, messages, memory contracts
│   ├── CodeGraph.Services/              # Analysis, indexing, query engine, assistant, reviews, memory, wiki
│   ├── CodeGraph.Data/                  # Store interfaces and shared entities
│   ├── CodeGraph.Data.MariaDb/          # MariaDB/MySQL-backed implementations
│   ├── CodeGraph.Data.Neo4j/            # Temporary compatibility/export provider
│   ├── CodeGraph.Jobs/                  # Background job host and embedded schedule runner
│   ├── CodeGraph.Mcp.Hub/               # MCP tool catalog, entitlements, and provider gateway
│   ├── CodeGraph.Extractors.Ansible/     # Ansible playbook/role extraction
│   ├── CodeGraph.Extractors.ColdFusion/  # ColdFusion CFM/CFC extraction
│   ├── CodeGraph.Extractors.CSharp/     # Roslyn-based extraction
│   ├── CodeGraph.Extractors.Rust/       # rust-analyzer/SCIP extraction
│   ├── CodeGraph.Extractors.Sql/        # T-SQL extraction
│   ├── CodeGraph.Extractors.Terraform/  # Terraform/HCL extraction
│   ├── CodeGraph.Extractors.TypeScript/ # TypeScript/Angular sidecar integration
│   ├── CodeGraph.Extractors.TreeSitter/ # Fallback multi-language extraction
│   ├── CodeGraph.Tests/                 # Main test suite
│   └── CodeGraph.Jobs.Tests/            # Job-host test suite
├── CodeGraphWeb/                        # Angular frontend
├── tools/cg/                            # CLI for harness hooks and session coordination
└── sql/migrations/                      # MariaDB schema and feature migrations
```

### Dependency flow

```text
Models <- Data <- Services <- Extractors.*
                         <- Api
                         <- Indexer.Host
                         <- Memory.Host
                         <- Jobs
```

`CodeGraph.Api` hosts REST and MCP, including the work tracker and project knowledge. `CodeGraph.Indexer.Host` executes durable indexing, linking, schema sync, and scheduled Dreaming runs. `CodeGraph.Memory.Host` serves memory and consumes queued memory writes. The API can use its integrated indexer and memory services or delegate to those hosts over HTTP. `CodeGraph.Metrics` persists LLM and MCP telemetry, and `CodeGraph.Jobs` runs the scheduler. MariaDB is the runtime datastore, RabbitMQ carries asynchronous work, and the solution retains a Neo4j compatibility/export provider.

### C# repository tooling trust

C# indexing is syntax-only by default. Restore and MSBuild solution analysis can execute repository-controlled build logic, so they run only for provider-resolved identities explicitly listed in the comma-separated `CodeGraph:IndexingOptions:TrustedDotnetRepositories` setting. Identity forms are `github:https://host/owner/repo`, `gitlab:https://host/group/repo`, and `folder:relative/path`; local paths outside the configured folder root use `folder-path:/absolute/path`. Folder paths are case-sensitive on non-Windows hosts. User-supplied URL, group, and path combinations cannot grant trust unless they agree with the provider resolution. Every enabled or blocked solution-analysis decision emits a `SECURITY-AUDIT` log entry with the resolved identity. Production Compose exposes the setting as `CODEGRAPH_TRUSTED_DOTNET_REPOSITORIES` and defaults it to empty.

## Key Capabilities

### Knowledge graph and discovery

- Repository, namespace, file, class, method, route, service, event, queue, exchange, table, component, and job indexing
- Cross-repo traversal for callers, consumers, publishers, data lineage, impact analysis, and service clustering
- Trust-aware search and source lookup for graph nodes
- Commit-specific reads through branch overlays, with the indexed commit reported in graph responses

### Frontend and backend dependencies

The sidecar analyzes admitted `.js`, `.jsx`, `.ts`, and `.tsx` files, including JavaScript apps without a `tsconfig`. It follows Angular lazy routes, templates and dependency injection, React components and React Router, and Next.js App Router pages and layouts. A desktop or state-selected screen can have render and call dependencies even when it has no URL.

CodeGraph follows statically resolvable request wrappers to HTTP endpoints, including Axum and Gin registrations, and links Tauri calls and named events to the owning app's Rust registrations. It follows shared frontend packages through their exported symbols. Graph tracing and file impact can use these edges to identify affected components, screens, and routes. Incremental indexing refreshes affected relationships when source, templates, configuration, exports, or backend targets change. Dynamic or ambiguous targets can remain unresolved.

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
- Paged repository review history with stale indicators when HEAD has moved, exact run links, and SSE progress streams

### Operations and wiki

- Repository discovery from local folders, GitHub, or GitLab
- Embedded scheduling, manual execution, and durable job failures visible in **Settings → Operations**
- Durable indexer runs for repository processing, discovery, linking, community detection, batch processing, and database schema sync
- Hierarchical wiki with sections, nested pages, attachments, revisions, and auto-generated MCP documentation

## Work tracking and agent coordination

A **project** groups indexed repositories and owns features, items, and shared knowledge. A feature holds goals, numbered requirements, acceptance tests, and documents. A story, task, or bug belongs to a project and can also belong to a feature. The item can inherit feature requirements and record the repository and files it touches. The feature view shows both linked items and coverage by completed items for each requirement.

- Create a plan with `create_work`. Preview and apply changes with `sync_plan`, which uses stable caller references to update the plan and its dependencies together.
- Start with `start_session` or `cg brief`. The briefing includes relevant rules, current claims and handoffs, and messages. Pass `session_id` on work calls to renew the heartbeat and preserve attribution.
- Use `next_work` to claim eligible work in a project, optionally within one feature. It checks for recoverable in-progress items first, then ready items. Unfinished dependencies and blocking questions affect eligibility. `update_work` uses `expected_version` and returns the current object on conflict. Save decisions, findings, attempts, and corrections with `update_work` or `end_session`. Leave a handoff when releasing unfinished work.
- Use `post`, `inbox`, and `ack` for durable messages and item threads. Live sessions receive advisory warnings about overlapping changes. Resource holds record owners and waiters for shared resources such as GPUs and runners.
- A linked push or PR can advance the item's status. A merge flags `merged_awaiting_acceptance`. Complete the item with validation evidence and an acceptance report. Feature completion checks that every item is complete or closed, at least one item is complete, and every active requirement is covered by completed work.

CodeGraph became the work tracker on November 2, 2026. The Shortcut integration and the Shortcut and KanbanBoard work importers were retired. Imported records, attachments, and `sc-NNNN` aliases remain available. [Imported work and attachment storage](deploy/README.md#imported-work-shortcut-kanbanboard)

## Memory and project knowledge

The **Memory** graph stores entities, claims, supporting evidence, and observations about contradictions. Writes return durable receipts. Use `memory_read` or the individual memory read tools for search, bounded subgraphs, claims, and entity bundles. Pass `project` to scope reads to a work project. REST reads use `?project=` on GET requests and `projectKey` on POST request bodies.

MCP writes take an explicit `project` or derive scope from `sessionId`. With neither, they use the project shared by all the caller's live sessions, when there is exactly one such project. Otherwise the write stays unscoped. CodeGraph queues a memory backfill when a project gains repositories. The daily `ReconcileMemoryProjectScopes` job retries pending backfills and uses a durable watermark to reconcile new and changed memory incrementally.

**Knowledge** stores project decisions, constraints, facts, gotchas, attempts, and lessons. `record` writes entries directly, and `recall` filters them by project, repository, path, item, or activity. Repository changes can flag source-backed entries as possibly stale. Briefings surface relevant knowledge and rules by activity and scope.

A correction from the owner becomes a rule immediately, including a correction relayed by an agent. Lessons inferred by agents or review bots become rules after recurrence across distinct sources (`Work:Lessons:RuleRecurrenceThreshold`, default 2). Approvals show open challenges, conflicting lessons, and Dreaming proposals. Autonomous decisions appear in knowledge and briefings.

### Dreaming

Dreaming is a scheduled or manual curation pass over one project's knowledge and evidence. The seeded `DreamProjects` schedule runs at 02:00 UTC. Runs with no new signal skip the model call. Each run uses a configured model that supports tools, a turn and token budget, and versioned instructions from the wiki.

The dreamer can make permitted, evidence-backed changes within the project. Proposals to retract, supersede, or merge entries, or to draft skills, hooks, or tests, go to Approvals. The **Dreaming** view shows run history, evidence, usage and estimated cost, settings, and instruction revisions. Interrupted runs can expose a continuation from a saved checkpoint. Reversion creates new revisions for the run's direct writes and reports conflicts with later edits. [Dreaming tool and REST routes](#work-knowledge-and-dreaming)

## AI Provider Support

Manage runtime providers under **Settings → LLM Configuration**.

- Supported built-in providers are `anthropic`, `openai`, and `lmstudio`
- The `lmstudio` provider uses an OpenAI-compatible endpoint

The LLM Configuration page owns:

- Provider tokens, endpoint overrides, API versions, and model lists
- Default Analysis provider/model and token/file/parallel caps
- Default Review provider/model and review caps
- Default Assistant provider/model, max tokens, and max turns

`AnalysisOptions` provides local-development fallback values. Save production provider settings in the admin page. Startup warnings tagged `llm.config.deprecation` identify fallback values still in use. Dreaming has its own model and budget settings and uses the configured provider credentials.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- MariaDB 11.8 for the standard setup, matching CI and the shared database Compose file
- RabbitMQ
- [Node.js 22](https://nodejs.org/), matching CI for Angular and the extraction sidecar
- A C compiler and `curl` on macOS/Linux (Xcode command line tools or gcc): the build compiles the tree-sitter Swift grammar from pinned, hash-verified sources (`src/CodeGraph.Extractors.TreeSitter/native/build-tree-sitter-swift.sh`)
- A repository source configuration: `Folder`, `GitHub`, or `GitLab`
- At least one configured analysis provider if you want AI analysis or reviews

For Docker-based local model setups, the default LM Studio provider points at `http://host.docker.internal:1234/v1`.

For repository indexing, CodeGraph expects one or more top-level solution files (`.sln` or `.slnx`) when you want full Roslyn solution analysis. The Docker API image also includes compatibility SDKs for older repositories and `libssl1.1` for legacy .NET Core 2.1/global.json scenarios.

## Quick Start

### 1. Configure settings

The main local config file is [src/CodeGraph.Api/appsettings.json](src/CodeGraph.Api/appsettings.json). Compose defaults live in [.env.example](.env.example).

Useful settings to know:

| Setting | Purpose |
|---|---|
| `CodeGraph:StorageOptions:*` | MariaDB provider, migration, encryption-key, and embedding model settings |
| `CodeGraph:StorageOptions:MariaDbMigrationLockTimeoutSeconds` | Maximum wait for the database-scoped startup migration lock; defaults to 120 seconds |
| `CodeGraph:AuthOptions:*` | Standalone auth settings: local dev identity by default, optional OIDC/JWT authority/audience/client/scope/CORS settings |
| `CodeGraph:McpOptions:RequirePersonalAccessToken` | Require an issued user or agent MCP token. When OAuth auth is enabled, `/mcp` always requires one. |
| `CodeGraph:Indexer:BaseUrl` | Optional remote indexer host URL, for example `http://localhost:5042`; when empty, API keeps the local integrated indexer fallback. |
| `CodeGraph:Memory:BaseUrl` | Optional remote memory host URL, for example `http://localhost:5039`; when empty, API/MCP keep the local memory fallback. |
| `CodeGraph:InternalServiceAuth:*` | HMAC internal service identity used by API/jobs clients when talking to split hosts. Disabled by default for local development. |
| `CodeGraph:AssistantRetentionOptions:*` | Stale assistant run and assistant history/debug retention settings used by the jobs cleanup task |
| `CodeGraph:RepositorySource:Provider` | Choose `Folder`, `GitHub`, or `GitLab` |
| `CodeGraph:RepositorySource:Folder:RootPath` | Local repo root when using the folder provider |
| `CodeGraph:RepositorySource:GitHub:IncludeAuthenticatedUser` | Include all repositories accessible to the configured GitHub token; defaults to `true` with multi-owner settings |
| `CodeGraph:RepositorySource:GitHub:UserAccounts` | Comma-separated additional GitHub user accounts; named-user discovery includes their public repositories |
| `CodeGraph:RepositorySource:GitHub:Organizations` | Comma-separated GitHub organizations; token permissions determine private repository visibility |
| LLM Configuration admin page | Runtime LLM provider tokens, endpoints, model lists, and Analysis/Review/Assistant defaults |
| `CodeGraph:AnalysisOptions:AutoCommitDocs` | Auto-commit generated `CODEGRAPH.md` files |
| `CodeGraph:AnalysisOptions:AutoPushDocs` | Auto-push generated doc commits |
| `CodeGraph:TsPort` | TypeScript analyzer sidecar port |

#### Index multiple GitHub accounts and organizations

The GitHub provider can merge the authenticated user's accessible repositories with explicit user and organization scopes. Repositories returned by more than one scope are deduplicated by GitHub repository ID.

For the personal `michaeltrefry` account plus the `SceneWorks` organization, the authenticated-user scope already covers the personal account, so configure:

```json
"RepositorySource": {
  "Provider": "GitHub",
  "GitHub": {
    "BaseUrl": "https://api.github.com",
    "PersonalAccessToken": "",
    "IncludeAuthenticatedUser": true,
    "UserAccounts": "",
    "Organizations": "SceneWorks"
  }
}
```

The Docker/production equivalent is:

```bash
CodeGraph__RepositorySource__Provider=GitHub
CodeGraph__RepositorySource__GitHub__IncludeAuthenticatedUser=true
CodeGraph__RepositorySource__GitHub__UserAccounts=
CodeGraph__RepositorySource__GitHub__Organizations=SceneWorks
```

Use comma-separated values such as `SceneWorks,AnotherOrg` or `octocat,another-user` for additional scopes. `UserAccounts` uses GitHub's named-user endpoint, which returns public repositories. The authenticated-user endpoint retains private repositories that the token can access; organization scopes include private repositories only when the token has access. The legacy singular `Organization` setting remains supported and, when used alone, retains its organization-only behavior.

#### GitHub webhook

The webhook queues incremental indexing for pushes to an indexed repository's default branch and records GitHub evidence for work items. Set `CodeGraph__RepositorySource__GitHub__WebhookSecret` (the `CODEGRAPH__REPOSITORYSOURCE__GITHUB__WEBHOOKSECRET` Actions secret in production), then add a webhook on the repository or organization.

- Payload URL: `https://<codegraph-api-host>/api/webhooks/github`
- Content type: `application/json`
- Secret: the same value
- Events: **Pushes**, **Pull requests**, **Pull request reviews**, **Pull request review comments**, and **Issue comments**

A signed default-branch push returns `202` when it queues a sync. Repeated submissions for the same head SHA queue at most one sync. Scheduled sync remains the fallback. Other branch pushes, PRs, reviews, and comments can record evidence when they reference an item in a project that includes the repository. Evidence-only and ignored deliveries return `200`. A missing or invalid `X-Hub-Signature-256`, or a missing server secret, returns `401`. [Webhook settings, status transitions, and redelivery](deploy/README.md#github-webhook-and-work-evidence)

### 2. Run locally

Start MariaDB and RabbitMQ first. Create a `codegraph` database and configure the API's connection string with an account that can apply migrations. If you use the folder provider, set `CodeGraph:RepositorySource:Folder:RootPath` to the directory containing your repositories.

```bash
npm ci --prefix tools/ts-extractor
dotnet build CodeGraph.sln

# API + MCP host
dotnet run --project src/CodeGraph.Api

# Angular UI (in another terminal, from the repository root)
npm ci --prefix CodeGraphWeb
npm start --prefix CodeGraphWeb
```

With its default settings the API runs indexing and memory in-process. To run a split service locally, use `dotnet run --project src/CodeGraph.Indexer.Host` or `dotnet run --project src/CodeGraph.Memory.Host`, and point the API's corresponding `BaseUrl` at it. Run `dotnet run --project src/CodeGraph.Metrics` to collect telemetry and `dotnet run --project src/CodeGraph.Jobs` to run schedules.

Default endpoints:

- API: [http://localhost:5037](http://localhost:5037)
- Swagger: [http://localhost:5037/swagger](http://localhost:5037/swagger)
- MCP: [http://localhost:5037/mcp](http://localhost:5037/mcp)
- Indexer host: [http://localhost:5042](http://localhost:5042)
- Indexer sidecar health: [http://localhost:5042/health/sidecar](http://localhost:5042/health/sidecar)
- Memory host: [http://localhost:5039](http://localhost:5039)
- Memory write health: [http://localhost:5039/health/memory-write](http://localhost:5039/health/memory-write)
- Metrics host: [http://localhost:5041](http://localhost:5041)
- Web UI: [http://localhost:4200](http://localhost:4200)

### 3. Run with Docker Compose

Start MariaDB and RabbitMQ on the external `trefry-network` before launching the application. The default container hostnames are `mariadb` and `rabbitmq`. Copy the template, then edit the database connection string, RabbitMQ credentials, and repository and model mounts as needed.

```bash
cp .env.example .env
```

```bash
docker compose up --build
```

The compose stack serves the Angular UI over plain HTTP from the `codegraph-web` container and forwards API, Swagger, and MCP traffic to the internal `codegraph-api` container over the Docker network.
The web container binds to `127.0.0.1:4380` by default. Use `CODEGRAPH_DOCKER_HTTP_PORT` to change the port and terminate public TLS at the host reverse proxy.

The compose stack includes the CodeGraph application services:

- `codegraph-api`
- `codegraph-indexer`
- `codegraph-memory`
- `codegraph-metrics`
- `jobs`
- `codegraph-web`

API, indexer, memory, metrics, and jobs initialization is gated on the same MariaDB migration protocol. The first simultaneously starting host acquires a database-scoped advisory lock and applies pending, statement-journaled migrations; the remaining hosts wait, re-read migration history, and only then continue to readiness. If MariaDB is unavailable or the configured migration-lock timeout expires, the affected host fails startup rather than serving or running workers against a partial schema.

By default compose mounts the parent repo folder (`../`) into the containers at `/repos/workspace` as writable; remote providers clone and update repositories directly under that path. Override `CODEGRAPH_DOCKER_REPOS_MOUNT` when your host repositories should live somewhere else, or `CODEGRAPH_CONTAINER_REPOS_ROOT` if the in-container path must change.

Embeddings are expected under `/models` in containers. The default model path is `/models/embeddings/nomic-embed-text-v1.5/model.onnx`; the production deploy helper downloads the model and `vocab.txt` into the host model cache when they are missing.

Docker endpoints:

- Web UI: [http://localhost:4380](http://localhost:4380)
- API: [http://localhost:4380/api](http://localhost:4380/api)
- Development Swagger: [http://localhost:4380/swagger](http://localhost:4380/swagger)
- MCP: [http://localhost:4380/mcp](http://localhost:4380/mcp)
- Indexer host: [http://localhost:5042](http://localhost:5042)
- Indexer sidecar health: [http://localhost:5042/health/sidecar](http://localhost:5042/health/sidecar)
- Memory host: [http://localhost:5039](http://localhost:5039)
- Memory write health: [http://localhost:5039/health/memory-write](http://localhost:5039/health/memory-write)
- Metrics host: [http://localhost:5041](http://localhost:5041)

### Public trefry.net OAuth deployment

For the public deployment, expose the services as:

- Web UI: `https://codegraph.trefry.net`
- API: `https://codegraph-api.trefry.net/api`
- MCP: `https://codegraph-api.trefry.net/mcp`
- Identity provider: `https://identity.trefry.net`

The Angular app uses Authorization Code + PKCE. The API validates bearer JWTs from the configured authority. MCP uses CodeGraph-issued user or agent tokens.

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

- `ci.yml` runs on pull requests and pushes to `main`. Pushes that only change generated `CODEGRAPH.md` files skip CI. CI tests the backend against MariaDB 11.8, checks that the committed sidecar build is current, tests Angular and runs a browser smoke test, and validates the application images. It also checks Rust semantic indexing and the Swift grammar in the final indexer image, including ARM64.
- `deploy.yml` runs only after `ci.yml` succeeds for a push to `main` (a failed or cancelled CI run skips it), deploying exactly the commit CI tested, and can be started manually. It publishes API, jobs, indexer, memory, metrics, and web images to GHCR, then deploys through SSH when the production environment variable `CODEGRAPH_DEPLOY_ENABLED=true`.

Deployment uses `docker-compose.yml` plus `deploy/docker-compose.production.yml` on the host. The deploy workflow generates the remote `.env` file from GitHub Actions production environment variables and secrets. [Required settings and health checks](deploy/README.md)

## Event-Driven Pipeline

Repository processing is asynchronous and message-driven via MassTransit and RabbitMQ.

Typical flow:

1. A repository is queued for processing.
2. Indexing extracts graph data and publishes completion events.
3. Cross-repo linking, health/vitals, security, and analysis follow as downstream stages.
4. Analysis results are synthesized into repository summaries and `CODEGRAPH.md`.
5. Memory writes are queued separately through their own consumer path.

### Consumer retries and `_error`

CodeGraph does not need the RabbitMQ `rabbitmq_delayed_message_exchange` plugin: no host configures delayed redelivery, a delayed message scheduler, or scheduled publishes. Every receive endpoint retries in process while it holds the delivery (`ConsumerConfiguration` in `CodeGraph.Host.Shared`):

- Transient infrastructure failures (`MySqlException`, `SocketException`, Neo4j `ServiceUnavailableException`, `RetryableAnalysisException`) retry after 5 s, 15 s, 30 s and 60 s (about 2 minutes of waiting).
- Any other failure retries `ConsumerRetryLimit` times (default 3) after 1 s, 2 s, 3 s (`ConsumerRetryInitialInterval` / `ConsumerRetryIntervalIncrement`).
- `DoNotRetryException` is not retried.
- `process-repository` retries only failures in the pre-index phases (`resolve-repository`, `check-up-to-date`); a failure from indexing onward is not retried in place, and the scheduled sync picks the repository up again.

When the retries are used up, MassTransit moves the message to the endpoint's `<queue>_error` queue. Retry time per delivery stays far below RabbitMQ's 30-minute `consumer_timeout`.

The API always handles wiki page changes. It also handles indexing and memory messages when it runs those services in-process. In the split deployment, the indexer and memory hosts own their respective queues.

## REST API Highlights

The API serves Swagger in development at [http://localhost:5037/swagger](http://localhost:5037/swagger).

### Repository and graph data

Use `/api/repositories` for indexed code and the `repository` or `repo` argument shown in the graph tool schema. The `project` argument on work and memory tools refers to a work project, which can span repositories.

| Route | Purpose |
|---|---|
| `GET /api/repositories` | List repositories with search, grouping, and pagination |
| `GET /api/repositories/{name}` | Repository detail |
| `GET /api/repositories/{name}/health` | Health, hotspots, security summary, .NET support, vitality |
| `GET /api/repositories/{name}/security` | Expanded security findings |
| `GET /api/repositories/{name}/metrics` | File metrics |
| `GET /api/repositories/{name}/hotspots` | Top hotspots |
| `GET /api/repositories/{name}/nodes` | Repository nodes by label/.NET project |
| `GET /api/repositories/{name}/readme` | Stored README content for an indexed repo |
| `GET /api/repositories/{name}/impact` | Impact analysis by node name |
| `GET /api/repositories/{name}/impact/file` | Impact analysis by file path |
| `POST /api/repositories/ReAnalyze` | Trigger re-analysis for a repository |
| `DELETE /api/repositories/{name}` | Remove an indexed repository |
| `GET /api/graph/overview` | Cross-repo graph overview |
| `GET /api/search` | Search repositories and nodes |
| `GET /api/nodes/by-file` | Nodes for a file |
| `GET /api/nodes/{id}` | Node detail |
| `GET /api/nodes/{id}/source` | Source for a node |
| `PUT /api/nodes/{id}/do-not-trust` | Mark a node as untrusted |
| `GET /api/nodes/search` | Node search |
| `GET /api/clusters`, `/api/clusters/graph`, `/api/clusters/{id}` | Service cluster views |

`/impact`, `/impact/file`, `/nodes/by-file`, `/nodes/{id}`, `/nodes/{id}/source` and `/nodes/search` (and the MCP tools `analyze_impact`, `get_code_snippet`, `read_node_source`, `search_graph`, `trace_call_path`, `trace_data_lineage`, `find_consumers`, `find_publishers`, `codegraph_search`, `graph_source` and `graph_trace`) take an optional `sha` (full 40-hex commit). With it they answer from the ready branch overlay for that commit layered over the default-branch index, with the overlay's deleted files left out; a SHA without a ready overlay returns a typed `commit_not_indexed` result (HTTP 409) carrying the overlay status (`queued`, `indexing`, `failed`, `expired`, `stale`, or `unknown`). Every answer names the commit it describes: REST responses carry `commitSha`; MCP answers start with `Commit: <sha>` and carry `structuredContent.commitSha` (the consolidated `codegraph_search`, `graph_trace` and `graph_source` tools return a JSON envelope, so the commit is in `envelope.commitSha` and `structuredContent.commitSha`, and the `Commit:` line opens the envelope's `result`). An answer that spans several repositories also carries `commits` (repository → commit): with a SHA, the other repositories are answered from their default-branch index commit; without one, a multi-repository answer has a null `commitSha` and a `Commit: mixed (...)` line. Without a SHA, source reads return the file at the default-branch index commit, falling back to the working tree only for sources with no git history.

MCP callers (for example a personal access token holder, who cannot call the admin-only `POST /api/indexer/overlays`) request and poll overlays with two model-free tools. `request_overlay(repo, ref)` takes a branch name, tag, or full commit SHA, resolves it to its current commit (`git ls-remote` against the repository checkout, no fetch), and returns the live overlay for that repository and commit if one exists (queued, indexing, or ready against the current default-branch index commit, within the TTL; `reused: true`), otherwise queues a new overlay pinned to that commit. `get_overlay_status(overlayId)` reports `queued`, `indexing`, `ready`, `failed`, or `expired`, plus `stale` for a ready overlay whose base is no longer the default-branch index commit. Both answer `overlayId`, `status`, `headSha` and `baseSha` in `structuredContent` (with a `Commit: <headSha>` first line) and return typed errors (`repo_not_found`, `invalid_ref`, `overlay_not_found`, ...). Failed, expired, and stale overlays are never reused: requesting again queues a new one. Like every native tool they are available to tokens in `all` mode and to `selected` tokens that list them.

Two model-free tools give per-file risk readings in `structuredContent`. `analyze_impact_file(repo, path, sha?, depth=3)` is the MCP form of `/impact/file`: it answers `found`, `blastRadius` (the worst risk level among the affected nodes, `critical`|`high`|`medium`|`low`, or `none` when nothing depends on the file or it has no nodes), `criticalCount`, `highCount`, `mediumCount`, `lowCount`, `totalAffected`, `crossRepoCount`, `crossRepoImpacts` (one `{sourceRepo, targetRepo, edgeType, count}` per repository pair), `lowConfidenceCount` and `unmappedInboundEdges` (null without a SHA), with `commitSha` and `source` (`overlay` or `default-branch`); a SHA without a ready overlay returns the typed not-indexed result. `file_hotspot(repo, path)` reads the default-branch vitals for one file: `found`, `riskScore`, `concernScore`, `healthScore`, `churn365d`, `complexityScore`, `computedAt` and `commitSha`, the checkout commit the vitals run computed them at (`file_metrics.commit_sha`), plus `indexCommitSha`. Metrics computed before that column existed have a null `commitSha` and a null `concernScore` (it was not stored) until the next vitals run. Both take a repository-relative path (`..`, absolute and empty segments are rejected with `invalid_path`) and answer `repo_not_found` for an unknown repository.

File tools return `languageIndexed` and `language` even when a file has no graph data. `languageIndexed` reflects the extensions registered by extractors and matches them case-sensitively. For example, `.swift`, `.js`, `.jsx`, `.ts`, and `.tsx` are indexed extensions, while `.m` and `.mm` are outside graph coverage. A file in an indexed language can still have `found: false` if it is new, deleted, excluded, or too large. Non-code files report `language: null`. The hotspot metrics cover some extensions outside graph coverage, so a file can have churn metrics without graph nodes.

Impact analysis follows inbound dependents across repository links within the requested depth and counts a dependent repository once, even if multiple paths reach it.

Cargo dependencies link repositories at the crate level. A dependency reference is linked to the crate's definition in another indexed repository by exact package key (registry crates), by its git URL matching that repository's URL (https, ssh and scp-style spellings compare equal; `matched_by: git_url`), or, for a path dependency that leaves its repository, by crate name, preferring a repository named like a directory on the path (`matched_by: path`, medium confidence). A git dependency on a URL that is not indexed is never linked by name. For a `.rs` file, `analyze_impact_file` adds the repositories whose manifests depend on the file's crate, directly or through crates that depend on it within `depth` package hops, to `crossRepoImpacts` with `edgeType: REFERENCES_PACKAGE`; these are crate-level dependents, so they do not change `blastRadius` or the affected counts. They are read from the default-branch Cargo manifests even when a `sha` is given (manifests are not part of an overlay), and a file with no nodes at the commit (`found: false`) reports none. Git links need the upstream repository's stored URL to be its remote, so a repository indexed from a local folder source is not matched by git URL. Cargo manifests are re-read on every index but a routine sync skips a repository whose HEAD is unchanged, so on an existing fleet a git link appears after the next commit to either repository is indexed and a path link after the next commit to the downstream one (or after a manual reindex).

Swift package dependencies link the same way (`ecosystem: swiftpm`). A `.package(url:)` declared in `Package.swift`, or a source-control pin in a `Package.resolved` (root, Xcode project or workspace; pins hang off the repository node), whose URL is an indexed repository's remote links to the package defined by that repository's root `Package.swift` (`matched_by: git_url`, high confidence; `.git` suffixes, case, ports and https/ssh/scp spellings are normalised). Each Swift link carries `dependency_kind`: `declared` (from `Package.swift`) or `pinned` (from `Package.resolved`). A pin is linked only when the same repository declares no dependency on that package, so a declared dependency and its pin count once; the pinned links that remain are an Xcode project's packages (it has no `Package.swift`) and transitive pins, which `Package.resolved` lists alongside direct ones, so a `pinned` link may be an indirect dependency. A `.package(path:)` that leaves its repository links by package name only to a repository named like a directory on the path (`matched_by: path`, medium confidence), because the name SwiftPM gives a path dependency is just its directory name. A remote URL shared by several indexed repositories is ambiguous: dependencies on it are not linked and the linker logs the URL and the repositories. A URL that matches no indexed repository stays an external package, and registry (`.package(id:)`) and local `file://` dependencies are not linked. For a `.swift` file, `analyze_impact_file` reports the repositories depending on the package of the nearest `Package.swift` above it, as it does for a Rust crate.

A Swift `import M` (also `@testable import M`) binds to the repository's own module `M` when one exists: a SwiftPM target of a `Package.swift`, or a native target of an Xcode project (`*.xcodeproj/project.pbxproj`) whose module name — `PRODUCT_MODULE_NAME`, else `PRODUCT_NAME` as a C99 identifier, else the target name — is `M`. When several provide it, the one nearest above the importing file wins (an Xcode target counts from the directory holding its `.xcodeproj`). Otherwise the import binds to a dependency package providing `M`, or to an external `swift-external-module:M` stub. Manifests that are not source files (`Cargo.toml`, `Package.resolved`, `project.pbxproj`) are tracked by content hash: an incremental run rewrites their package and module rows only when a manifest of their ecosystem changed.

`get_repo_dependencies(repo)` is the model-free form of the dependency lists in `get_architecture` (which also returns a stored model summary): `inbound` and `outbound` list the other repositories as `{repo, edgeType, count, confidenceBand}` from the default-branch cross-repository links (`confidenceBand` is the weakest link of the group, or null when the links carry none). Its `rustSemantic` object says how the repository's Rust code was indexed: `status` is `semantic`, `partial`, `fallback` or `not_analyzed`, and `treeSitterGroups` lists every crate group that was indexed by tree-sitter instead of rust-analyzer as `{index, crates, outcome, failureCode, detailCode}`. Those crates have no symbol-level cross-repository edges, so a missing dependency on them is not evidence that none exists.

### Reviews and diagnostics

| Route | Purpose |
|---|---|
| `POST /api/repositories/{repo}/reviews` | Start a project-level review |
| `GET /api/repositories/{repo}/reviews/latest?projectName=...` | Latest review of one .NET project (`projectName` is the project, not the repository) |
| `GET /api/repositories/{repo}/reviews/{reviewRunId}/stream` | SSE stream for a project review |
| `GET /api/repositories/{repo}/diagnostics` | Project diagnostics and review prep data |
| `POST /api/repositories/{repo}/code-review` | Start a repository-level review |
| `GET /api/repositories/{repo}/code-review?limit=20&before=...` | Page through repository review history by run ID |
| `GET /api/repositories/{repo}/code-review/latest` | Latest repository review |
| `GET /api/repositories/{repo}/code-review/{reviewRunId}` | Repository review detail |
| `GET /api/repositories/{repo}/code-review/{reviewRunId}/stream` | SSE stream for a repository review |

### Memory

| Route | Purpose |
|---|---|
| `POST /api/memory/claims/store` | Queue claim-centric memory writes |
| `GET /api/memory/writes/{receiptId}` | Check durable write status |
| `GET /api/memory/writes/diagnostics` | Inspect queued, stale, retrying, and failed memory writes |
| `GET /api/memory/diagnostics` | Inspect claim-graph counts, orphan rows, and memory write health |
| `POST /api/memory/cleanup/by-source` | Dry-run or delete claim-centric memory from one source and rebuild affected projections |
| `POST /api/memory/cleanup/test-data` | Dry-run or delete memory from test/fixture/dev sources |
| `POST /api/memory/cleanup/by-ids` | Dry-run or delete specific claim/entity IDs |
| `GET /api/memory/search` | Search memory claims/entities |
| `POST /api/memory/subgraph` | Fetch a bounded structured subgraph |
| `GET /api/memory/entities/{id}/bundle` | Entity bundle with nearby claims |
| `GET /api/memory/entities/{id}/graph` | Focused graph snapshot for an entity |
| `GET /api/memory/claims/{id}` | Claim bundle |
| `POST /api/memory/frontier/expand` | Expand memory frontier |
| `POST /api/memory/render-summary` | Render a human-readable summary |
| `GET /api/memory/query` | Convenience query returning structure plus summary |
| `GET /api/memory/graph` | Paginated global memory graph snapshot |

### Work, knowledge, and Dreaming

Work REST routes share tool contracts with MCP. Browser users sign in and agents authenticate with MCP tokens granted access to the corresponding tools.

| Route | Purpose |
|---|---|
| `POST /api/work`, `POST /api/work/find` | Create projects, features, items, or plans and list projects, features, or items |
| `GET /api/work/{id}`, `PATCH /api/work/{id}` | Inspect or update a work record by key or ID |
| `POST /api/work/next`, `POST /api/work/plans/sync` | Claim available work or preview/apply a replan |
| `POST /api/work/sessions`, `GET /api/work/sessions/{sessionId}/briefing` | Start a session and retrieve a briefing |
| `POST /api/work/messages`, `GET /api/work/inbox` | Post and retrieve messages |
| `POST /api/work/knowledge`, `POST /api/work/knowledge/recall` | Record or retrieve project knowledge |
| `GET /api/work/knowledge/review-queue` | Read pending approvals |
| `GET` or `POST /api/work/dreaming/{project}/runs` | List runs or start one |
| `GET /api/work/dreaming/{project}/runs/{runId}`, `POST /api/work/dreaming/{project}/runs/{runId}/revert` | Inspect a run or revert direct writes |
| `GET` or `PUT /api/work/dreaming/{project}/settings`, `/api/work/dreaming/{project}/instructions` | Read or edit Dreaming settings and instructions using their returned version or revision |

### Settings, operations, and wiki

| Route | Purpose |
|---|---|
| `POST /api/settings/processRepos` | Legacy route that queues durable processing for one or more repositories |
| `POST /api/settings/reIndexAll` | Legacy route that queues durable re-indexing for all known repos |
| `POST /api/settings/link` | Legacy route that queues durable cross-repo linking |
| `POST /api/settings/detectCommunities` | Legacy route that queues durable community detection |
| `POST /api/settings/linkAndDetect` | Legacy route that queues durable link-then-detect work |
| `POST /api/settings/processBatchAnalysis` | Legacy route that queues durable batch-analysis result processing |
| `POST /api/settings/discover` | Legacy route that queues durable repository discovery |
| `GET /api/settings/db-health` | MariaDB schema/index health diagnostics |
| `GET/POST/PUT/DELETE /api/settings/schedules...` | Embedded job scheduling |
| `GET/POST/PUT/DELETE /api/settings/sections...` | Wiki section management |
| `GET/POST/PUT/DELETE /api/settings/exclusions...` | Exclusion rule management |
| `POST /api/settings/mcp/regenerate` | Rebuild generated MCP documentation pages |
| `GET /api/wiki/...` | Wiki tree, page, revision, and attachment operations |
| `POST /api/ask` | Streaming Ask experience |
| `POST/GET /api/ask/runs...` | Persisted Ask runs, SSE replay, cancellation, and debug exchange inspection |
| `GET/POST/DELETE /api/user/mcp-tokens...` | User MCP personal access token list/create/revoke |
| `GET/POST/PATCH/DELETE /api/admin/agents...` | Admin-only agent accounts and their MCP tokens (see [Agent accounts](#agent-accounts)) |
| `GET/POST/DELETE /api/admin/admins...` | Standalone admin-user management backed by MariaDB |
| `GET /api/admin/jobs/queue` | Running, queued, and unresolved failed indexer, analysis, and scheduled work |
| `POST /api/admin/jobs/queue/{kind}/{id}/dismiss` | Dismiss a failed queue entry |
| `GET/PUT/DELETE /api/admin/prompts...` | Admin prompt catalog and prompt override management for analysis, review, and Ask assistant system prompts |
| `GET /api/admin/reports...` | Assistant, MCP, review, and repository-analysis usage reports |
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

- `/projects` lists work projects. Each project has **Overview**, **Work**, **Approvals**, **Knowledge**, **Lessons**, **Memory**, and **Dreaming** tabs. **Overview** links to **Code reviews**, which shows paged review history across the project's repositories and opens the exact run selected.
- `/work` shows current work, with feature boards, item details, and durable threads. The project **Work** tab shows a filterable feature table, live sessions, holds and waiters, and recent activity.
- `/knowledge`, `/lessons`, `/approvals`, and `/dreaming` also expose their own views.
- `/repos` and `/repos/:name` for repository browsing, health, security, vitality, and review workflows
- `/graph` for the repository dependency graph
- `/clusters` for service cluster visualization
- `/impact` for blast-radius analysis
- `/search` for global search
- `/ask` for streaming assistant interactions
- `/access-tokens` for user-managed MCP personal access tokens
- `/memory` for the memory browser and entity-focused graph exploration
- `/wiki/...` for the conventions/wiki system
- `/settings/...` for operations, schedules, DB health, sections, exclusions, admin users, agent accounts (`/settings/agents`), prompt overrides, database sources, reports, and assistant debug inspection

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

### Agent accounts

An administrator creates named agent identities and issues MCP tokens at **Settings → Agents** (`/settings/agents`). CodeGraph attributes writes made with an agent token to that agent. A session adds harness, model, and host to the attribution. A person's own token writes as that person.

- **Names** are 1 to 64 characters, `[a-z0-9][a-z0-9._-]*`, unique among agents, and never a reserved name (`anonymous`, `dreamer`, `human`) or a person CodeGraph has recorded (an admin, a person's token owner, a person's session or change-feed write).
- **Tokens** follow the personal-token model: shown once, 30, 60 or 90 days, at most 5 active per agent, all tools or a selection. Disabling an agent stops its tokens on their next call; re-enabling restores them. Deleting an agent revokes its tokens; its name may be used again.
- **Rename and delete** are refused while the agent has a live session, because a session is bound to its identity by name. Disabling is always allowed.
- An agent's token works on MCP and the token-accepting `api/work` endpoints only: never for the web UI, admin, user-token or agent endpoints, and never as an administrator.
- **Agents are never human input.** With an agent's token these are refused `forbidden` and change nothing:
  - a correction or lesson with `source.kind` `prompt` or `review`;
  - `promote` approve or reject, and `dream_review`;
  - `inbox` or `ack` without `session_id` (the humans' inbox), and directly answering a question put to the humans;
  - changing `dream_settings` (reading is allowed).

  When the owner explicitly answers or declines an open CodeGraph question in chat, the agent uses `get_work` to match the exact question and calls `post` with `in_reply_to` (question ID), `resolves` (`answered` or `declined`), `relayed_from_chat: true`, and the decision and its scope in `body`. Pass the session ID and use any returned item version for the next update. This records the reply under the agent's name and closes the question. If the question is already resolved, reuse that result.

  A reply to a question, from the web UI or from `post` with `in_reply_to`, reaches the inbox of the session that asked it, even when the reply also names an item. When the asking session has ended, the reply goes to the item's holder, or to the next session that claims the item if nobody has read it. `get_work` lists the item's newest questions and requests, open and closed, under `questions`; each closed one carries `resolved_by`, `resolved_at` and `answer`, the text of the reply that closed it. The web UI keeps answered and declined questions to the humans under **Answered** on the project and Now pages.

  To relay an owner's correction, use `relayed_prompt` or `relayed_review` and set `applies_when.activity`. Include the owner's exact words in `source.quote` when available. The relayed correction makes its lesson a rule immediately, including when it merges into an existing lesson.

  An agent also cannot revise, retract, supersede or merge away a lesson from a person's prompt or review, nor amend or retract it by resolving a challenge; it opens a challenge for the owner instead.

**Switching a harness to an agent token (owner steps).** Agents never enter or see token values.

1. Create the agents (for example `claude` and `codex`) at Settings → Agents, and issue a token for each.
2. Claude Code: replace the token in `~/.claude.json` (`mcpServers.codegraph.headers.Authorization`, a literal `Bearer ...` value) or point it at the variable with `"Authorization": "Bearer ${CLAUDE_CODEGRAPH_MCP_TOKEN}"`. Then set `CLAUDE_CODEGRAPH_MCP_TOKEN` in `~/.zshrc` and in launchd (`launchctl setenv CLAUDE_CODEGRAPH_MCP_TOKEN "$CLAUDE_CODEGRAPH_MCP_TOKEN"`). GUI apps and the hooks they start (`cg-hook`) read launchd, not the shell profile.
3. Codex: `~/.codex/config.toml` reads `bearer_token_env_var = "CODEX_CODEGRAPH_MCP_TOKEN"`; set that variable in `~/.zshrc` and launchd the same way.
4. Quit and reopen the apps and start new harness sessions. A session started under the owner's name is not usable with the agent's token.
5. Check: a `record` from the harness shows `claude (agent)` on the Knowledge page.

### Graph tools

- `get_graph_schema`
- `list_repositories`
- `search_graph`
- `get_service_summary`
- `trace_call_path`
- `trace_data_lineage`
- `find_consumers`
- `find_publishers`
- `get_architecture`
- `get_repository_health`
- `get_fleet_health`
- `find_archival_candidates`
- `get_service_clusters`
- `get_cluster_detail`
- `analyze_impact`
- `analyze_impact_file`
- `file_hotspot`
- `get_repo_dependencies`
- `read_node_source`
- `get_code_snippet`
- `list_conventions`
- `get_convention`

### Work and knowledge tools

| Tools | Purpose |
|---|---|
| `create_work`, `get_work`, `find_work`, `update_work`, `next_work`, `sync_plan`, `changes` | Work, planning, and the change feed |
| `start_session`, `get_briefing`, `end_session` | Session lifecycle, rules, and handoffs |
| `post`, `inbox`, `ack`, `acquire`, `renew_hold`, `release` | Messages and resource holds |
| `record`, `recall`, `import_knowledge`, `lessons`, `promote`, `challenge`, `review_queue` | Project knowledge and lessons |
| `dream_run`, `dream_runs`, `dream_settings`, `dream_instructions`, `dream_revert`, `dream_review` | Dreaming controls and proposal review |

For harness hooks, the `cg` CLI calls REST. Set `CODEGRAPH_URL` to the API base URL and `CODEGRAPH_TOKEN` to the CodeGraph-issued token. Use `cg brief`, `cg inbox`, `cg end`, or `cg hold renew` from an installed copy. The repository version can also run through the .NET SDK.

```bash
dotnet run --project tools/cg -- help
```

### Memory tools

- `memory_read`, `memory_store`, and `memory_diagnostics` for common operations
- `store_memory_v2`
- `get_memory_write_status`
- `query_memory`
- `search_memory`
- `get_memory_subgraph`
- `get_entity_bundle`
- `get_claim_bundle`
- `expand_memory_frontier`
- `render_memory_summary`

The API also exposes MCP resources and can regenerate wiki-backed MCP documentation from current tool metadata.

## Optional Git Hooks

Versioned git hooks live in [.githooks](.githooks), with an installer at [tools/install-git-hooks.sh](tools/install-git-hooks.sh).

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

The sidecar and web app have separate test commands. Install their dependencies as shown in [Run locally](#2-run-locally).

```bash
npm test --prefix tools/ts-extractor
npm test --prefix CodeGraphWeb -- --watch=false
```

The opt-in Dreaming provider test uses a persistent local MariaDB fixture and a real model. [Live acceptance procedure](docs/dreaming/live-acceptance.md)

## Job queue and retention

Admin Operations polls the job queue every five seconds. Failures persist until dismissed or a later successful matching operation resolves them. Scheduled retries preserve the prior error. The seeded `IndexerRunRetention` schedule runs Sundays at 04:00 UTC, pruning resolved indexer history older than 30 days in bounded batches; active runs and unresolved failures are retained. Its positive `retentionDays` argument is configurable under Admin Schedules. Deleting old runs also releases their durable submission keys after the retention window.
