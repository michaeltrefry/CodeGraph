# Production Deployment

On a push to `main`, the CI workflow builds six images once and pushes them to GHCR tagged with the commit SHA (pull requests build but never push). After that CI run succeeds, the deploy workflow retags those exact images as `latest` without rebuilding and can deploy them to the public host. A failed CI run on `main` skips deployment, and a manual dispatch is refused unless a CI push run succeeded for the commit:

- `ghcr.io/<owner>/codegraph-api:<sha>`
- `ghcr.io/<owner>/codegraph-jobs:<sha>`
- `ghcr.io/<owner>/codegraph-indexer:<sha>`
- `ghcr.io/<owner>/codegraph-memory:<sha>`
- `ghcr.io/<owner>/codegraph-metrics:<sha>`
- `ghcr.io/<owner>/codegraph-web:<sha>`

The deployment job is gated by the production environment variable:

```text
CODEGRAPH_DEPLOY_ENABLED=true
```

Required GitHub production environment variables:

```text
CODEGRAPH_DEPLOY_HOST=<ssh host>
CODEGRAPH_DEPLOY_USER=<ssh user>
CODEGRAPH_DEPLOY_PATH=/opt/codegraph
```

Required GitHub production environment secrets:

```text
CODEGRAPH_DEPLOY_SSH_KEY=<private ssh key, or use CODEGRAPH_DEPLOY_SSH_KEY_B64>
CODEGRAPH__STORAGEOPTIONS__MARIADBCONNECTIONSTRING=<MariaDB connection string>
CODEGRAPH__STORAGEOPTIONS__MARIADBENCRYPTIONKEY=<32-byte encryption key>
CODEGRAPH__RABBITMQOPTIONS__USERNAME=<RabbitMQ username>
CODEGRAPH__RABBITMQOPTIONS__PASSWORD=<RabbitMQ password>
CODEGRAPH__REPOSITORYSOURCE__GITHUB__PERSONALACCESSTOKEN=<GitHub token>
CODEGRAPH__INTERNALSERVICEAUTH__HMACKEY=<internal service HMAC key>
```

`CODEGRAPH_DEPLOY_SSH_KEY_B64` can be used instead of `CODEGRAPH_DEPLOY_SSH_KEY` when storing the private key as base64 is more convenient.

Optional GitHub production environment secrets:

```text
CODEGRAPH_DEPLOY_SSH_KEY_PASSPHRASE=<private ssh key passphrase>
```

Embedding model files are downloaded on the remote host during deployment if they are missing. By default the deploy helper writes to `${CODEGRAPH_DOCKER_MODELS_MOUNT:-./.cache/models}/embeddings/nomic-embed-text-v1.5/`, which is the same directory mounted into containers at `/models`.

Before authenticating to GHCR, the deploy workflow prunes all Docker images that are not referenced by a container. Images used by the running deployment are retained, while unused images from older deployments are removed to reclaim disk space before pulling the next release.

Optional GitHub production environment variables for the embedding download:

```text
CODEGRAPH_DOCKER_MODELS_MOUNT=/opt/codegraph/models
CODEGRAPH_EMBEDDING_MODEL_ONNX_URL=https://huggingface.co/nomic-ai/nomic-embed-text-v1.5/resolve/main/onnx/model.onnx?download=true
CODEGRAPH_EMBEDDING_MODEL_VOCAB_URL=https://huggingface.co/nomic-ai/nomic-embed-text-v1.5/resolve/main/vocab.txt?download=true
```

The production indexer is resource-isolated from the API and database. Its defaults are
two CPUs and 10 GiB of memory (with no additional swap), and can be tuned without editing
the compose files. Rust semantic indexing uses the same two-worker ceiling and excludes
dependency source documents while preserving external SCIP references:

```text
CODEGRAPH_INDEXER_CPUS=2.0
CODEGRAPH_INDEXER_MEMORY_LIMIT=10g
CODEGRAPH_RUST_SEMANTIC_TIMEOUT_SECONDS=600
CODEGRAPH_RUST_SEMANTIC_MAX_THREADS=2
CODEGRAPH_RUST_SEMANTIC_STDERR_TAIL_CHARACTERS=4096
CODEGRAPH_RUST_SEMANTIC_CRATE_GROUP_SIZE=8
CODEGRAPH_RUST_SEMANTIC_GROUP_TIMEOUT_SECONDS=600
CODEGRAPH_RUST_SEMANTIC_WORKSPACE_TIMEOUT_SECONDS=3600
CODEGRAPH_RUST_SEMANTIC_GROUP_MEMORY_LIMIT_MB=8192
CODEGRAPH_RUST_SEMANTIC_BUILD_SCRIPTS=false
CODEGRAPH_RUST_SEMANTIC_PROC_MACROS=false
```

Branch overlay indexes (API and indexer) expire after a TTL and are swept periodically:

```text
CODEGRAPH_BRANCH_OVERLAY_TTL_HOURS=72
CODEGRAPH_BRANCH_OVERLAY_SWEEP_INTERVAL_MINUTES=15
```

Rust semantic failures are not automatically replayed: they are deterministic for the
current checkout and toolchain, and immediate retries can monopolize the host.
An analyzer terminated with exit 137 is reported as
`rust_semantic_resource_exhausted` together with bounded cgroup memory metrics when the
container runtime exposes them.

The deploy workflow builds the remote `.env` file from individual GitHub production environment variables and secrets. Public/non-sensitive app settings should be stored as environment variables, using upper-case names:

```bash
ASPNETCORE_ENVIRONMENT=Production
DOTNET_ENVIRONMENT=Production
CODEGRAPH__AUTHOPTIONS__ENABLED=true
CODEGRAPH__AUTHOPTIONS__AUTHORITY=https://identity.trefry.net/realms/trefry
CODEGRAPH__AUTHOPTIONS__AUDIENCE=codegraph-api
CODEGRAPH__AUTHOPTIONS__CLIENTID=codegraph-web
CODEGRAPH__AUTHOPTIONS__SCOPE=openid profile email
CODEGRAPH__AUTHOPTIONS__AUTHORIZATIONURL=https://identity.trefry.net/realms/trefry/protocol/openid-connect/auth
CODEGRAPH__AUTHOPTIONS__TOKENURL=https://identity.trefry.net/realms/trefry/protocol/openid-connect/token
CODEGRAPH__AUTHOPTIONS__ENDSESSIONURL=https://identity.trefry.net/realms/trefry/protocol/openid-connect/logout
CODEGRAPH__AUTHOPTIONS__ALLOWEDORIGINS__0=https://codegraph.trefry.net
CODEGRAPH__AUTHOPTIONS__REQUIREHTTPSMETADATA=true
CODEGRAPH__MCPOPTIONS__REQUIREPERSONALACCESSTOKEN=true
CODEGRAPH__REPOSITORYSOURCE__GITHUB__INCLUDEAUTHENTICATEDUSER=true
CODEGRAPH__REPOSITORYSOURCE__GITHUB__USERACCOUNTS=
CODEGRAPH__REPOSITORYSOURCE__GITHUB__ORGANIZATIONS=SceneWorks
```

`USERACCOUNTS` and `ORGANIZATIONS` accept comma-separated GitHub owner names. The authenticated-user scope includes repositories accessible to the configured token; named user scopes include public repositories, and organization scopes include private repositories only when the token has access.

LLM provider tokens, provider endpoints, model lists, default model selections, and analysis/review/assistant token caps are not production deployment secrets. After first boot, configure them from the admin Settings page under **LLM Configuration**; provider tokens are stored encrypted in MariaDB and are never returned by the API. If startup logs include `llm.config.deprecation`, those entries identify appsettings fallback values that should be saved through the admin page.

The deploy workflow intentionally skips `CODEGRAPH__ANALYSISOPTIONS__...` production variables for LLM runtime settings. The only `AnalysisOptions` values that belong in production environment variables are non-LLM CODEGRAPH.md automation settings such as `AutoCommitDocs`, `AutoPushDocs`, and their commit author/message fields.

The production compose override in `deploy/docker-compose.production.yml` replaces local builds with GHCR images for API, indexer, memory, metrics, jobs, and web. Keep shared MariaDB/RabbitMQ settings, repository mounts, TLS/reverse-proxy paths, and internal service auth values in GitHub Actions variables/secrets rather than hand-maintaining a bundled `.env` secret.

API, indexer, memory, metrics, and Jobs may start concurrently. Each blocks before HTTP readiness or background-worker execution on the same database-scoped MariaDB advisory lock; the first lock owner applies pending statement-journaled migrations and the others re-check history after acquiring the lock. Ensure the shared MariaDB service is accepting connections before expecting application health checks or scheduled jobs to run. A migration error or lock timeout intentionally fails host startup so container restart policy can retry restart-safe statements from the journal instead of exposing a partially migrated schema. SQL errors remain fatal; the runner does not use a global duplicate-error reconciliation rule.

`CODEGRAPH_TRUSTED_DOTNET_REPOSITORIES` (empty by default) is forwarded to the indexer as `CodeGraph__IndexingOptions__TrustedDotnetRepositories`; set it to provider-qualified canonical repository identities to opt them in.

## Post-deploy verification

After `docker compose up -d`, the **Verify deployment health** step runs `deploy/verify-deployment.sh` on the host over the same SSH connection settings (every `ssh`/`scp` uses `ConnectTimeout`, `ServerAliveInterval`, and `ServerAliveCountMax` so long remote commands survive idle NAT timeouts). The script waits up to 10 minutes for every compose service to run a container on the deployed `IMAGE_TAG`, in the `running` state, not restarting, with a `healthy` docker healthcheck, then confirms no container restarted during a 30-second stability window. A service that restarts three or more times fails fast as a crash loop. On failure it prints `docker compose ps -a`, recent healthcheck output, and the last 100 log lines of each unready service (so migration errors are visible in the job log), and the job fails.

