# Standalone CodeGraph Rebase Inventory

Date: 2026-04-26
Kanban project: `a81a9c98-d82d-4f4d-a008-8c7327e6f72f`
Kanban epic: `9811f675-79c4-4de8-add1-d486f467c54d`

## Decision

The standalone rebase targets `.NET 10`.

The rebase will proceed in-place in `/Users/michael/Repos/CodeGraph`: keep CodeGraph as the standalone home and import donor surfaces from `/Users/michael/Repos/TC.CodeGraphApi` into feature branches/slices.

Implications:

- Current `CodeGraph` projects already target `net10.0`; keep that as the baseline.
- `TC.CodeGraphApi` donor projects currently target `net9.0`; any donor project or file port must be upgraded during import.
- Prefer existing standalone `Microsoft.Extensions.*` package versions where possible. Current CodeGraph uses `10.0.3`; donor uses mostly `10.0.5` extension packages in `net9.0` projects.
- Upgrade ASP.NET and EF-related donor packages to `.NET 10` compatible versions as part of each port slice. Notable donor versions to revisit include `Microsoft.AspNetCore.Mvc.Testing 9.0.0`, `Microsoft.EntityFrameworkCore.* 9.0.0`, and `Pomelo.EntityFrameworkCore.MySql 9.0.0`.
- Angular is already aligned enough for the rebase: both frontends use Angular `21.2.x`, TypeScript `~5.9.2`, and npm `11.6.2`.

## Frozen Sources

| Repo | Path | Branch | SHA | Working tree |
| --- | --- | --- | --- | --- |
| CodeGraph | `/Users/michael/Repos/CodeGraph` | `main` at inventory start | `8f4770e821d30144919dff980153bf99fdddbd17` | Clean before branch creation |
| TC.CodeGraphApi | `/Users/michael/Repos/TC.CodeGraphApi` | `main` | `ccd8d9aa5de63a324177491c585f8f020ca19c78` | Clean |

Inventory work is being recorded on CodeGraph branch `codex/standalone-rebase-inventory`.

## Bootstrap Status

