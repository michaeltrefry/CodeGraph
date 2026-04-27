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
- Neo4j-to-MariaDB migration API slice: the dry-run plan is now available through `GET /api/migration/neo4j-to-mariadb/dry-run` behind the admin policy via `INeo4jToMariaDbMigrationService`. Plan steps include stable `neo4j:{area}` exporter keys, `mariadb:{area}` importer keys, `CanExecute=false`, and blocking reasons until real readers/importers are implemented.
- Neo4j-to-MariaDB repositories/graph execution slice: `CodeGraph.Data.Neo4j` now exposes a graph migration exporter for repository metadata, `CodeNode` nodes, graph relationships, and `CrossRepoEdge` records. `INeo4jToMariaDbMigrationService` uses the exporter plus the active MariaDB `IGraphStore` to import repositories, upsert graph nodes, remap Neo4j node IDs to MariaDB IDs, and insert graph/cross-repo edges. `GET /api/migration/neo4j-to-mariadb/dry-run` now includes live counts for the implemented repositories/graph areas, and `POST /api/migration/neo4j-to-mariadb/repositories-graph/run` executes that first slice with returned checkpoints and optional `indexer_runs` status updates. Remaining migration areas still need exporters/importers in manifest order.
- Memory decoupling slice: updated MCP tool descriptions away from Neo4j-specific wording. The standalone direction remains integrated-first over `IMemoryGraphStore`/`MySqlMemoryGraphStore` and existing `MemoryService` contracts, with split memory host/client work deferred until remote-host value is clear.
- Angular browser validation slice: the donor-style Vitest browser target has been restored as `npm run test:browser` using Playwright/Chromium. A standalone shell browser smoke spec now verifies that the top navigation renders the expected route set without creating page-level horizontal overflow, and the shell CSS wraps the nav/search layout cleanly at narrow browser widths. Validation on 2026-04-26: `npm run test:browser` passed with 1 browser test; `npm test -- --watch=false` passed with 20 jsdom tests; `npm run build` passed with only the pre-existing `repo-detail.component.scss` style-budget warning.
- Local dev compose usability slice: `docker-compose.yml` now creates a named MCP network by default instead of requiring a pre-existing external `mcp-shared` network, and API/jobs containers mount the host repo root from `CODEGRAPH_DOCKER_REPOS_MOUNT` into `/repos` while keeping `/repos/.cache` writable through the existing named volume. `.env.example` documents `CODEGRAPH_DOCKER_REPOS_MOUNT` and `CODEGRAPH_DOCKER_MCP_NETWORK`. Validation on 2026-04-26: `docker compose config --quiet` passed.
- Agent guidance refresh slice: repo-level `AGENTS.md` and `CLAUDE.md` now describe the current `.NET 10`, MariaDB-primary standalone architecture, expanded extractor projects, SQL migration path, and Neo4j compatibility/export boundary instead of the older `.NET 9`/Neo4j-primary shape.

## Solution Shape

Current CodeGraph solution has 15 projects:

- `CodeGraph.Api`
- `CodeGraph.Data`
- `CodeGraph.Data.Neo4j`
- `CodeGraph.Extractors.Ansible`
- `CodeGraph.Extractors.ColdFusion`
- `CodeGraph.Extractors.CSharp`
- `CodeGraph.Extractors.Sql`
- `CodeGraph.Extractors.Terraform`
- `CodeGraph.Extractors.TreeSitter`
- `CodeGraph.Extractors.TypeScript`
- `CodeGraph.Jobs`
- `CodeGraph.Jobs.Tests`
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

Porting implication: decide per slice whether these remain split hosts or become integrated endpoints in the standalone API/jobs architecture.

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
5. Treat split host architecture decisions for indexer and memory as explicit design points before copying large surfaces.
6. Use donor tests as acceptance criteria for each imported capability, upgraded to `net10.0`.

## Open Questions

- Should `CodeGraph.Data.Neo4j` stay temporarily for export/import tooling, or be removed from runtime once MariaDB is registered?
- Should the donor split indexer/memory hosts remain separate processes in standalone CodeGraph, or should standalone collapse them into the API/jobs host first?
- Live Keycloak testing still needs coordinated realm/client/audience settings for `https://identity.trefry.net`.
- Should the donor `TC.CodeGraphApi.Metrics` host become a standalone project, or should metrics publishing/reporting live inside the API/jobs services?
