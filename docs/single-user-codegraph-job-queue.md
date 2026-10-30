# Plan: Admin job queue for single-user CodeGraph

Hand-off for an agent working in `github.com/michaeltrefry/CodeGraph`. It ports a feature that was built and merged in the TurnCommerce CodeGraph (`ai/tc.codegraphapi` MRs !175 and !176). Reviewed against standalone CodeGraph `main` through `64d418bd` on 2026-10-30. The implementation uses current standalone source, not donor code verbatim. Shortcut is the operational source of truth; this file is the versioned implementation reference.

## Goal

Add a **Job queue** table at the bottom of Admin → Operations (`CodeGraphWeb/src/app/pages/admin/admin-operations.component.ts`) that shows every job that is **running, queued or failed**:

- indexing, including each repository
- linking, community detection, link + detect
- discover, re-index all, process repos
- batch analysis
- schema syncs, branch overlays, re-analyze
- scheduled Jobs-host work, including wrapper submission failures before an indexer run exists

Behaviour:

- A failed job shows its error and stays until someone clicks **Dismiss**, or until a later run of the same operation and target completes.
- Successful jobs drop out of the table by themselves.
- No progress bars.
- The table polls every 5 seconds.
- A weekly retention job deletes old resolved terminal runs. Unresolved failures remain until dismissal or a later successful matching operation; retention never removes queued or running work.

## What this repo already has (don't rebuild it)

This repo is further along than TC's was. Most of the TC work was building run tracking that already exists here:

- **Every operation is already a durable `indexer_runs` row.** See `IndexerRunOperations` in `src/CodeGraph.Services/Indexer/StandaloneIndexerOperationsService.cs`: `process_repositories`, `reindex_all`, `discover`, `link`, `detect_communities`, `link_and_detect`, `process_batch_analysis`, `sync_schema`, `sync_all_schemas`, `branch_overlay`, `reanalyze`.
- **Each repository already gets its own run.** `ProcessRepositoryConsumer` (`src/CodeGraph.Host.Shared/Consumers/`) hands each `ProcessRepository` message to `ProcessRepositoryHandoff`. The handoff creates (or merges into an existing active) `process_repository` run, with `Target` = repo name. **Don't add publish-time run creation or a run id on the message**; that was the TC-only part. Messages sit in RabbitMQ only for the instant before the handoff inserts the row, which is negligible.
- **Runs are leased and heartbeated** (migration `059_durable_indexer_runs.sql`), and runs left behind by a restart are recovered. So TC's "stale" tag and the option to dismiss stuck active rows are **not needed** here.
- **Retries are already modelled.** `FailOrRetryIndexerRunAsync` puts a retryable failure back to `queued` with `next_attempt_at`. Only an exhausted run ends `failed`. Errors are in `error` / `error_code`.
- **Cancel already exists.** `POST /api/indexer/runs/{id}/cancel` → `CancelRunAsync`; `canceled` is a terminal status.
- **A generic run list already exists:** `GET /api/indexer/runs?status=&operation=&take=` (`IndexerController`). The Angular `api.service.ts` already has `listIndexerRuns`, `getIndexerRun` and `cancelIndexerRun`, but nothing in the UI uses them yet.
- **Scheduling is data in this repo.** `job_schedules` (migration `046`) runs in-process via `ScheduleRunnerWorker`, and default schedules are seeded by migration (see `086_default_work_retention_schedules.sql`). So unlike TC, the retention job's schedule ships with the code.

## Gaps to build