- Branch: `codex/standalone-rebase-inventory`
- Repo strategy: import donor surfaces into the existing CodeGraph repository.
- README note: added to `README.md` to clarify that the first phase is dependency removal, donor import, and mechanical rename rather than behavior change.
- First donor asset import: SQL migrations copied into `sql/migrations` with provenance recorded in `sql/migrations/README.md`.
- First MariaDB implementation slice: `CodeGraph.Data.MariaDb` added to the solution with a `net10.0` migration runner that can create the configured database, create `migration_history`, apply sorted SQL scripts, and record applied scripts.
- Migration validation slice: `MariaDbMigrationRunner` now handles semicolons inside SQL literals/comments and has repeatable opt-in MariaDB integration coverage. A disposable MariaDB 11.4 validation applied all 43 imported migrations twice and confirmed `migration_history` idempotency.
- Provider scaffold slice: `CodeGraph.Data.MariaDb` now includes an EF Core `CodeGraphDbContext` mapped to the donor MariaDB table/column names for the existing standalone `CodeGraph.Data` entities. EF-backed `MySqlWikiStore`, `MySqlExclusionStore`, `MySqlMetricsStore`, `MySqlAnalysisStore`, `MySqlReviewStore`, `MySqlJobScheduleStore`, `MySqlAdminStore`, `MySqlDatabaseSourceStore`, and `MySqlIndexerRunStore` are present with focused opt-in MariaDB coverage. `MySqlDbHealthStore` provides MariaDB schema/index health for settings/admin surfaces. `MySqlGraphStore` now covers the current standalone `IGraphStore` contract through core repository/node/edge/cross-repo/sync/file-hash/cluster operations plus delegation to the analysis, metrics, review, and migration stores. `MySqlVectorStore` covers the current standalone `IVectorStore` contract using a standalone embeddings table. `MySqlMemoryGraphStore` covers the current standalone `IMemoryGraphStore` contract over the donor memory schema plus standalone external-id compatibility migration `048_standalone_memory_external_ids.sql`. `AddCodeGraphMariaDbData` registers the current standalone provider contracts behind one DI extension. Split-host memory/indexer surfaces remain for later feature-parity cards rather than blocking the current runtime swap.
- Runtime persistence swap slice: API and jobs now register MariaDB stores when `CodeGraph:StorageOptions:Provider` is `MariaDb`/`MySql`, with Neo4j preserved only as an explicit compatibility fallback. API initialization resolves SQL migrations from `MariaDbMigrationsPath`; API/jobs appsettings and docker-compose now point at the MariaDB provider and app-user connection string.
- Assistant/MCP persistence slice: standalone data contracts and MariaDB stores now cover donor assistant run/chat/event/debug/audit persistence (`IAssistantRunStore`/`MySqlAssistantRunStore`), MCP personal access token persistence (`IMcpPersonalAccessTokenStore`/`MySqlMcpPersonalAccessTokenStore`), and event telemetry for LLM usage plus MCP tool invocations (`IMetricsEventStore`/`MySqlMetricsEventStore`). These map to imported donor migrations `027`, `031`, `034` through `039`, `042`, and `043`; they are registered by `AddCodeGraphMariaDbData` without importing TC host/auth/message bus dependencies.
- Assistant/MCP service integration slice: standalone API/services now expose persisted assistant run creation, run lookup, event replay, SSE run streaming, cancellation request handling, chat listing/transcripts, MCP personal access token list/create/revoke, and an optional `McpPat` authentication handler/policy for the MCP endpoint. The legacy `POST /api/ask` direct SSE path remains for compatibility, while new persisted endpoints live under `/api/ask/runs` and `/api/ask/chats`. MCP PAT enforcement is controlled by `CodeGraph:McpOptions:RequirePersonalAccessToken` so local dev is not locked out when `MariaDbEncryptionKey` is empty.
- Assistant/admin reporting integration slice: standalone services now include `IMetricsEventPublisher` backed directly by `IMetricsEventStore` instead of TC queues, `GET /api/ask/options` for assistant/indexing provider defaults, and `GET /api/admin/reports/*` endpoints for assistant usage, assistant activity, MCP usage, code-review usage, repository-analysis usage, and report filters. `IAdminReportsStore` keeps report queries behind the data abstraction and `MySqlAdminReportsStore` reads MariaDB `llm_usage`, `assistant_runs`, and `mcp_tool_invocations`.
- Assistant/MCP execution telemetry slice: `GraphAssistant` now records internal graph-tool duration/success telemetry through `IMetricsEventPublisher`; OpenAI-compatible assistant providers record `usage` token counts into `llm_usage` when the provider response includes them; and Anthropic streaming assistant turns capture `message_delta.usage` with cache-created/cache-read tokens folded into input-token totals. The API also adds `/mcp` JSON-RPC telemetry middleware that records external MCP `tools/call` invocations with username and PAT token id claims when available, while leaving the request body readable for the MCP endpoint.
- Assistant retention cleanup slice: standalone jobs now include an `AssistantRetentionCleanup` job type backed by `IAssistantRetentionCleanupService` and `IAssistantRunStore.CleanupAssistantRetentionAsync`. The MariaDB store finalizes stale queued/running runs, prunes old terminal runs, run events, chat messages, debug exchanges, and debug trace audit rows using `CodeGraph:AssistantRetentionOptions:*` cutoffs without TC.JobUtilities.
- Assistant resume/debug capture slice: persisted assistant runs now write execution-state checkpoints as stream events are saved, append a terminal `completed` event when runs finish, and capture per-provider debug exchanges from `GraphAssistant` into `assistant_debug_exchanges` while running under an assistant-run context. `GET /api/ask/runs/{runId}/debug-exchanges` returns the captured exchange summaries for the run owner and writes a `assistant_debug_trace_audit` row for operator traceability.
- Standalone auth slice: API startup now binds `CodeGraph:AuthOptions`, defaults to a local-dev identity when auth is disabled, supports optional JWT/OIDC bearer authentication for standalone deployments such as Keycloak, and registers `CodeGraphUser`/`CodeGraphAdmin` policies. Admin authorization can use role/admin claims or the MariaDB `IAdminStore`; MCP PAT auth remains a separate `/mcp` policy controlled by `CodeGraph:McpOptions:RequirePersonalAccessToken`. The API now also exposes frontend bootstrap endpoints under `/api/auth`: anonymous `GET /api/auth/config` for public OIDC client settings and authenticated `GET /api/auth/me` for normalized username/admin status.
- Admin/auth management slice: standalone `GET/POST/DELETE /api/admin/admins` endpoints now manage admin usernames through `IAdminStore` without TC permission-gateway services. These endpoints use the same `CodeGraphAdmin` policy as settings and reports.
- Prompt/database-source admin slice: standalone `GET/PUT/DELETE /api/admin/prompts` endpoints now expose the admin prompt catalog and persist prompt overrides through `IAdminStore` without TC prompt services. Stored prompt overrides now flow into repository analysis, project/repository review system prompts, and the graph-backed Ask assistant. Standalone `GET/POST/PUT/DELETE /api/database-sources` endpoints now expose the already-ported `IDatabaseSourceStore`, mask passwords in API responses, and include `POST /api/database-sources/generate-key` for MariaDB encryption-key setup.
- Expanded extractor coverage slice: donor Ansible, Terraform/HCL, and ColdFusion extractors have been imported as first-class `CodeGraph.Extractors.*` projects targeting `net10.0`, with the required IaC/schema node labels and edge types added to `CodeGraph.Models`. The API registers them as `ICodeExtractor` implementations alongside C#, SQL, TypeScript, and Tree-sitter, and donor extractor tests now run under `CodeGraph.Tests`.
- Angular admin settings slice: the current Angular shell now exposes `/settings/admins`, `/settings/prompts`, and `/settings/database-sources` over the standalone backend APIs. The slice also restores the Angular Vitest unit-test target for focused admin component/helper coverage. Validation on 2026-04-26: `npm test -- --watch=false --include 'src/app/pages/admin/*.spec.ts'` passed with 12 tests, and `npm run build` passed with only the pre-existing `repo-detail.component.scss` style-budget warning.
- Angular assistant/MCP feature-parity slice: the current Angular shell now exposes `/access-tokens` for user MCP PAT list/create/revoke, `/settings/reports` for assistant/MCP/review/repository-analysis usage reports, and `/settings/assistant-debug` for inspecting persisted assistant debug exchanges by run id. Focused Vitest coverage was added for the new components. Initial validation on 2026-04-26: `npm test -- --watch=false --include 'src/app/pages/access-tokens/*.spec.ts' --include 'src/app/pages/admin/admin-reports.component.spec.ts' --include 'src/app/pages/admin/assistant-debug.component.spec.ts'` passed with 8 tests.
- Schema catalog parity slice: standalone API now exposes `GET /api/schemas` and `GET /api/schemas/{name}/catalog` over the existing graph store, recognizing `db:` projects and schema metadata properties without requiring the donor split indexer yet. The Angular shell now has `/schemas`, `/schemas/:name`, and `/schemas/:name/nodes` routes, a schema list page, a schema catalog detail view, top-nav entry, and schema-aware node breadcrumbs. Validation on 2026-04-26: `dotnet test src/CodeGraph.Tests/CodeGraph.Tests.csproj --filter "ProjectQueryServiceTests" --no-restore` passed with 3 tests, and `npm run build` passed with only the pre-existing `repo-detail.component.scss` style-budget warning.
- Standalone indexer boundary slice: added `IIndexerOperationsService`/`StandaloneIndexerOperationsService` plus durable `IndexerAcceptedResponse` and `IndexerRunResponse` contracts. `POST /api/database-sources/{id}/sync` and `POST /api/database-sources/sync-all` now create queued `indexer_runs` rows through `IIndexerRunStore`, and `GET /api/indexer/runs/{runId}` exposes run status. The Angular database-source settings page now has per-source and all-source sync buttons and displays the last queued run id.
- Executable schema-sync slice: added an integrated `IndexerRunExecutor`/`IndexerRunBackgroundRunner` plus standalone `IDatabaseSchemaExtractor`/`DatabaseSchemaExtractor`. Queued schema-sync runs now start in-process after durable run creation, mark runs running/completed/failed, enumerate enabled database sources, ingest MariaDB schema objects into `db:{server}:{database}` graph projects, and refresh database-source `LastSyncedAt`. This keeps the standalone runtime useful before deciding whether to restore a separate remote indexer host.
- Durable indexer operations slice: `indexer_runs` now carries optional `args_json`, and `/api/indexer` can queue repository processing, re-index-all, discovery, link, detect-communities, link-and-detect, batch-analysis processing, schema sync, and run-list/status operations through the same integrated background runner. The Angular admin operations surface now targets the durable indexer endpoints instead of directly invoking legacy `/api/settings` operations.
- Neo4j-to-MariaDB migration tooling slice: added `Neo4jToMariaDbMigrationManifest.Current` as executable scaffolding for the export/import workload order: repositories, graph, wiki, analysis, reviews, metrics, vectors, memory, assistant, and jobs. `Neo4jToMariaDbMigrationPlanner.CreateDryRunReport` now turns that manifest into a typed dry-run report with ordered planned steps and explicit exporter/importer-pending notes before source-specific exporters/importers are implemented.
- Neo4j-to-MariaDB migration API slice: this previously exposed admin HTTP routes for dry-run planning and repositories/graph import execution.
- Neo4j-to-MariaDB repositories/graph execution slice: `CodeGraph.Data.Neo4j` exposes a graph migration exporter for repository metadata, `CodeNode` nodes, graph relationships, and `CrossRepoEdge` records, with importer scaffolding that can map those records into the active MariaDB `IGraphStore`.
- Migration endpoint removal slice: on 2026-04-27 the standalone cutover decision changed to fresh MariaDB data. The Neo4j-to-MariaDB API controller, memory migration HTTP endpoints, and memory migration MCP tools were removed from the public API/tool surface; the underlying compatibility/export scaffolding remains only as non-public reference code during the rebase.
- Memory decoupling slice: updated MCP tool descriptions away from Neo4j-specific wording. The standalone direction remains integrated-first over `IMemoryGraphStore`/`MySqlMemoryGraphStore` and existing `MemoryService` contracts, with split memory host/client work deferred until remote-host value is clear.
- Angular browser validation slice: the donor-style Vitest browser target has been restored as `npm run test:browser` using Playwright/Chromium. A standalone shell browser smoke spec now verifies that the top navigation renders the expected route set without creating page-level horizontal overflow, and the shell CSS wraps the nav/search layout cleanly at narrow browser widths. Validation on 2026-04-26: `npm run test:browser` passed with 1 browser test; `npm test -- --watch=false` passed with 20 jsdom tests; `npm run build` passed with only the pre-existing `repo-detail.component.scss` style-budget warning.
- Local dev compose usability slice: `docker-compose.yml` now creates a named MCP network by default instead of requiring a pre-existing external `mcp-shared` network, and API/jobs containers mount the host repo root from `CODEGRAPH_DOCKER_REPOS_MOUNT` into `/repos` while keeping `/repos/.cache` writable through the existing named volume. `.env.example` documents `CODEGRAPH_DOCKER_REPOS_MOUNT` and `CODEGRAPH_DOCKER_MCP_NETWORK`. Validation on 2026-04-26: `docker compose config --quiet` passed.
- Agent guidance refresh slice: repo-level `AGENTS.md` and `CLAUDE.md` now describe the current `.NET 10`, MariaDB-primary standalone architecture, expanded extractor projects, SQL migration path, and Neo4j compatibility/export boundary instead of the older `.NET 9`/Neo4j-primary shape.
- Host shared foundation slice: `CodeGraph.Host.Shared` is now a `net10.0` project in the solution with standalone internal service auth options, a CodeGraph-owned internal identity header, HMAC token factory/validator, host descriptor registration, health-check registration, and shared activity sources. This replaces the donor Host.Shared/service-stack concept with a small standard-DI foundation that future API, Indexer.Host, Memory.Host, Metrics, and Jobs ports can consume. Validation on 2026-04-27: focused `InternalServiceTokenTests` passed with 3 tests and `dotnet build CodeGraph.sln --no-restore` passed with 0 warnings/errors.
- Indexer client split slice: `CodeGraph.Indexer.Client` is now a `net10.0` project in the solution. It provides `IIndexerClient`, `HttpIndexerClient`, client options, DI registration, typed error handling, and internal service-token header attachment through `CodeGraph.Host.Shared`. The client targets the current durable `/api/indexer` operation surface: process repositories, reindex all, discover, schema sync, link, community detection, link-and-detect, batch-analysis processing, run lookup, and run listing. Validation on 2026-04-27: focused `HttpIndexerClientTests` passed with 4 tests and `dotnet build CodeGraph.sln --no-restore` passed with 0 warnings/errors. Remaining indexer split work is to create the deployable `CodeGraph.Indexer.Host`, move execution/consumers there, and update API/jobs delegation to use the client.
- Indexer host split slice: `CodeGraph.Indexer.Host` is now a deployable `net10.0` web host on port `5042`. It owns the durable `/api/indexer` operation surface, `IndexerRunExecutor`/background runner, database schema extraction, TypeScript sidecar warmup and `/health/sidecar`, extractor registrations, RabbitMQ indexer consumers, and MariaDB migration/exclusion initialization. API can switch from integrated indexer execution to `RemoteIndexerOperationsService` when `CodeGraph:Indexer:BaseUrl` is configured, and jobs now trigger discovery, reindex, link/detect, and batch-analysis work through `CodeGraph.Indexer.Client`. Validation on 2026-04-27: focused indexer host/API/client tests passed with 7 tests and `dotnet build CodeGraph.sln --no-restore` passed with 0 warnings/errors. Remaining split-host work is local/dev Docker stack wiring plus later memory/metrics host parity.
- Local/dev indexer stack slice: `Dockerfile.indexer` now builds/publishes `CodeGraph.Indexer.Host`; API and jobs Dockerfiles copy the new host-shared/indexer-client project files for restore; compose now runs `codegraph-indexer` as a distinct service on port `5042`, wires API/jobs to `http://codegraph-indexer:5042`, and keeps MariaDB/RabbitMQ external on `trefry-network`. CI/CD build and publish the indexer image alongside API/jobs/web, and production compose overrides the indexer image plus internal-service-auth settings. Validation on 2026-04-27: `docker compose config --quiet`, production compose config, workflow YAML parsing, build-stage Docker builds for API/jobs/indexer, and `dotnet build CodeGraph.sln --no-restore` all passed. Remaining local/dev stack scope waits on `CodeGraph.Metrics` existing as a deployable host.
- Memory client/host foundation slice: `CodeGraph.Memory.Client` and `CodeGraph.Memory.Host` are now `net10.0` projects in the solution. The client provides `IMemoryClient`, `HttpMemoryClient`, options, typed error handling, transient read retry behavior, and CodeGraph internal-service-token header attachment. The host runs on port `5039`, uses `CodeGraph.Host.Shared` internal auth, exposes the current claim-centric `/api/memory` routes plus `/health/memory-write`, registers existing `CodeGraph.Services.Memory` services over `IMemoryGraphStore`, and owns the `store-memory-claims` MassTransit consumer. API/MCP memory calls now go through `IMemoryOperationsService`, which uses `CodeGraph.Memory.Client` when `CodeGraph:Memory:BaseUrl` is configured and falls back to the local `MemoryService`/message-bus path when it is empty. The memory API/client/host now also expose donor-style `GET /api/memory/writes/diagnostics`, `GET /api/memory/diagnostics`, and cleanup routes for source/test-data/explicit IDs. The MariaDB cleanup path supports dry runs and rebuilds affected active-claim, claim-edge, observation, entity-edge, adjacency, seed-alias, write-receipt, and orphan-entity projections after deletes. Validation on 2026-04-27: focused `MemoryClient|MemoryControllerCleanup|MariaDbMemoryGraphStore` tests passed with 14 tests, the backend `CodeGraph.Tests` project passed 523 tests with `--no-build`, and `dotnet build CodeGraph.sln --no-restore` passed with 0 warnings/errors. The first standalone cutover keeps memory contracts in `CodeGraph.Models`, persistence contracts/implementations in `CodeGraph.Data` and `CodeGraph.Data.MariaDb`, and memory services in `CodeGraph.Services` while preserving the deployable `CodeGraph.Memory.Host` and typed `CodeGraph.Memory.Client` boundary.
- Local/dev memory stack slice: `Dockerfile.memory` now builds/publishes `CodeGraph.Memory.Host`; compose now runs `codegraph-memory` as a distinct service on port `5039`, wires API to `http://codegraph-memory:5039` through `CodeGraph.Memory.Client`, and leaves MariaDB/RabbitMQ external on `trefry-network`. CI/CD build and publish the memory image alongside API/jobs/indexer/web, and production compose overrides the memory image plus internal-service-auth settings. Validation on 2026-04-27: `docker compose config --quiet`, production compose config, workflow YAML parsing, and build-stage Docker builds for memory/API passed.
- Metrics host foundation slice: `CodeGraph.Metrics` is now a deployable `net10.0` host on port `5041`. API/services publish normalized `LlmUsageRecorded` and `McpToolInvocationRecorded` events through MassTransit instead of writing telemetry directly, while the metrics host consumes those events and persists them through `IMetricsEventRecorder` and the existing MariaDB `IMetricsEventStore` with idempotent `event_id` handling. The split-host stack now includes `Dockerfile.metrics`, a `codegraph-metrics` compose service on `trefry-network`, GHCR CI/CD image wiring, and docs/env defaults. Validation on 2026-04-27: focused metrics tests, compose config, production compose config, workflow YAML parsing, Docker build-stage checks, and solution build were run for this slice.

