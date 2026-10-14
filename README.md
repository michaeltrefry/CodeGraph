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
- Supports multiple AI backends for analysis: Anthropic, OpenAI, and LM Studio/OpenAI-compatible endpoints
- Computes repository health, hotspot metrics, security findings, .NET support posture, and repository vitality trends
- Runs project-level and repository-level AI code reviews with persisted findings and SSE streaming updates
- Stores claim-centric personal memory in MariaDB, including evidence, conflicts, bounded subgraphs, and frontier expansion
- Serves a web UI for repositories, graph views, reviews, impact analysis, wiki pages, schedules, and a dedicated memory browser
- Exposes graph and memory capabilities over MCP for assistants and other compatible clients

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
                         <- Indexer.Host
                         <- Memory.Host
                         <- Jobs
```

`CodeGraph.Api` hosts the REST API and MCP endpoint. `CodeGraph.Indexer.Host` owns indexing execution, indexer MassTransit consumers, schema sync, TypeScript sidecar warmup, durable run execution, and `/api/indexer` run/status endpoints. `CodeGraph.Indexer.Client` lets API and jobs delegate work to that host when `CodeGraph:Indexer:BaseUrl` is configured. `CodeGraph.Memory.Host` owns the memory REST surface and async memory-write consumer, while `CodeGraph.Memory.Client` lets API/MCP delegate memory operations when `CodeGraph:Memory:BaseUrl` is configured. `CodeGraph.Metrics` consumes LLM usage and MCP tool invocation events and persists telemetry into MariaDB. `CodeGraph.Jobs` runs scheduled and manual triggers. `CodeGraphWeb` is the Angular UI. MariaDB/MySQL is the primary datastore, RabbitMQ backs the event-driven pipeline, and Neo4j remains only as a temporary compatibility/export provider during the standalone rebase.

### C# repository tooling trust

C# indexing is syntax-only by default. Restore and MSBuild solution analysis can execute repository-controlled build logic, so they run only for provider-resolved identities explicitly listed in the comma-separated `CodeGraph:IndexingOptions:TrustedDotnetRepositories` setting. Identity forms are `github:https://host/owner/repo`, `gitlab:https://host/group/repo`, and `folder:relative/path`; local paths outside the configured folder root use `folder-path:/absolute/path`. Folder paths are case-sensitive on non-Windows hosts. User-supplied URL, group, and path combinations cannot grant trust unless they agree with the provider resolution. Every enabled or blocked solution-analysis decision emits a `SECURITY-AUDIT` log entry with the resolved identity. Production Compose exposes the setting as `CODEGRAPH_TRUSTED_DOTNET_REPOSITORIES` and defaults it to empty.

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

CodeGraph no longer assumes a single analysis backend. Runtime LLM configuration is managed from the admin Settings page under **LLM Configuration**.

- Supported built-in providers are `anthropic`, `openai`, and `lmstudio`
- LM Studio analysis targets OpenAI-compatible endpoints by default and is wired for tools like LM Studio

The LLM Configuration page owns:

- Provider tokens, endpoint overrides, API versions, and model lists
- Default Analysis provider/model and token/file/parallel caps
- Default Review provider/model and review caps
- Default Assistant provider/model, max tokens, and max turns