1. **A queue query.** The existing list is newest-first, capped at 200, and has no notion of active-or-failed, dismissal, or failures that a later success has superseded.
2. **Dismissal**, persisted so it survives reloads.
3. **Analysis batch visibility and errors.** `process_batch_analysis` is a short run. Provider batches waiting or failed live in `analysis_batches`, which has no error column. `BatchAnalysisService` sets `failed` without recording why (search for `UpdateBatchStatusAsync(pending.Id, "failed"`).
4. **Scheduled Jobs-host work that isn't an indexer run**: `RegenerateMcpDocs`, `AssistantRetentionCleanup`, `IngestConventionEmbeddings`, `PruneWorkSessions`, `PruneWorkMessages` and `WorkImport` (see `src/CodeGraph.Jobs/JobTypes.cs`). Their execution state is on `job_schedules` (`lease_*`, `last_run_status`, `last_error`). Persist unresolved failure time and error independently because starting a retry overwrites the last-run fields. Wrapper schedules also need visibility when dispatch fails before handoff.
5. **The UI table.**
6. **Retention** for `indexer_runs`.

## Implementation

### 1. Migration `093_job_queue_visibility.sql`

Use the next free number after `092`. Never reuse `074` or `088`, and leave the unused `076` / `090` alone. Follow `sql/migrations/README.md`: every statement must be restart-safe and use `IF [NOT] EXISTS`. The runner has a quote/comment-aware statement splitter. Keep statements restart-safe and never edit previously started migrations.

```sql
ALTER TABLE indexer_runs ADD COLUMN IF NOT EXISTS dismissed_at DATETIME(6) NULL;
ALTER TABLE analysis_batches ADD COLUMN IF NOT EXISTS error TEXT NULL;
ALTER TABLE analysis_batches ADD COLUMN IF NOT EXISTS dismissed_at DATETIME(6) NULL;
ALTER TABLE job_schedules ADD COLUMN IF NOT EXISTS failure_dismissed_at DATETIME(3) NULL;
ALTER TABLE indexer_runs ADD COLUMN IF NOT EXISTS failure_resolved_at DATETIME(6) NULL;
ALTER TABLE indexer_runs ADD COLUMN IF NOT EXISTS queue_work_identity VARCHAR(64) NULL;
ALTER TABLE job_schedules ADD COLUMN IF NOT EXISTS failure_at_utc DATETIME(3) NULL;
ALTER TABLE job_schedules ADD COLUMN IF NOT EXISTS failure_error TEXT NULL;
-- Start the queue empty rather than full of historical failures.
UPDATE indexer_runs SET dismissed_at = UTC_TIMESTAMP(6) WHERE status = 'failed' AND dismissed_at IS NULL;
UPDATE analysis_batches SET dismissed_at = UTC_TIMESTAMP(6) WHERE status = 'failed' AND dismissed_at IS NULL;
UPDATE job_schedules SET failure_dismissed_at = UTC_TIMESTAMP(3) WHERE last_run_status = 'failed' AND failure_dismissed_at IS NULL;
```

Also map the new columns in `src/CodeGraph.Data.MariaDb/CodeGraphDbContext.cs` and on the entities.

### 2. Store: queue query, dismiss, retention delete

Add these to `IIndexerRunStore` (`src/CodeGraph.Data/IIndexerRunStore.cs`) and implement them in `MySqlIndexerRunStore`. The only other implementer is the test fake in `StandaloneIndexerOperationsServiceTests`.

- `Task<JobQueueResponse> GetJobQueueAsync(int limit, CancellationToken ct)`
- `Task<bool> DismissJobAsync(string kind, long id, CancellationToken ct)`: returns false for an unknown kind, an unknown id, or a row that already completed.
- `Task<int> DeleteFinishedIndexerRunsBeforeAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)`

**Queue query.** Use one SQL CTE (Dapper or raw SQL) that unions the sources, then run it twice: once with `ORDER BY ... LIMIT @limit` for the items, and once with `GROUP BY Status` for the counts. TC's version is in `MySqlGraphStore.IndexerRuns.cs` in `ai/tc.codegraphapi`.

- **`indexer_runs`**, kind `indexer-run`: include all queued/running rows and failed rows whose `dismissed_at` and `failure_resolved_at` are null. Resolve an older failed row when a later matching run is completed. For simple operations compare operation and null-safe target. For `process_repositories`, compare the actual normalized repository set from args (order independent), not the display string such as "2 repositories". For `branch_overlay`, compare repository and requested ref/commit scope through the overlay record, not its unique overlay ID. Store a SHA256 `queue_work_identity` for these special operations, independently of the execution target and submission hash, and backfill existing active/history rows before resolution/deletion. Conservatively retain failures when legacy metadata is insufficient. Persist resolution before pruning a successor, and keep any successful witness still needed by an older active run that could later fail.
  Return `next_attempt_at` and `attempt_count` too, so the UI can show "retrying at …". Use `error`, falling back to `message`.