## Solution Shape

Current CodeGraph solution has 21 projects:

- `CodeGraph.Api`
- `CodeGraph.Data`
- `CodeGraph.Data.MariaDb`
- `CodeGraph.Data.Neo4j`
- `CodeGraph.Extractors.Ansible`
- `CodeGraph.Extractors.ColdFusion`
- `CodeGraph.Extractors.CSharp`
- `CodeGraph.Extractors.Sql`
- `CodeGraph.Extractors.Terraform`
- `CodeGraph.Extractors.TreeSitter`
- `CodeGraph.Extractors.TypeScript`
- `CodeGraph.Host.Shared`
- `CodeGraph.Indexer.Client`
- `CodeGraph.Indexer.Host`
- `CodeGraph.Jobs`
- `CodeGraph.Jobs.Tests`
- `CodeGraph.Memory.Client`
- `CodeGraph.Memory.Host`
- `CodeGraph.Models`
- `CodeGraph.Services`
- `CodeGraph.Tests`

TC donor solution has 27 projects:

- Main API/services/models/tests: `TC.CodeGraphApi`, `TC.CodeGraphApi.Services`, `TC.CodeGraphApi.Models`, `TC.CodeGraphApi.Tests`
- MariaDB data: `TC.CodeGraphApi.Data`
- Extra extractors: `TC.CodeGraphApi.Extractors.Ansible`, `TC.CodeGraphApi.Extractors.ColdFusion`, `TC.CodeGraphApi.Extractors.Terraform`
- Shared extractors also present: `TC.CodeGraphApi.Extractors.CSharp`, `TC.CodeGraphApi.Extractors.Sql`, `TC.CodeGraphApi.Extractors.TypeScript`
- Split hosts/clients: `TC.CodeGraphApi.Indexer.Host`, `TC.CodeGraphApi.Indexer.Client`, `TC.CodeGraphApi.Memory.Host`, `TC.CodeGraphApi.Memory.Client`
- Memory split libraries: `TC.CodeGraphApi.Memory`, `TC.CodeGraphApi.Memory.Data`, `TC.CodeGraphApi.Memory.Services`
- Metrics host: `TC.CodeGraphApi.Metrics`
- Host shared infrastructure: `TC.CodeGraphApi.Host.Shared`
- Jobs: `TC.CodeGraphJobs`
- Console utility: `TC.CodeGraphApi.Console`
- Associated tests for indexer, memory, metrics, and jobs