`AnalysisOptions` still exists as a local-development and transitional fallback. Production operators should save LLM values through the admin page instead of carrying provider/model/tuning values in environment variables. Startup warnings tagged `llm.config.deprecation` identify fallback values that have not yet been migrated into the database.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- MariaDB/MySQL 11.x-compatible server
- RabbitMQ
- [Node.js 18+](https://nodejs.org/)
- A C compiler and `curl` on macOS/Linux (Xcode command line tools or gcc): the build compiles the tree-sitter Swift grammar from pinned, hash-verified sources (`src/CodeGraph.Extractors.TreeSitter/native/build-tree-sitter-swift.sh`)
- A repository source configuration: `Folder`, `GitHub`, or `GitLab`
- At least one configured analysis provider if you want AI analysis or reviews

For Docker-based local model setups, the default LM Studio provider points at `http://host.docker.internal:1234/v1`.

For repository indexing, CodeGraph expects one or more top-level solution files (`.sln` or `.slnx`) when you want full Roslyn solution analysis. The Docker API image also includes compatibility SDKs for older repositories and `libssl1.1` for legacy .NET Core 2.1/global.json scenarios.

## Quick Start

### 1. Configure settings

The main local config file is [src/CodeGraph.Api/appsettings.json](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/appsettings.json). Docker-friendly defaults live in [.env.example](/Users/michael/Repos/CodeGraph/.env.example).

Useful settings to know:

| Setting | Purpose |
|---|---|
| `CodeGraph:StorageOptions:*` | MariaDB provider, migration, encryption-key, and embedding model settings |
| `CodeGraph:StorageOptions:MariaDbMigrationLockTimeoutSeconds` | Maximum wait for the database-scoped startup migration lock; defaults to 120 seconds |
| `CodeGraph:AuthOptions:*` | Standalone auth settings: local dev identity by default, optional OIDC/JWT authority/audience/client/scope/CORS settings |
| `CodeGraph:McpOptions:RequirePersonalAccessToken` | Require issued MCP PATs for `/mcp`; defaults off for local dev. When OAuth auth is enabled, `/mcp` is PAT-only even if this is not set. |
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

#### Push webhook

A GitHub `push` webhook refreshes a repository's default-branch index within seconds instead of waiting for the scheduled sync, which remains the fallback. Set `CodeGraph__RepositorySource__GitHub__WebhookSecret` (production: the `CODEGRAPH__REPOSITORYSOURCE__GITHUB__WEBHOOKSECRET` Actions secret), then add a webhook on the repository or organization:

- Payload URL: `https://<codegraph-api-host>/api/webhooks/github`
- Content type: `application/json`
- Secret: the same value
- Events: just the `push` event

A signed push to an indexed repository's default branch queues that repository's incremental sync (skipped when HEAD is already indexed) and answers `202`; repeated deliveries for the same head SHA queue at most one sync. Other branches, unindexed repositories and other events are acknowledged with `200` and ignored. A missing or invalid `X-Hub-Signature-256` answers `401`; with no secret configured every delivery is refused.

### 2. Run locally

```bash
dotnet build CodeGraph.sln

# API + MCP host
dotnet run --project src/CodeGraph.Api

# Indexer host
dotnet run --project src/CodeGraph.Indexer.Host

# Memory host
dotnet run --project src/CodeGraph.Memory.Host

# Metrics host
dotnet run --project src/CodeGraph.Metrics

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
- Indexer host: [http://localhost:5042](http://localhost:5042)
- Indexer sidecar health: [http://localhost:5042/health/sidecar](http://localhost:5042/health/sidecar)
- Memory host: [http://localhost:5039](http://localhost:5039)
- Memory write health: [http://localhost:5039/health/memory-write](http://localhost:5039/health/memory-write)
- Metrics host: [http://localhost:5041](http://localhost:5041)
- Web UI: [http://localhost:4200](http://localhost:4200)

### 3. Run with Docker Compose

```bash
cp .env.example .env
docker compose up --build
```

The compose stack serves the Angular UI over plain HTTP from the `codegraph-web` container and forwards API, Swagger, and MCP traffic to the internal `codegraph-api` container over the Docker network.
By default it binds only to `127.0.0.1:4280` so it does not take over shared host ports like `80` or `443`. Public TLS should be terminated by the host reverse proxy, such as Caddy.

The compose stack includes the CodeGraph application services:

- `codegraph-api`
- `codegraph-indexer`
- `codegraph-memory`
- `codegraph-metrics`
- `jobs`
- `codegraph-web`

MariaDB and RabbitMQ are expected to be shared containers on the external `trefry-network`; this compose file does not create them. The default container hostnames are `mariadb` and `rabbitmq`, and you can override `CodeGraph__StorageOptions__MariaDbConnectionString` or `CodeGraph__RabbitMqOptions__Host` when your shared services use different names.

API, indexer, memory, metrics, and jobs initialization is gated on the same MariaDB migration protocol. The first simultaneously starting host acquires a database-scoped advisory lock and applies pending, statement-journaled migrations; the remaining hosts wait, re-read migration history, and only then continue to readiness. If MariaDB is unavailable or the configured migration-lock timeout expires, the affected host fails startup rather than serving or running workers against a partial schema.

By default compose mounts the parent repo folder (`../`) into the containers at `/repos/workspace` as writable; remote providers clone and update repositories directly under that path. Override `CODEGRAPH_DOCKER_REPOS_MOUNT` when your host repositories should live somewhere else, or `CODEGRAPH_CONTAINER_REPOS_ROOT` if the in-container path must change.

Embeddings are expected under `/models` in containers. The default model path is `/models/embeddings/nomic-embed-text-v1.5/model.onnx`; the production deploy helper downloads the model and `vocab.txt` into the host model cache when they are missing.

Docker endpoints:

- Web UI: [http://localhost:4280](http://localhost:4280)
- API: [http://localhost:4280/api](http://localhost:4280/api)
- Swagger: [http://localhost:4280/swagger](http://localhost:4280/swagger)
- MCP: [http://localhost:4280/mcp](http://localhost:4280/mcp)
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

- `ci.yml` runs on pull requests and pushes to `main`. It restores, builds, and tests the .NET solution; builds and tests the Angular app; runs the browser shell smoke; and validates Docker image builds for API, jobs, indexer, memory, metrics, and web.
- `deploy.yml` runs only after `ci.yml` succeeds for a push to `main` (a failed or cancelled CI run skips it), deploying exactly the commit CI tested, and can be started manually. It publishes API, jobs, indexer, memory, metrics, and web images to GHCR, then deploys through SSH when the production environment variable `CODEGRAPH_DEPLOY_ENABLED=true`.

Deployment uses `docker-compose.yml` plus `deploy/docker-compose.production.yml` on the host. The deploy workflow generates the remote `.env` file from individual GitHub Actions production environment variables and secrets. Required settings are documented in [deploy/README.md](/Users/michael/Repos/CodeGraph/deploy/README.md).

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

`/impact`, `/impact/file`, `/nodes/by-file`, `/nodes/{id}`, `/nodes/{id}/source` and `/nodes/search` (and the MCP tools `analyze_impact`, `get_code_snippet`, `read_node_source`, `search_graph`, `trace_call_path`, `trace_data_lineage`, `find_consumers`, `find_publishers`, `codegraph_search`, `graph_source` and `graph_trace`) take an optional `sha` (full 40-hex commit). With it they answer from the ready branch overlay for that commit layered over the default-branch index, with the overlay's deleted files left out; a SHA without a ready overlay returns a typed `commit_not_indexed` result (HTTP 409) carrying the overlay status (`queued`, `indexing`, `failed`, `expired`, `stale`, or `unknown`). Every answer names the commit it describes: REST responses carry `commitSha`; MCP answers start with `Commit: <sha>` and carry `structuredContent.commitSha` (the consolidated `codegraph_search`, `graph_trace` and `graph_source` tools return a JSON envelope, so the commit is in `envelope.commitSha` and `structuredContent.commitSha`, and the `Commit:` line opens the envelope's `result`). An answer that spans several repositories also carries `commits` (repository → commit): with a SHA, the other repositories are answered from their default-branch index commit; without one, a multi-repository answer has a null `commitSha` and a `Commit: mixed (...)` line. Without a SHA, source reads return the file at the default-branch index commit, falling back to the working tree only for sources with no git history.

MCP callers (for example a personal access token holder, who cannot call the admin-only `POST /api/indexer/overlays`) request and poll overlays with two model-free tools. `request_overlay(repo, ref)` takes a branch name, tag, or full commit SHA, resolves it to its current commit (`git ls-remote` against the repository checkout, no fetch), and returns the live overlay for that repository and commit if one exists (queued, indexing, or ready against the current default-branch index commit, within the TTL; `reused: true`), otherwise queues a new overlay pinned to that commit. `get_overlay_status(overlayId)` reports `queued`, `indexing`, `ready`, `failed`, or `expired`, plus `stale` for a ready overlay whose base is no longer the default-branch index commit. Both answer `overlayId`, `status`, `headSha` and `baseSha` in `structuredContent` (with a `Commit: <headSha>` first line) and return typed errors (`repo_not_found`, `invalid_ref`, `overlay_not_found`, ...). Failed, expired, and stale overlays are never reused: requesting again queues a new one. Like every native tool they are available to tokens in `all` mode and to `selected` tokens that list them.

Two model-free tools give per-file risk readings in `structuredContent`. `analyze_impact_file(repo, path, sha?, depth=3)` is the MCP form of `/impact/file`: it answers `found`, `blastRadius` (the worst risk level among the affected nodes, `critical`|`high`|`medium`|`low`, or `none` when nothing depends on the file or it has no nodes), `criticalCount`, `highCount`, `mediumCount`, `lowCount`, `totalAffected`, `crossRepoCount`, `crossRepoImpacts` (one `{sourceRepo, targetRepo, edgeType, count}` per repository pair), `lowConfidenceCount` and `unmappedInboundEdges` (null without a SHA), with `commitSha` and `source` (`overlay` or `default-branch`); a SHA without a ready overlay returns the typed not-indexed result. `file_hotspot(repo, path)` reads the default-branch vitals for one file: `found`, `riskScore`, `concernScore`, `healthScore`, `churn365d`, `complexityScore`, `computedAt` and `commitSha`, the checkout commit the vitals run computed them at (`file_metrics.commit_sha`), plus `indexCommitSha`. Metrics computed before that column existed have a null `commitSha` and a null `concernScore` (it was not stored) until the next vitals run. Both take a repository-relative path (`..`, absolute and empty segments are rejected with `invalid_path`) and answer `repo_not_found` for an unknown repository.

Cargo dependencies link repositories at the crate level. A dependency reference is linked to the crate's definition in another indexed repository by exact package key (registry crates), by its git URL matching that repository's URL (https, ssh and scp-style spellings compare equal; `matched_by: git_url`), or, for a path dependency that leaves its repository, by crate name, preferring a repository named like a directory on the path (`matched_by: path`, medium confidence). A git dependency on a URL that is not indexed is never linked by name. For a `.rs` file, `analyze_impact_file` adds the repositories whose manifests depend on the file's crate, directly or through crates that depend on it within `depth` package hops, to `crossRepoImpacts` with `edgeType: REFERENCES_PACKAGE`; these are crate-level dependents, so they do not change `blastRadius` or the affected counts. They are read from the default-branch Cargo manifests even when a `sha` is given (manifests are not part of an overlay), and a file with no nodes at the commit (`found: false`) reports none. Git links need the upstream repository's stored URL to be its remote, so a repository indexed from a local folder source is not matched by git URL. Cargo manifests are re-read on every index but a routine sync skips a repository whose HEAD is unchanged, so on an existing fleet a git link appears after the next commit to either repository is indexed and a path link after the next commit to the downstream one (or after a manual reindex).

Swift package dependencies link the same way (`ecosystem: swiftpm`). A `.package(url:)` declared in `Package.swift`, or a source-control pin in a `Package.resolved` (root, Xcode project or workspace; pins hang off the repository node), whose URL is an indexed repository's remote links to the package defined by that repository's root `Package.swift` (`matched_by: git_url`, high confidence; `.git` suffixes, case, ports and https/ssh/scp spellings are normalised). A `.package(path:)` that leaves its repository links by package name like a Cargo path dependency (`matched_by: path`, medium confidence). A URL that matches no indexed repository stays an external package, and registry (`.package(id:)`) and local `file://` dependencies are not linked. For a `.swift` file, `analyze_impact_file` reports the repositories depending on the package of the nearest `Package.swift` above it, as it does for a Rust crate.

`get_repo_dependencies(repo)` is the model-free form of the dependency lists in `get_architecture` (which also returns a stored model summary): `inbound` and `outbound` list the other repositories as `{repo, edgeType, count, confidenceBand}` from the default-branch cross-repository links (`confidenceBand` is the weakest link of the group, or null when the links carry none). Its `rustSemantic` object says how the repository's Rust code was indexed: `status` is `semantic`, `partial`, `fallback` or `not_analyzed`, and `treeSitterGroups` lists every crate group that was indexed by tree-sitter instead of rust-analyzer as `{index, crates, outcome, failureCode, detailCode}`. Those crates have no symbol-level cross-repository edges, so a missing dependency on them is not evidence that none exists.

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

### Settings, operations, and wiki

CodeGraph has largely moved operational endpoints under `api/settings` rather than the older `api/admin` naming.

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
| `GET/POST/DELETE /api/admin/admins...` | Standalone admin-user management backed by MariaDB |
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
- `analyze_impact_file`
- `file_hotspot`
- `get_repo_dependencies`
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