- **`analysis_batches`**, kind `analysis-batch`: rows that are not `completed`/`failed` show as `running`. Failed rows, not dismissed and not superseded by a later `completed` batch for the same `repo`, show as `failed`. Operation is `analysis-batch`, target is `repo`. Check the actual status values in `BatchAnalysisService`, and note that `execution_mode` exists.
- **`job_schedules`**, kind `scheduled-job`: include leased non-indexer jobs and every unresolved schedule failure, including `Discover`, `ReIndexAll`, `ProcessBatchAnalysis`, `LinkAndDetect` and `DetectCommunities` submission failures. Suppress wrapper running rows when the durable run represents their execution, avoiding duplicate successful handoffs without hiding failures before handoff.
  - **Running:** `lease_expires_utc > UTC_TIMESTAMP(3)` for non-wrapper jobs.
  - **Failed:** independent persisted unresolved failure exists and is not dismissed. A retry must leave the previous error visible until success, including while the retry is running or after its lease expires.
  - Operation is `job_type`, target is schedule `name`; use execution/failure timestamps, not the schedule creation date, for Since and ordering.
  - A failed completion records a fresh failure and resets its dismissal. A successful completion uses **`succeeded`**, and clears the unresolved failure. Update within both existing fenced completion branches; preserve lease and schedule-revision behavior. Starting a retry must not clear the independent failure.
  - A schedule may have both a running row and an unresolved failed row during a retry. Angular tracking must include kind, id, and status. Dismissal must affect only failed state and preserve execution ownership.

**Ordering:** running, then failed (newest first), then queued (oldest first, the order the queue drains). Cap items at 500. `ORDER BY FIELD(Status,'running','failed','queued'), CASE WHEN Status='failed' THEN CreatedAt END DESC, CreatedAt, Id`.

**Mapping:** map rows into a private mutable row class rather than straight into a positional record; Dapper's constructor matching is fussy about types. Map `COUNT(*)` as `long`.

**Response contract** (put it in `CodeGraph.Models`):

```csharp
public sealed record JobQueueEntry(string Kind, long Id, string Operation, string? Target, string Status,
    string? RequestedByUsername, string? Message, string? Error, DateTime CreatedAt, DateTime? StartedAt,
    DateTime? NextAttemptAt, int AttemptCount, bool Cancelable);
public sealed record JobQueueResponse(IReadOnlyList<JobQueueEntry> Items, int Running, int Queued, int Failed, DateTime GeneratedAt);
```

`Cancelable` is true only for `indexer-run` rows that are queued or running and have no `cancel_requested_at`.

**`process_repository` requester.** `requested_by_username` is always `process-repository`. If `args_json` carries the original requester, surface it; otherwise show the operation's own requester where one exists and leave it at that.

**Batch errors.** Pass exception details into a new optional error parameter on `UpdateBatchStatusAsync`, propagating through `IAnalysisStore`, `MySqlAnalysisStore`, the `MySqlGraphStore` forwarding adapter, the Neo4j compatibility implementation, and all fakes. Handle terminal provider failure/cancellation/expiration explicitly rather than polling forever. Aggregate item failures (including empty/missing or invalid results) into a failed parent batch with meaningful persisted error details; never increment a success count for an errored item or let a failed parent supersede an older failure. Preserve valid successful item results and existing provider retry behavior. Add error metadata to provider result contracts where required so provider-supplied reasons survive. Successful parent batches alone supersede older failures for the same repository. Submission currently saves the parent before request rows: keep a declared nonzero request set pending while its rows are still absent or partially persisted, including LM Studio direct status checks. Judge missing provider output only after the full local request set exists; cover a paused submission overlapping a completion sweep.