First import rule: rename and port donor surfaces into the existing `CodeGraph.*` naming scheme, but keep each slice buildable on `net10.0`.

## Backend API Surface

Current standalone API controllers:

- `AskController`
- `ClustersController`
- `GraphController`
- `HealthController`
- `MemoryController`
- `NodesController`
- `ProjectReviewsController`
- `ProjectsController`
- `RepositoryReviewsController`
- `SearchController`
- `SettingsController`
- `WikiController`

Donor main API adds or expands:

- `AdminController`
- `AdminReportsController`
- `AuthConfigController`
- `DatabaseSourcesController` (CRUD, key generation, and queued schema-sync trigger endpoints now ported)
- `SchemasController`
- `UserMcpTokensController`
- Expanded `AskController` with run creation, stream tokens, run events, chat history, cancellation, and options
- Expanded `MemoryController` with diagnostics and cleanup endpoints
- Expanded admin operations for settings, prompts, users, assistant debug, indexer status/runs, MCP regeneration, exclusions, sections, and batch operations. Admin user management and prompt override CRUD are now ported under `/api/admin`.

Donor split hosts add additional controller surfaces:

- Indexer host: `api/indexer`, repository indexing, reanalysis, discovery, linking, community detection, schema sync, result processing, run lookup, and status
- Memory host: `api/memory`, write health, diagnostics, cleanup, graph, claim/entity bundles
- Jobs host: job-trigger endpoints for repository discovery, repository processing, batch result processing, link/detect, schema sync, and assistant retention cleanup
- Metrics host: health check surface backed by MariaDB context

Porting implication: the 2026-04-27 architecture correction resolves the earlier split/collapse question. These boundaries should be restored as standalone deployable hosts and clients, with API/jobs delegating to them. The integrated API/services implementations already ported in this branch are reusable scaffolding, but they are not the final runtime shape for indexer, memory, or metrics.

## Persistence And Migrations

Current CodeGraph persistence:

- Abstraction project: `CodeGraph.Data`
- Runtime provider: `CodeGraph.Data.Neo4j`
- Migrations: 12 Cypher migrations under `src/CodeGraph.Api/Migrations`
- Current migration coverage includes schema, wiki sections, analysis batch repair, repository metadata label, memory graph repairs, job schedules, project/repository reviews, and memory claim graph v2.

TC donor persistence:

- MariaDB/MySQL provider project: `TC.CodeGraphApi.Data`
- Memory persistence project: `TC.CodeGraphApi.Memory.Data`
- Uses `Dapper`, `MySqlConnector`, `Pomelo.EntityFrameworkCore.MySql`, and `CodeGraphDbContext`
- Store implementations include `MySqlGraphStore`, `MySqlWikiStore`, `MySqlAdminStore`, `MySqlDatabaseSourceStore`, assistant runs, indexer runs, MCP telemetry, metrics, reviews, security, exclusions, migrations, nodes, and edges
- SQL migrations `001` through `043` under `sql/migrations`

Standalone import status:

- Donor SQL migrations `001` through `043` have been copied into `/Users/michael/Repos/CodeGraph/sql/migrations`.
- Standalone migration `044_standalone_analysis_batch_fields.sql` extends the donor schema with current CodeGraph analysis batch/request fields and aligns file metric numeric column types with the standalone entity model.
- Standalone migration `045_standalone_project_reviews.sql` adds current CodeGraph project review run/finding tables missing from the donor MariaDB schema.
- Standalone migration `047_standalone_embeddings.sql` adds a generic embeddings table for the current standalone `IVectorStore` contract.
- Standalone migration `048_standalone_memory_external_ids.sql` adds stable external IDs for current standalone memory claims/evidence/observations and receipt fields needed by `IMemoryGraphStore`.
- `CodeGraph.Data.MariaDb` contains the initial standalone `MariaDbMigrationRunner`.
- The migration runner has been locally validated against MariaDB 11.4 with all imported and standalone scripts and an idempotent second run.
- `CodeGraph.Data.MariaDb` now contains an initial `CodeGraphDbContext` mapping for existing standalone entities against the donor MariaDB schema.
- `CodeGraph.Data.MariaDb` has focused EF-backed store implementations for wiki pages/sections, exclusion rules, file metrics, project health, security summary/finding surfaces, repository/project analysis, analysis batches/requests, node analysis, graph-context reads used by batch prompt building, project diagnostics, project review runs/findings, repository review runs/findings/sections, standalone job schedules, admin users/settings/prompt overrides, database sources with encrypted connection strings, and indexer run status tracking.
- `MySqlGraphStore` has been ported/adapted for current standalone graph operations: repository upsert/search, node and edge batch writes, traversal, cross-repo edges, file hashes, sync state, clusters, and project cleanup helpers. It delegates inherited analysis/metrics/review/migration members to the smaller provider stores rather than duplicating them.
- `MySqlVectorStore` has been added for standalone vector storage and similarity search using JSON-serialized embeddings and cosine similarity.
- `MySqlMemoryGraphStore` has been added for the current standalone memory contract, including write receipts, entity/claim/evidence/observation writes, text/vector claim/entity search, bundles, graph snapshots, and subgraph reads.
- `AddCodeGraphMariaDbData` now registers the provider's standalone store contracts for `IGraphStore`, `IWikiStore`, `IExclusionStore`, `IJobScheduleStore`, `IDbHealthStore`, `IAdminStore`, `IDatabaseSourceStore`, `IIndexerRunStore`, `IVectorStore`, `IMemoryGraphStore`, and companion analysis/metrics/review/migration stores.
- Validation on 2026-04-26: `dotnet test src/CodeGraph.Tests/CodeGraph.Tests.csproj --filter "MariaDb"` passed without integration env; app-user MariaDB integration on localhost:3306 passed migration-runner, memory-store, vector-store, and service-registration tests against all migrations through `048_standalone_memory_external_ids.sql`; `dotnet build CodeGraph.sln --no-restore` passed.
- `IAssistantRunStore`, `IMcpPersonalAccessTokenStore`, and `IMetricsEventStore` have been added as standalone contracts for donor assistant/MCP persistence surfaces. `CodeGraphDbContext` now maps `assistant_runs`, `assistant_chat_messages`, `assistant_run_events`, `assistant_debug_exchanges`, `assistant_debug_trace_audit`, `mcp_personal_access_tokens`, `mcp_tool_invocations`, and `llm_usage`.
- `MySqlAssistantRunStore`, `MySqlMcpPersonalAccessTokenStore`, and `MySqlMetricsEventStore` have been added and registered in `AddCodeGraphMariaDbData`. Validation on 2026-04-26: `dotnet test src/CodeGraph.Tests/CodeGraph.Tests.csproj --filter "MariaDb" --no-restore` passed with 34 tests; `CODEGRAPH_MARIADB_TEST_CONNECTION='Server=localhost;Port=3306;Database=codegraph;User ID=codegraph;Password=codegraph_test!;' dotnet test src/CodeGraph.Tests/CodeGraph.Tests.csproj --filter "MariaDb" --no-restore` passed against app-user MariaDB, applying all migrations and round-tripping the MariaDB provider suite including assistant runs, chat messages, debug exchanges, debug audit rows, MCP tokens, LLM usage, and MCP tool invocations. Temporary integration-test database names now use the `codegraph_%` prefix expected by the app-user grant.
- Standalone migration `046_standalone_job_schedules.sql` adds the MariaDB job schedule table required by the current `CodeGraph.Jobs` scheduler.
- `MySqlDbHealthStore` covers MariaDB schema-health reporting by checking expected table constraints and indexes plus duplicate data groups that should not exist after migration.
- Runtime MariaDB migration execution is enabled through API startup when the MariaDB provider is selected.
- Remaining donor-only feature-parity work includes deeper run recovery/lease resumption behavior, richer report/drilldown polish beyond the initial Angular surfaces, and split-host memory/indexer surfaces.

## Neo4j Compatibility Boundary

Decision on 2026-04-26: keep `CodeGraph.Data.Neo4j` in the repository during the standalone rebase as a compatibility/export source and migration reference, but do not keep Neo4j as an equal first-class runtime backend for the first MariaDB release.

Runtime direction:

- `CodeGraph.Data.MariaDb` is the target runtime provider.
- `CodeGraph.Data.Neo4j` should remain available until the Neo4j-to-MariaDB migration/export tooling can cover graph nodes/edges, cross-repo edges, wiki/conventions, analysis batches/results, reviews, metrics/health/security, memory entities/claims/evidence/observations, vectors where retained, sync state, and job schedules.
- API/jobs DI should swap to MariaDB once the MariaDB provider has enough surface for the current runtime contracts. Neo4j registrations should then be isolated behind migration/export tooling or removed from runtime startup.
- Any future dual-backend support should be an explicit later decision, not an accidental consequence of keeping old registrations alive.

Donor SQL migration range:

- `001_initial_schema.sql` through `017_database_sources.sql`: base graph, file metrics, health, conventions/wiki, admin, security, exclusions, clusters, ESLint metrics, database sources
- `018_memory_graph.sql` through `026_memory_write_receipts.sql`: memory graph, .NET support, claim graph, observations, indexes, projections, reviews, receipts
- `027_mcp_personal_access_tokens.sql` through `040_repository_review_requested_by_username.sql`: MCP PATs, prompt overrides, vitality, LLM usage, assistant runs/chat/debug/leases/resume, MCP tool invocations, review user attribution
- `041_indexer_runs.sql` through `043_metric_event_id_defaults.sql`: indexer runs and metric event identifiers
- `044_standalone_analysis_batch_fields.sql`: standalone compatibility additions for analysis batch execution mode/source flags, batch request payload/attempt/response fields, and file metric numeric column types
- `045_standalone_project_reviews.sql`: standalone compatibility additions for current CodeGraph project review run/finding persistence
- `046_standalone_job_schedules.sql`: standalone compatibility additions for current CodeGraph job schedule persistence
- `047_standalone_embeddings.sql`: standalone compatibility additions for current CodeGraph vector embedding persistence
- `048_standalone_memory_external_ids.sql`: standalone compatibility additions for stable claim/evidence/observation external IDs and memory write receipt fields

Porting implication: `CodeGraph.Data.MariaDb` should start from donor `TC.CodeGraphApi.Data`, then absorb memory-data concepts or expose companion interfaces without retaining TC namespaces.

## Messaging, Jobs, And Hosting

Current CodeGraph:

- API and jobs are standard `CodeGraph.Api` and `CodeGraph.Jobs` projects.
- Messaging abstraction exists via `CodeGraph.Services/Messaging/IMessageBus.cs` and `MassTransitMessageBus.cs`.
- API hosts MassTransit consumers for repository processing, analysis, synthesis, repository removal, and memory claim storage.
- Jobs are scheduled through `CodeGraph.Jobs` using persistent schedules.

TC donor:

- Uses TC platform packages in several hosts: `TC.Common.TcServiceStack`, `TC.Common.TcServiceStack.Queue`, `TC.Jarvis.ApiDocumentation`, `TC.Jarvis.DependencyInjection.Autofac`, `TC.Permission.Gateway`, and `TC.JobUtilities`.
- Has split indexer and memory hosts with loopback service bus implementations.
- Has TC-specific consumer definitions and service-stack startup helpers.
- Has standalone internal signed identity pieces for indexer and memory client communication.

Porting implication: TC queue, service-stack, permission, and job utility dependencies should not cross the boundary. Preserve behavior through standard ASP.NET Core hosting, standard DI, the existing message bus abstraction, MassTransit/RabbitMQ where needed, and explicit standalone auth options.

## Split-Host Donor Architecture Inventory (2026-04-27)

This section freezes the corrected donor/current mapping for the required split-host architecture. The donor reference is `/Users/michael/Repos/TC.CodeGraphApi`; the standalone implementation target is `/Users/michael/Repos/CodeGraph`, still on `net10.0`.

### Target Deployable Boundaries