The production compose override defines the healthchecks. Because every host applies migrations before it listens, a healthy check also means migrations completed:

- API, indexer, memory, metrics: `GET http://127.0.0.1:<port>/health` from inside the container (the API's `/health` is anonymous but answers only loopback callers; all return only the aggregate status word, which includes the MassTransit bus check). By design, a RabbitMQ outage during a deploy therefore keeps these services unhealthy and fails verification after the 10-minute timeout.
- Jobs: the `/tmp/codegraph-jobs.ready` marker, cleared at process start and written after migrations.
- Web: `GET http://127.0.0.1/` from nginx.

When `CODEGRAPH_DEPLOY_ENABLED` is not `true`, the deploy job succeeds but emits a `Deployment skipped` notice and job summary.

## GitHub webhook and work evidence

`POST /api/webhooks/github` (signed with `CodeGraph:RepositorySource:GitHub:WebhookSecret`) queues the default-branch index on a push and, for work tracking, records evidence on work items. Subscribe the webhook to: **push** (all branches), **pull requests**, **pull request reviews**, **pull request review comments** and **issue comments**.

- Branches, PR titles and PR bodies naming an item key (`SW-142`) or an imported alias (`sc-27866`) link to that item. The repository must belong to the item's project.
- Forward moves only (In Progress on a push, In Review on an open PR); a merge flags `merged_awaiting_acceptance` and never completes the item; a PR closed unmerged returns an In Review item to Ready only when that PR is linked to the item's current attempt. Evidence of superseded attempts is recorded and moves nothing.
- Reviews and comments are stored with source (human or bot) and priority. The repository owner's login counts as the owner automatically; for **organisation-owned repositories** set `CodeGraph:RepositorySource:GitHub:OwnerLogins` (comma-separated GitHub logins) so the owner's reviews are stored with high priority (bots and other accounts are normal).
- Evidence that changes no link, status or claim (a comment, a review) is fed to the change feed but does not bump the item's version.

### Redelivering a failed delivery

GitHub does not retry failed deliveries on its own. A transient store failure (deadlock, lock wait, timeout) is retried three times inside the request; if it still fails the response is HTTP 500 and the delivery shows as failed. The handler is idempotent (the `X-GitHub-Delivery` id is the key, per item), so redelivering is always safe:

- UI: repository (or organisation) **Settings > Webhooks > the webhook > Recent Deliveries**, open the failed delivery, **Redeliver**.
- API: `gh api repos/OWNER/REPO/hooks/HOOK_ID/deliveries` lists deliveries (`status_code` not 2xx marks the failures); `gh api -X POST repos/OWNER/REPO/hooks/HOOK_ID/deliveries/DELIVERY_ID/attempts` redelivers one (organisation hooks: `orgs/ORG/hooks/...`).

A redelivered push also resubmits the index run for the same head SHA, which the indexer already treats as a duplicate.

## Work imports (Shortcut, KanbanBoard)

The `WorkImport` job and the admin routes under `/api/work/imports` (sc-27876) bring the Shortcut workspace
and the KanbanBoard "SceneWorks Code Review 2026-10-27" epic into the work tracker. Both sources are only read.

- **Tokens:** MCP Hub credentials `shortcut/apiToken` (read access is enough) and `kanban/apiToken` (a read-scoped
  KanbanBoard PAT). Nothing runs without them.
- **Approval:** the admin routes take a web sign-in only, never a personal access token. `GET
  /api/work/imports/shortcut/settings` returns the proposed team → project table. A dry run
  (`POST .../shortcut/runs {"mode":"dry_run"}`) reports the mapping. `PUT .../shortcut/settings` with
  `"approve": true` approves the table. Imports are refused until then.
- **No seeded schedule:** no WorkImport schedule is seeded, because it needs the token and approved settings first.
  After the full import (`{"mode":"import"}`), create a `WorkImport` schedule under Admin > Schedules. With no args it
  runs the Shortcut delta sync (`updated_since` = start of the last completed import, plus that run's failures).
  It runs until cutover. **Remove the delta schedule at cutover**: from then on, CodeGraph is the tracker.
- **Attachments:** downloaded files live under `WorkImport:AttachmentStoragePath` (default `uploads/work-items`,
  relative to `/app`). `docker-compose.yml` mounts the `codegraph-uploads` volume at `/app/uploads` in both
  `codegraph-api` (serves the files) and `jobs` (downloads them). The production override inherits that mount,
  which also persists wiki attachments (`uploads/wiki`) across redeploys. A record whose file is missing is
  downloaded again on the next run.
- **Until cutover:** a re-run is a three-way merge. Only fields that changed in Shortcut since the last run are
  written; CodeGraph-side changes to the other fields survive. Shortcut wins a field both sides changed.