### 3. API

Add these to whichever admin controller the Operations page already uses, behind the same admin authorization:

- `GET /api/admin/jobs/queue` → `GetJobQueueAsync(500)`
- `POST /api/admin/jobs/queue/{kind}/{id:long}/dismiss` → 204, or 404 when nothing was dismissed

Reuse the existing `POST /api/indexer/runs/{id}/cancel` for Cancel. If a constructor change would ripple through controller tests, use `[FromServices]` on the action.

### 4. UI

Create a standalone `AdminJobQueueComponent` (`admin-job-queue.component.ts`) and render it at the end of `admin-operations.component.ts`.

- Header: running / queued / failed count chips.
- Table columns: Job (a friendly label per operation), Target, Status chip, Requested by, Since (`startedAt` when running, else `createdAt`), Detail, and an actions column.
  - **Detail:** the error in red monospace for failures. For queued rows with `nextAttemptAt`, "Retrying at …". Otherwise the message.
  - **Actions:** **Dismiss** for failed rows and **Cancel** for cancelable rows. After either, refresh.
- Polling: poll every 5s with `setInterval`, and clear it with `inject(DestroyRef).onDestroy`.
- Loading and empty states: if a load fails, show the error but keep the last good data. When the queue is empty, show "Nothing is running, queued or failed.", and "Showing N of M" when items are capped.
- Timestamps: parse them as UTC by appending `Z` when there's no offset. TC's admin components have a `parseUtcDate` helper; check whether this repo has one.
- Follow the existing `cg-table` / `adm-card` / `cg-chip` styles, matching neighbouring admin components.

### 5. Retention job, scheduled weekly by migration

- **Job:** `IndexerRunRetentionJob : IJobCommand<…>`, modelled on `PruneWorkMessagesJob`, with a small retention service like `WorkMessageRetentionService`.
  - Add `JobTypes.IndexerRunRetention` and add it to `JobTypes.All`.
  - Register it wherever the other jobs are wired (see `CodeGraphJobSchedulingServiceCollectionExtensions.cs` / `JobCommandDispatcher.cs`).
- **Request:** `retentionDays`, default 30, must be greater than 0.
- **Deletion policy:** loop in bounded batches (default/max 5000) until a batch is short. Delete old completed/canceled rows and old failed rows only after dismissal or durable resolution. Never delete an unresolved failed row. Materialize supersession before deleting successful evidence so old failures cannot reappear. Respect the caller's batch size and check cancellation between batches. Keep the age cutoff based on `COALESCE(completed_at, created_at)`.
- **This deliberately differs from TC, which deleted every status.** Here, a leased `queued`/`running` row must never be deleted from under the worker. Fencing would make the worker's finish a silent no-op.
- **Side effects of deleting old rows:**
  - It frees old `(requested_by_username, submission_key)` idempotency keys. That's fine after 30 days, but say so in a comment.
  - Check whether anything else references `indexer_runs.id` (for example `branch_overlays.indexer_run_id`) before deleting. Dangling references are acceptable only if nothing joins on them.
- **Message:** `"Indexer run retention deleted N rows."`
- **Seed migration:** in `094_default_indexer_run_retention_schedule.sql`, mirror `086`:
  ```sql
  INSERT INTO job_schedules (name, job_type, is_enabled, cron_expression, time_zone_id, args_json, next_run_utc)
  SELECT 'indexer-run-retention', 'IndexerRunRetention', TRUE, '0 4 * * 0', 'UTC', '{}', UTC_TIMESTAMP(3)
  FROM DUAL
  WHERE NOT EXISTS (SELECT 1 FROM job_schedules WHERE job_type = 'IndexerRunRetention')
    AND NOT EXISTS (SELECT 1 FROM job_schedules WHERE name = 'indexer-run-retention')
  ```
  That's Sunday 04:00 UTC. Confirm the scheduler's cron dialect first: `086` uses 5-field cron, not Quartz.