| Target standalone project | Donor project | Donor port | Primary responsibilities | Current standalone status |
| --- | --- | ---: | --- | --- |
| `CodeGraph.Host.Shared` | `TC.CodeGraphApi.Host.Shared` | n/a | Shared host setup, OpenTelemetry wiring, internal service identity options, shared MariaDB/config registration helpers, HTTP clients for memory/indexer | Present as a standalone shared auth/health/activity-source foundation used by split hosts and typed clients. |
| `CodeGraph.Indexer.Client` | `TC.CodeGraphApi.Indexer.Client` | n/a | Typed HTTP client for indexer operations, internal identity header/token attachment, client options/mode registration | Present. API and jobs can delegate through it when `CodeGraph:Indexer:BaseUrl` is configured. |
| `CodeGraph.Indexer.Host` | `TC.CodeGraphApi.Indexer.Host` | 5042 | Repository indexing, repository reanalysis, discovery, schema sync, link/community operations, batch-result processing, TypeScript sidecar warmup, run status, indexer consumers | Present as a deployable host with durable indexer execution, controller routes, consumers, schema sync, and TypeScript sidecar warmup. |
| `CodeGraph.Memory` | `TC.CodeGraphApi.Memory` | n/a | Claim-centric memory contracts, models, request DTOs, legacy bridge models, embedding/claim store interfaces | Deliberately not created for first standalone cutover. Current contracts remain in `CodeGraph.Models/Memory` to avoid churn before API/jobs recomposition. |
| `CodeGraph.Memory.Data` | `TC.CodeGraphApi.Memory.Data` | n/a | MariaDB memory claim store, memory DbContext/records, seed alias utilities | Deliberately not created for first standalone cutover. `CodeGraph.Data` and `CodeGraph.Data.MariaDb/MySqlMemoryGraphStore.cs` remain the persistence boundary over the donor-style MariaDB memory schema. |
| `CodeGraph.Memory.Services` | `TC.CodeGraphApi.Memory.Services` | n/a | Memory write submission/receipts, search, bundles, subgraph/frontier, cleanup, diagnostics, ONNX embeddings, legacy read adapter | Deliberately not created for first standalone cutover. Current memory services stay under `CodeGraph.Services/Memory`, composed by `CodeGraph.Memory.Host` and delegated to by API/MCP through the client boundary. |
| `CodeGraph.Memory.Client` | `TC.CodeGraphApi.Memory.Client` | n/a | Typed HTTP client for memory search/subgraph/frontier/bundles/write status/cleanup/graph, transient retry behavior, internal identity token attachment | Present. API/MCP can delegate memory operations through it when `CodeGraph:Memory:BaseUrl` is configured, with local fallback when empty. |
| `CodeGraph.Memory.Host` | `TC.CodeGraphApi.Memory.Host` | 5039 | Memory REST API, write health probe, `StoreMemory` consumer, memory service composition | Present as a deployable host with memory REST routes, write health, diagnostics, and the async `store-memory-claims` consumer. |
| `CodeGraph.Metrics` | `TC.CodeGraphApi.Metrics` | 5041 | Consume LLM usage and MCP tool invocation events, normalize/idempotently persist telemetry, health endpoint | Present. `CodeGraph.Services/Metrics/MetricsEventPublisher` now publishes telemetry messages; `CodeGraph.Metrics` consumes and persists them through `IMetricsEventRecorder`. |
| `CodeGraph.Jobs` | `TC.CodeGraphJobs` | host-specific | Scheduled triggers for repository processing, discovery, batch processing, link/detect, schema sync, assistant retention cleanup | Present, but still runs integrated jobs/services. Needs to use indexer/memory clients once split boundaries exist. |

### Donor Project References And Packages

All donor split-host projects target `net9.0`; every port must upgrade to `net10.0`.

| Donor project | Project references | Package notes |
| --- | --- | --- |
| `TC.CodeGraphApi.Host.Shared` | Data, Models, Indexer.Client, Memory.Client | OpenTelemetry packages only; no TC service-stack package in the csproj, but concepts must be adapted into standalone host extensions. |
| `TC.CodeGraphApi.Indexer.Client` | Models | `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Http`, `Microsoft.Extensions.Options`. |
| `TC.CodeGraphApi.Indexer.Host` | Host.Shared, Indexer.Client, Models, Data, Services, CSharp/SQL/ColdFusion/TypeScript/Ansible/Terraform extractors | `Autofac.Extensions.DependencyInjection`, `TC.Common.TcServiceStack`, `TC.Jarvis.ApiDocumentation*`. TC packages must be replaced. Add `TreeSitter` extractor in standalone if it should remain available to indexer host. |
| `TC.CodeGraphApi.Memory` | none | `Microsoft.Extensions.Logging.Abstractions`. |
| `TC.CodeGraphApi.Memory.Data` | Memory | `Dapper`, `MySqlConnector`, `Pomelo.EntityFrameworkCore.MySql`, `Microsoft.Extensions.Options`. Use standalone MariaDB option names/versions. |
| `TC.CodeGraphApi.Memory.Services` | Data, Models, Memory, Memory.Data | `Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers`, `TC.Common.TcServiceStack.Queue`. Replace TC queue dependency with standalone messaging abstractions. |
| `TC.CodeGraphApi.Memory.Client` | Memory, Memory.Services | `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Http`, `Microsoft.Extensions.Options`. |
| `TC.CodeGraphApi.Memory.Host` | Host.Shared, Models, Memory, Memory.Data, Memory.Services, Services | `Autofac.Extensions.DependencyInjection`, `TC.Common.TcServiceStack`, `TC.Jarvis.ApiDocumentation*`. TC packages must be replaced. |
| `TC.CodeGraphApi.Metrics` | Host.Shared, Data, Models, Services | `Autofac.Extensions.DependencyInjection`, `TC.Common.TcServiceStack`, `TC.Jarvis.ApiDocumentation*`. TC packages must be replaced. |
| Donor split tests | Host/client/data/service projects under test | `Microsoft.AspNetCore.Mvc.Testing 9.0.0`, EF Core test providers 9.0.0, xUnit, Shouldly, NSubstitute. Upgrade ASP.NET/EF test packages to `.NET 10` compatible versions while porting. |

### Donor Runtime Surfaces To Preserve

### Memory Library Split Decision

For the first standalone cutover, do not create physical `CodeGraph.Memory`, `CodeGraph.Memory.Data`, or `CodeGraph.Memory.Services` projects. The runtime value of the donor split is the deployable host/client boundary, not the namespace reshuffle. The current standalone implementation already has that runtime boundary through `CodeGraph.Memory.Host`, `CodeGraph.Memory.Client`, `IMemoryOperationsService`, and `CodeGraph.Host.Shared`.

Keep memory DTOs/contracts in `CodeGraph.Models/Memory`, store contracts and MariaDB implementation in `CodeGraph.Data`/`CodeGraph.Data.MariaDb`, and memory ingestion/retrieval services in `CodeGraph.Services/Memory` for the first cutover. Revisit physical library extraction only after API/jobs recomposition is stable enough for the move to be mechanical and test-backed instead of mixed with behavior changes.

This decision means the completed memory split work covers the deployable boundary, API/MCP delegation, async write host ownership, diagnostics, cleanup/projection-rebuild parity, compose/CI wiring, and local fallback mode.

Indexer client/host:

- `IIndexerClient` covers index repository, reanalyze repository, discover, link, detect communities, link-all, sync all schemas, sync one schema, process batch results, get run, and get status.
- `HttpIndexerClient` uses routes under `api/indexer` and attaches an internal identity header named `X-Tc-Internal-Identity`; standalone should rename this header to a CodeGraph-owned name or centralize it behind host-shared auth options.
- `IndexerController` exposes `POST api/indexer/repositories/{name}/index`, `POST api/indexer/repositories/{name}/reanalyze`, `POST api/indexer/discover`, `POST api/indexer/link-all`, `POST api/indexer/link`, `POST api/indexer/communities/detect`, `POST api/indexer/schemas/sync-all`, `POST api/indexer/schemas/{sourceId}/sync`, `POST api/indexer/results/process`, `GET api/indexer/runs/{runId}`, and `GET api/indexer/status`.
- `IndexerCommandService` publishes `ProcessRepository` for index/discovery work, executes reanalysis via repository processing plus batch submission, creates durable `indexer_runs` for link/community/schema operations, and reports status from `indexer_runs`, repositories, sync states, and analysis batches.
- `IndexerRunExecutor` owns durable execution for link, detect communities, link-all, schema sync, and run failure/completion transitions. The current standalone `IndexerRunExecutor` already has a broader integrated operation set and `args_json`; preserve that improvement when moving into the host.
- Indexer consumers to port: `AnalysisBatchSubmittedConsumer`, `AnalysisSynthesisCompletedConsumer`, `ProcessRepositoryConsumer`, `ProjectAnalysisResultsProcessedConsumer`, `RepositoryIndexingCompletedConsumer`, and `RepositoryRemovedConsumer`.

Memory client/host:

- `IMemoryRemoteClient` covers search, subgraph, frontier expansion, entity bundle, claim bundle, queued write, write status, write diagnostics, memory diagnostics, graph snapshot, entity-with-relationships, cleanup by source/test data/ids.
- `HttpMemoryRemoteClient` uses routes under `api/memory`, attaches `X-Tc-Internal-Identity`, and retries transient read/query requests up to three attempts. Preserve retry behavior, but move header naming/signing into standalone host shared infrastructure.
- `MemoryController` exposes `POST api/memory/store`, `POST api/memory/search`, `POST api/memory/subgraph`, `POST api/memory/frontier/expand`, `GET api/memory/entities/{id}/bundle`, `GET api/memory/claims/{id}/bundle`, `GET api/memory/writes/{receiptId}`, `GET api/memory/writes/diagnostics`, `GET api/memory/diagnostics`, `GET api/memory/graph`, `GET api/memory/entities/{id}`, and cleanup endpoints under `api/memory/cleanup/*`.
- `MemoryWriteHealthController` provides a write probe that submits a synthetic extraction and polls receipt status.
- `StoreMemoryConsumer` processes async `StoreMemory` messages through `MemoryService` and `MemoryWriteReceiptService`.

Metrics host:

- `LlmUsageRecordedConsumer` consumes `LlmUsageRecorded`, normalizes usernames/tokens/event IDs through `LlmUsageRecorder`, and persists to the existing LLM usage tables.
- `McpToolInvocationRecordedConsumer` consumes `McpToolInvocationRecorded`, normalizes user/tool/error/duration/event ID through `McpTelemetryRecorder`, and persists to the MCP telemetry tables.
- `MetricsEventPublisher` publishes `LlmUsageRecorded` and `McpToolInvocationRecorded` events through RabbitMQ/MassTransit; `CodeGraph.Metrics` records them through MariaDB-backed `IMetricsEventStore`.

### TC-Specific Dependencies To Replace

- Replace `TC.Common.TcServiceStack` host inheritance and `Program.cs` service-stack bootstrapping with standard ASP.NET Core `WebApplication`/generic host startup.
- Remove Consul/service registrar behavior such as `DISABLE_SERVICE_REGISTRAR`, `TC_COLOCATION`, `HostingOptions.ConsulAgentUrl`, and Jarvis API documentation packages.
- Replace `TC.Jarvis.DependencyInjection`/Autofac scope usage with standard Microsoft DI unless a specific local pattern demands otherwise.
- Replace `TC.Common.TcServiceStack.Queue`/`ITcServiceBus` with the existing standalone `IMessageBus` plus MassTransit/RabbitMQ consumers. Loopback buses may remain as local/test adapters, but should implement standalone interfaces.
- Replace `TcConsumer<T>` and `TcConsumerDefinition<T>` with plain MassTransit `IConsumer<T>` and shared retry configuration from standalone `ConsumerOptions`.
- Replace the internal identity header name `X-Tc-Internal-Identity` with a CodeGraph-owned header and service-token/HMAC options under `CodeGraph:InternalServiceAuth` or equivalent host-shared options.
- Keep standalone browser/API auth based on `CodeGraph:AuthOptions`, `CodeGraphUser`, and `CodeGraphAdmin`; do not import TC permission gateway or Jarvis auth assumptions.
- Keep MariaDB and RabbitMQ external/shared on `trefry-network`; the standalone compose/deploy stack must not define owned database or broker containers.

### Current Standalone Code To Harvest

- Indexer scaffolding: `CodeGraph.Services/Indexer/IIndexerOperationsService.cs`, `StandaloneIndexerOperationsService.cs`, `IndexerRunExecutor.cs`, `IndexerRunBackgroundRunner.cs`, `IIndexerRunBackgroundRunner.cs`, `CodeGraph.Api/Controllers/IndexerController.cs`, `CodeGraph.Data/IIndexerRunStore.cs`, and `CodeGraph.Data.MariaDb/MySqlIndexerRunStore.cs`.
- Indexer improvements over donor: `args_json` for durable run arguments, run listing/filtering, queued operations for process repositories, reindex all, discovery, link, detect, link-and-detect, batch analysis processing, schema sync, and sync-all.
- Schema sync scaffolding: `CodeGraph.Services/DatabaseSchema/IDatabaseSchemaExtractor.cs` and `DatabaseSchemaExtractor.cs`.
- Messaging: `CodeGraph.Services/Messaging/IMessageBus.cs`, `MassTransitMessageBus.cs`, and plain MassTransit consumers under `CodeGraph.Api/Consumers`.
- Memory scaffolding: current `CodeGraph.Models/Memory`, `CodeGraph.Services/Memory/*`, `CodeGraph.Services/Assistant/MemoryMcpServer.cs`, `CodeGraph.Api/Controllers/MemoryController.cs`, `CodeGraph.Api/Consumers/StoreMemoryClaimsConsumer.cs`, `CodeGraph.Data/IMemoryGraphStore.cs`, and `CodeGraph.Data.MariaDb/MySqlMemoryGraphStore.cs`.
- Metrics scaffolding: `CodeGraph.Services/Metrics/IMetricsEventPublisher.cs`, `MetricsEventPublisher.cs`, `CodeGraph.Data/IMetricsEventStore.cs`, and `CodeGraph.Data.MariaDb/MySqlMetricsEventStore.cs`.
- Auth/config scaffolding: `CodeGraph.Api/Auth/*`, `CodeGraph.Services/Configuration/*`, `CodeGraph.Api/Middleware/McpTelemetryMiddleware.cs`, and the existing RabbitMQ configuration in `Startup.cs`.

### Responsibilities That Must Move Out Of `CodeGraph.Api`

- Repository processing consumers and heavy indexing execution should move to `CodeGraph.Indexer.Host`.
- Batch analysis result processing, repository indexing completion follow-up, cross-repo linking, community detection, database schema sync, and TypeScript sidecar warmup should move to `CodeGraph.Indexer.Host`.
- Async memory write consumption and claim-centric memory read/write/search/cleanup services should move to `CodeGraph.Memory.Host`/`CodeGraph.Memory.Services`, with API/MCP delegating through `CodeGraph.Memory.Client` unless explicitly running in local mode.
- LLM usage and MCP telemetry persistence should move to `CodeGraph.Metrics` consumers; API/services should publish events rather than writing directly in normal split-host mode.
- Jobs should trigger work through `CodeGraph.Indexer.Client` and memory/metrics clients or messages, not by owning the work inline.

### Deployment And Configuration Inventory