- **Admin Schedules page:** add the new type to its existing job-type list. Support editing and round-tripping `retentionDays` when creating or editing this schedule.

## Tests

- **MariaDB store tests**, next to `src/CodeGraph.Tests/Data/MariaDbIndexerRunStoreTests.cs`, following its isolated-database pattern and existing configured MariaDB test environment. Linux CI supplies MariaDB; use a native local server when available, without adding a container harness.
  - The queue lists queued, running and failed runs in order, and excludes completed and canceled ones.
  - A failure drops out once a later run with the same operation and target completes; a failure with a different target stays. Cover a `NULL` target, reordered identical repository sets, unrelated equal-size repository sets, and replacement overlays with different IDs.
  - Dismissing returns true once, false the second time, false for completed runs and unknown kinds, and the dismissed row is hidden.
  - Analysis batches: submitted shows as running, a failure carries its error, dismissal works, and only a later successful parent supersedes it.
  - Scheduled jobs: leased non-wrappers show as running; wrapper submission failures appear even without a run. Failure survives retry start and expired retry leases, dismissal persists, a new failure reappears, and successful completion clears it. Preserve fenced-write behavior.
  - Retention preserves unresolved failures and active rows regardless of age, removes only eligible old terminal rows in bounded batches, and cannot resurrect failures by deleting their successful successor.
- **Job tests** (`src/CodeGraph.Jobs.Tests`): the delete loop runs until a short batch, `retentionDays` overrides the cutoff, and invalid values throw without deleting.
- **`BatchAnalysisService`:** exception, provider terminal failure, mixed/all failed requests, malformed/empty results, and successful batches have the correct parent status, counts, and error details. Use stub providers and no paid/live model calls.
- **Angular spec** for the component, using vitest the same way the neighbouring `*.spec.ts` files do: it loads on creation, polls, stops after destroy, Dismiss posts the right URL and refreshes, Cancel calls `cancelIndexerRun`, and a failed load keeps the last data.
  - Gotcha from TC: `inject(DestroyRef)` inside `runInInjectionContext(Injector.create(...))` resolves to the injector itself, not to a provided `DestroyRef`. Stop polling in the test with `injector.destroy()`, and guard `afterEach` against destroying it twice.

Run `dotnet build CodeGraph.sln`, `dotnet test CodeGraph.sln`, and the Angular tests and build before calling it done.

## Docs

- Add a short Design Decisions bullet to `CLAUDE.md` (and `AGENTS.md` if it mirrors it) covering:
  - the queue's sources: `indexer_runs`, `analysis_batches` and `job_schedules` execution/failure state
  - how a failure leaves the queue: dismissal, or a later successful run
  - the retention job and its seeded weekly schedule
- If the README lists admin features or job types, update it.

## Out of scope

- Progress bars.
- Tracking messages before the handoff.
- A stale tag for long-running rows; leases already handle orphans here.
- Retention for `analysis_batches`.

## Shortcut slice plan

Operational source: [epic 27994](https://app.shortcut.com/trefry/epic/27994). One coherent delivery batch:

1. [sc-27995](https://app.shortcut.com/trefry/story/27995) **Feature — Durable Admin job queue and failure lifecycle:** migration, semantic supersession, persistent resolution/dismissal, schedule retry/dispatch failures, queue API, Angular table and regression coverage.
2. [sc-27996](https://app.shortcut.com/trefry/story/27996) **Bug — Preserve analysis provider and item failures:** terminal provider states, truthful parent status/count/error, store/compatibility propagation and regressions. Integrates with slice 1's error columns.
3. [sc-27997](https://app.shortcut.com/trefry/story/27997) **Chore — Retain unresolved failures while pruning old indexer runs:** bounded service/job, seeded weekly schedule, editable retention days, docs and integration checks. Depends on slice 1's durable resolution semantics.

Integrated acceptance reuses the slice tests plus the full .NET build/test, Angular test/build, browser smoke where supported, independent combined review, and required exact-head CI. No story is Done before its shared implementation is merged and verified.