- Donor local compose runs API, Memory, Metrics, Indexer, Jobs, and Web as separate services. API depends on Memory and Indexer health; Jobs depends on Indexer health.
- Donor local ports are API `5037`, Memory `5039`, Metrics `5041`, Indexer `5042`, Web `4200`.
- Donor API config has `CodeGraph:Memory:BaseUrl=http://127.0.0.1:5039`, `CodeGraph:Memory:Audience=codegraph-memory`, `CodeGraph:Indexer:BaseUrl=http://127.0.0.1:5042`, and `CodeGraph:Indexer:Audience=codegraph-indexer`.
- Donor host health checks are `/health` for Memory/Metrics and `/health/sidecar` for Indexer.
- Current standalone compose runs API, Web, Jobs, Indexer, Memory, and Metrics, using shared MariaDB/RabbitMQ on `trefry-network`. It does not add owned MariaDB or RabbitMQ containers.

### Implementation Slices Implied By This Inventory

1. Port `CodeGraph.Host.Shared` first with standalone service-to-service auth, OpenTelemetry-friendly setup, shared health/config helpers, and typed client registration support.
2. Port `CodeGraph.Indexer.Client` and adapt API/jobs to call it behind an options-controlled local/remote mode.
3. Create `CodeGraph.Indexer.Host` and move the harvested integrated indexer executor, operations service, controller routes, consumers, extractors, schema sync, and TypeScript warmup into that host.
4. Keep memory libraries in `CodeGraph.Models`, `CodeGraph.Data`/`CodeGraph.Data.MariaDb`, and `CodeGraph.Services` for the first standalone cutover while preserving the deployable `CodeGraph.Memory.Host` and `CodeGraph.Memory.Client` boundary.
5. Continue split-host parity by recomposing API/jobs around the completed indexer, memory, and metrics boundaries, then restore broader donor test coverage.
6. Update `CodeGraph.Jobs`, compose, Dockerfiles, README, and tests after the host/client projects exist.

Standalone auth status:

- `CodeGraph:AuthOptions:Enabled=false` keeps local development open through a synthetic `local-admin` identity.
- `CodeGraph:AuthOptions:Enabled=true` enables standard bearer JWT/OIDC validation using configured `Authority`, `Audience`, and optional valid audiences. The intended external test authority is the Keycloak server at `https://identity.trefry.net`; client/realm/audience details still need to be coordinated before live auth testing.
- `CodeGraphAdmin` protects settings/admin report surfaces and checks role/admin claims before falling back to `IAdminStore.IsAdminAsync`.
- `CodeGraphUser` protects user MCP token management. `/mcp` can still require MCP PATs independently of browser/API JWT auth.
- `GET /api/auth/config` returns the public standalone auth client settings needed by the Angular OIDC bootstrap without requiring an authenticated user; `GET /api/auth/me` returns the current normalized username and admin status through the same `CodeGraphAdmin` policy used by protected admin endpoints.
- Admin users are managed through `GET/POST/DELETE /api/admin/admins`, backed by `IAdminStore` and protected by `CodeGraphAdmin`.
- Prompt overrides are managed through `GET/PUT/DELETE /api/admin/prompts`, backed by `IAdminStore` and protected by `CodeGraphAdmin`. Overrides are applied at execution time for repository analysis batch/synthesis prompts, project review workflow/synthesis prompts, repository review synthesis prompts, and graph assistant system prompts, with catalog defaults used as a fallback if override lookup fails.
- Database sources are managed through `GET/POST/PUT/DELETE /api/database-sources`, backed by `IDatabaseSourceStore` and protected by `CodeGraphAdmin`. Response connection strings are password-masked. `POST /api/database-sources/{id}/sync` and `POST /api/database-sources/sync-all` create durable indexer runs through the integrated `IIndexerOperationsService`; the same `/api/indexer` boundary now queues repository processing, re-index-all, discovery, linking, community detection, link-and-detect, and batch-analysis processing runs.

## Extractor Coverage

Current standalone extractors:

- C#
- SQL
- TypeScript
- Ansible
- ColdFusion
- Terraform
- Tree-sitter fallback for several languages

Donor extractors:

- C#
- SQL
- TypeScript
- Ansible
- ColdFusion
- Terraform

Porting status: Ansible, ColdFusion, and Terraform were imported as first-class `CodeGraph.Extractors.*` projects on 2026-04-26. Donor tests were ported to `CodeGraph.Tests` and passed as part of the extractor coverage slice.

## Angular Surface

Current standalone frontend:

- Routes: repos, repo detail, repo nodes, node detail, search, graph, memory, clusters, impact, ask, access tokens, wiki, settings/admin redirects
- Admin/settings pages: operations, schedules, db health, sections, exclusions, admin users, prompt overrides, database sources, reports, assistant debug
- API access is mostly centralized in `core/api.service.ts`
- Package scripts: `start`, `build`, `watch`, `test`, `test:browser`; `test` runs Vitest through Angular's unit-test builder for jsdom specs, while `test:browser` runs `*.browser.spec.ts` through Playwright/Chromium

Donor frontend additions:

- OAuth callback and `angular-auth-oidc-client` integration
- Route guard for authenticated children and `adminGuard`
- Routes for schemas, schema detail, access tokens, admin reports, assistant debug, admin settings, prompt overrides, users, database sources
- More granular API services under `core/api/`
- More granular typed model files under `core/models/`
- Additional browser/Vitest coverage beyond the restored standalone shell smoke
- Repo detail subviews for dependencies, health, projects, review, security, schema catalog
- Memory graph detail/view components and browser tests

Porting implication: the UI rebase can be sliced independently after backend API and auth shape are decided. Preserve Angular 21, but make TC auth optional/configurable for local standalone use.

## Donor-Only Feature Deltas

High-value donor capabilities not present or less complete in current standalone CodeGraph:

- MariaDB/MySQL runtime persistence
- SQL migration history and database initialization path
- Admin users, settings, prompt overrides, database sources, access tokens, basic reports, and assistant debug run inspection
- Persisted assistant runs, chat history, stream tokens, leases, debug exchanges, and run recovery beyond the current checkpoint/debug capture slice
- MCP personal access tokens and tool invocation telemetry
- LLM usage reporting
- Split or remote indexer and memory host/client architecture
- Indexer runs and status tracking
- Database schema catalog and in-process schema sync
- Metrics event publishing
- Extra extractor projects for Ansible, ColdFusion, and Terraform (ported into standalone on 2026-04-26)
- Richer frontend test surface and browser validation setup
- Expanded backend test coverage for auth, assistant, MCP, metrics, database schema, project search, tokenized matching, split hosts, and donor-only extractors

## Initial Slice Order

Recommended next slices after this inventory:

1. Keep the runtime decision card closed around `.NET 10`.
2. Treat bootstrap as in-place donor import into the existing CodeGraph repository.
3. Port the MariaDB data provider and migration runner before replacing runtime registration.
4. Remove TC hosting/auth/messaging dependencies at the boundary of each imported host or service, not as a separate late cleanup.
5. Treat split host architecture for indexer, memory, and metrics as required for this epic, not an optional collapse/integration decision.
6. Use donor tests as acceptance criteria for each imported capability, upgraded to `net10.0`.

## Open Questions

- Should `CodeGraph.Data.Neo4j` stay temporarily for export/import tooling, or be removed from runtime once MariaDB is registered?
- Live Keycloak testing still needs coordinated realm/client/audience settings for `https://identity.trefry.net`.
