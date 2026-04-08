# Embedded Job Scheduling Plan

## Goal

Replace the old external-scheduler pattern with first-class, database-backed job scheduling managed from the Settings UI.

The end state should be:

- `CodeGraph.Jobs` remains the home for job-related code.
- `CodeGraph.Api` references `CodeGraph.Jobs` and exposes schedule management APIs.
- A worker process in `CodeGraph.Jobs` runs due schedules directly, without HTTP controllers.
- Schedules are stored in Neo4j with UTC timestamps in the database.
- Manual "Run now" and scheduled execution use the same typed command path.
- Overlap prevention is enforced per schedule/job so the same schedule cannot run twice concurrently.

## Decisions Locked In

- Keep `CodeGraph.Jobs`, but stop treating it like a web-triggered mini API.
- Remove job controllers and the generic `StartJob.Args` dictionary pattern.
- Use typed job request models instead of string dictionaries.
- Store all timestamps in UTC in the database.
- Allow "Run now" from the Settings UI.
- Prevent overlapping runs for the same schedule/job.
- Manual Settings operations and scheduled runs should execute the same underlying commands.

## Current State

Today, job launching is split awkwardly across two models:

- The API exposes manual operations through `src/CodeGraph.Api/Controllers/SettingsController.cs`.
- `src/CodeGraph.Jobs` is a web app that exposes HTTP endpoints for jobs.
- Job launch uses `JobsController` plus `JobRunner` plus `StartJob.Args`.
- Actual long-running work is mostly event-driven after the first command is issued.

This worked when an external scheduler could call HTTP endpoints, but it is not a good fit for the single-user deployment.

## Target Architecture

### High-Level Shape

1. `CodeGraph.Jobs` becomes a worker-oriented project containing:
   - job definitions
   - typed request models
   - job dispatcher
   - schedule evaluation service
   - background worker that polls for due schedules

2. `CodeGraph.Api` references `CodeGraph.Jobs` and exposes:
   - schedule CRUD endpoints
   - run-now endpoints
   - schedule status/history endpoints
   - existing manual operations, refactored to call the same typed command layer

3. Neo4j stores:
   - schedule definitions
   - next due time
   - last run status
   - last error
   - optional short execution history
   - overlap/lease information

4. The worker in `CodeGraph.Jobs`:
   - polls for due schedules
   - atomically claims one
   - runs the typed command
   - records status
   - computes the next run
   - releases the lease

### Recommended Responsibility Split

#### `CodeGraph.Jobs`

- Job contracts and implementations
- Background worker host
- Schedule runner/orchestrator
- Shared typed command execution layer, if the code naturally fits there

#### `CodeGraph.Api`

- Schedule management endpoints
- Validation endpoints
- Existing Settings manual action endpoints
- Dependency registration for stores/services used by scheduling

#### `CodeGraph.Services`

- Shared business logic already used by both API and jobs
- Admin operations
- Batch processing
- Linking/community detection
- Any reusable orchestration logic extracted from current jobs

## V1 Scope

These operations should become schedulable first:

- `Discover`
- `ReIndexAll`
- `ProcessBatchAnalysis`
- `LinkAndDetect`
- `DetectCommunities`
- `RegenerateMcpDocs`

These should remain manual-only in v1 unless a strong need appears:

- `ProcessRepos` with arbitrary pasted repo list
- highly ad hoc operations with one-off operator input

If later needed, `ProcessRepos` can become schedulable as a saved named target set, but it should not complicate v1.

## Data Model

### New Schedule Entity

Add a new entity in `src/CodeGraph.Data/Entities.cs`:

```csharp
public class JobScheduleEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string JobType { get; set; } = "";
    public bool IsEnabled { get; set; }
    public string CronExpression { get; set; } = "";
    public string TimeZoneId { get; set; } = "UTC";
    public string ArgsJson { get; set; } = "{}";
    public DateTime NextRunUtc { get; set; }
    public DateTime? LastRunStartedUtc { get; set; }
    public DateTime? LastRunCompletedUtc { get; set; }
    public string? LastRunStatus { get; set; }
    public string? LastError { get; set; }
    public DateTime? LeaseAcquiredUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
```

### Optional Run History Entity

Recommended for supportability:

```csharp
public class JobScheduleRunEntity
{
    public long Id { get; set; }
    public long ScheduleId { get; set; }
    public string JobType { get; set; } = "";
    public string TriggerType { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string Status { get; set; } = "";
    public string? Error { get; set; }
    public string? TriggeredBy { get; set; }
}
```

`TriggerType` can be values like:

- `schedule`
- `manual`

This is optional for v1, but recommended because it gives the UI something much better than a single "last status" field.

### Storage Rules

- Persist UTC only.
- Keep the chosen time zone ID on the schedule so the UI and next-run calculator can convert correctly.
- Persist `NextRunUtc` to avoid recomputing every schedule on every poll.
- Do not allow null `NextRunUtc` for enabled schedules.
- Disable schedules instead of deleting them when historical continuity matters.

## Schedule Store

Create a focused storage abstraction instead of adding everything onto `IGraphStore`.

### New Interface

Add `src/CodeGraph.Data/IJobScheduleStore.cs` with methods like:

```csharp
public interface IJobScheduleStore
{
    Task<IReadOnlyList<JobScheduleEntity>> ListSchedulesAsync();
    Task<JobScheduleEntity?> GetScheduleByIdAsync(long id);
    Task<JobScheduleEntity> CreateScheduleAsync(JobScheduleEntity entity);
    Task UpdateScheduleAsync(JobScheduleEntity entity);
    Task DeleteScheduleAsync(long id);

    Task<JobScheduleEntity?> TryAcquireDueScheduleAsync(
        DateTime utcNow,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task ReleaseScheduleLeaseAsync(long scheduleId, string leaseOwner, CancellationToken ct = default);
    Task MarkRunStartedAsync(long scheduleId, DateTime startedAtUtc, string leaseOwner, CancellationToken ct = default);
    Task MarkRunSucceededAsync(long scheduleId, DateTime completedAtUtc, DateTime nextRunUtc, CancellationToken ct = default);
    Task MarkRunFailedAsync(long scheduleId, DateTime completedAtUtc, DateTime nextRunUtc, string error, CancellationToken ct = default);
}
```

If run history is included:

- add `AppendRunAsync`
- add `ListRunsAsync(scheduleId, limit)`

### Neo4j Implementation

Implement this in `src/CodeGraph.Data.Neo4j`, likely as a new store file rather than mixing it into unrelated graph code.

Recommended node shape:

- `(:JobSchedule { ... })`
- `(:JobScheduleRun { ... })`
- `(:JobSchedule)-[:HAS_RUN]->(:JobScheduleRun)`

### Migration

Add a new migration under `src/CodeGraph.Api/Migrations`, for example:

- `007_job_schedule_schema.cypher`

Include:

- uniqueness constraint on schedule `appId`
- uniqueness constraint on schedule `name` if names must be unique
- index on `isEnabled`
- index on `nextRunUtc`
- index on `leaseExpiresUtc`
- optional run-history indexes

## Typed Job Contract Design

### Replace Generic Args with Typed Requests

Introduce a closed set of scheduleable job types.

Recommended enum:

```csharp
public enum JobType
{
    DiscoverRepositories,
    ReIndexAllRepositories,
    ProcessBatchAnalysis,
    LinkAndDetect,
    DetectCommunities,
    RegenerateMcpDocs
}
```

For each job type, define a typed request model.

Examples:

```csharp
public sealed record DiscoverRepositoriesJobRequest(
    bool ShouldIndex = true,
    bool ShouldAnalyze = true,
    bool SkipIfUpToDate = true,
    bool IncludeAllSource = true,
    string? NamePattern = null,
    int? Limit = null);

public sealed record ProcessBatchAnalysisJobRequest(
    string? Repo = null);

public sealed record ReIndexAllRepositoriesJobRequest();
public sealed record LinkAndDetectJobRequest();
public sealed record DetectCommunitiesJobRequest();
public sealed record RegenerateMcpDocsJobRequest();
```

### Envelope for Persistence

Because schedules need one persistence field for arguments, store a typed JSON payload alongside `JobType`.

Recommended pattern:

```csharp
public class ScheduledJobDefinition
{
    public JobType JobType { get; init; }
    public string ArgsJson { get; init; } = "{}";
}
```

At runtime:

- deserialize `ArgsJson` based on `JobType`
- validate before execution

This keeps persistence simple without falling back to weak string dictionaries.

## Execution Layer Refactor

### New Dispatcher

Create a typed dispatcher in `CodeGraph.Jobs`, for example:

- `IJobCommandDispatcher`
- `JobCommandDispatcher`

Responsibilities:

- accept `JobType` + typed request
- route to the proper implementation
- return structured execution result

Recommended result shape:

```csharp
public sealed record JobExecutionResult(
    bool Success,
    string Message,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc);
```

### New Job Implementations

Refactor current jobs so they become direct typed implementations instead of `IJob.ExecuteAsync(StartJob)`.

Suggested pattern:

```csharp
public interface IScheduledJob<in TRequest>
{
    Task<JobExecutionResult> ExecuteAsync(TRequest request, CancellationToken ct = default);
}
```

Examples:

- `DiscoverRepositoriesJob : IScheduledJob<DiscoverRepositoriesJobRequest>`
- `ProcessBatchAnalysisJob : IScheduledJob<ProcessBatchAnalysisJobRequest>`
- `ReIndexAllRepositoriesJob : IScheduledJob<ReIndexAllRepositoriesJobRequest>`

### Reuse Existing Services

Most of the real work already lives in services and should stay there:

- `IAdminService`
- `IBatchAnalysisService`
- `IMcpDocService`
- `CrossRepoLinker`
- `ICommunityDetectionService`

The job classes should stay thin and call those services directly.

### Shared Manual and Scheduled Path

Refactor `SettingsController` manual endpoints so they also go through the dispatcher or the same typed job services where appropriate.

That means:

- clicking "Run" in Settings
- clicking "Run now" on a schedule
- worker-triggered due schedule execution

all end up using the same typed command path.

This is important for keeping behavior consistent.

## Worker Host Refactor

### Project Shape

Change `src/CodeGraph.Jobs/CodeGraph.Jobs.csproj` from web SDK to worker/general SDK if possible.

Recommended target:

- `Microsoft.NET.Sdk`

Use generic host in `Program.cs`, not `WebApplication`.

### Remove Web Artifacts

Delete:

- `src/CodeGraph.Jobs/Controllers/JobsController.cs`
- `src/CodeGraph.Jobs/Jobs/JobFramework.cs`
- leftover web setup in `Startup.cs`
- any CORS/controller registration related only to job HTTP endpoints

### Add Background Worker

Create something like:

- `ScheduleRunnerWorker : BackgroundService`

Responsibilities:

1. Wake up every 30 to 60 seconds.
2. Ask store for one due schedulable job it can claim.
3. If none, delay and continue.
4. Mark run started.
5. Execute the job through the dispatcher.
6. Compute next run in UTC from cron plus time zone.
7. Mark success or failure.
8. Release lease.
9. Loop for the next job.

### Lease Strategy

Use a lease field on the schedule row/node so only one worker instance can claim a schedule at a time.

Recommended rules:

- only claim if `IsEnabled == true`
- only claim if `NextRunUtc <= utcNow`
- only claim if lease is absent or expired
- set `LeaseOwner` to machine/process identifier
- set `LeaseAcquiredUtc` and `LeaseExpiresUtc`
- on success or failure, clear lease

This prevents overlap for the same schedule/job while allowing different jobs to run in parallel if desired later.

### Concurrency for V1

Keep it simple:

- one claimed schedule at a time per worker loop
- no parallel execution inside one worker instance

This is enough for correctness first.

If needed later, add bounded parallelism for different schedules.

## Cron and Time Zone Handling

### Library Choice

Pick a mature cron parser with time zone support.

Good options:

- `Cronos`
- `NCrontab` plus separate time zone handling

Recommendation: use `Cronos`.

### Rules

- Store cron expression as provided.
- Store `TimeZoneId` as IANA or Windows ID consistently.
- Normalize all persisted execution timestamps to UTC.
- When a schedule is created or updated:
  - validate cron
  - validate time zone
  - compute `NextRunUtc`

### DST Handling

This is exactly why the time zone ID must stay on the schedule.

The UI should display:

- cron expression
- time zone
- computed next run in local time
- computed next run in UTC if helpful for diagnostics

## API Changes

### New Settings Endpoints

Add endpoints under `api/settings/schedules`:

- `GET /api/settings/schedules`
- `GET /api/settings/schedules/{id}`
- `POST /api/settings/schedules`
- `PUT /api/settings/schedules/{id}`
- `DELETE /api/settings/schedules/{id}`
- `POST /api/settings/schedules/{id}/run`
- `POST /api/settings/schedules/{id}/enable`
- `POST /api/settings/schedules/{id}/disable`
- `GET /api/settings/schedules/{id}/runs`

Optional:

- `POST /api/settings/schedules/validate`

### Request/Response Models

Add models in `CodeGraph.Models` for:

- create schedule request
- update schedule request
- schedule response
- schedule run response
- manual run response

Recommended schedule response fields:

- `id`
- `name`
- `jobType`
- `isEnabled`
- `cronExpression`
- `timeZoneId`
- `args`
- `nextRunUtc`
- `lastRunStartedUtc`
- `lastRunCompletedUtc`
- `lastRunStatus`
- `lastError`

### Service Layer

Add a new service in `CodeGraph.Services`, for example:

- `IJobScheduleService`
- `JobScheduleService`

Responsibilities:

- validate schedule payloads
- serialize/deserialize typed args
- compute next run UTC
- call store CRUD methods
- execute run-now by invoking dispatcher
- expose schedule list/details/history for the UI

## UI Changes

### New Settings Navigation Entry

Extend the Settings nav in `CodeGraphWeb/src/app/pages/admin/admin-layout.component.ts` with:

- `Schedules`

### New Page

Add a new component:

- `CodeGraphWeb/src/app/pages/admin/admin-schedules.component.ts`

Recommended sections:

1. Schedule list
   - name
   - job type
   - enabled/disabled state
   - next run
   - last status
   - last completed
   - run now button
   - edit button
   - enable/disable toggle

2. Create/edit form
   - name
   - job type dropdown
   - job-specific options
   - cron expression
   - time zone
   - enabled toggle
   - preview next run

3. Optional recent runs panel
   - started
   - completed
   - status
   - error summary

### Shared Form Model

Avoid duplicating operation definitions across:

- manual operations page
- schedule page

Create a typed front-end config model for scheduleable operations so:

- labels
- job type IDs
- form fields
- defaults

are defined once.

That will make the Settings UI much easier to keep consistent.

### API Client Cleanup

Right now, `admin-operations.component.ts` talks to `HttpClient` directly.

Recommended cleanup:

- add a small typed admin/settings API client under `CodeGraphWeb/src/app/core` or `services`
- move all schedule and manual operation calls there

This is a good time to stop growing raw endpoint strings in components.

## Detailed Implementation Checklist

### Phase 1: Contracts and Persistence

1. Add `JobType` enum and typed request contracts in `CodeGraph.Models`.
2. Add `JobScheduleEntity` and optionally `JobScheduleRunEntity` in `CodeGraph.Data/Entities.cs`.
3. Add `IJobScheduleStore` in `CodeGraph.Data`.
4. Implement `Neo4jJobScheduleStore` in `CodeGraph.Data.Neo4j`.
5. Add migration `007_job_schedule_schema.cypher`.
6. Register `IJobScheduleStore` in API startup and jobs startup.
7. Add unit tests for schedule CRUD and due-schedule claiming behavior.

### Phase 2: Job Refactor

1. Introduce typed job request classes.
2. Add `JobExecutionResult`.
3. Create `IJobCommandDispatcher` and implementation.
4. Refactor existing jobs to typed implementations.
5. Remove reliance on `StartJob` and string args.
6. Refactor manual Settings actions so scheduleable commands use the dispatcher.
7. Keep non-schedulable ad hoc actions on their current direct path if needed.

### Phase 3: Worker Conversion

1. Convert `CodeGraph.Jobs` to a generic host.
2. Delete controllers and `JobFramework`.
3. Add `ScheduleRunnerWorker : BackgroundService`.
4. Add schedule claim/lease logic.
5. Add cron next-run calculator service.
6. Add structured logging for:
   - claim
   - start
   - success
   - failure
   - next run computed
7. Add worker tests for one full due-schedule execution cycle.

### Phase 4: API Surface

1. Add schedule DTOs.
2. Add `IJobScheduleService` and implementation.
3. Add CRUD endpoints to `SettingsController` or a dedicated `JobSchedulesController`.
4. Add `Run now` endpoint.
5. Add enable/disable endpoints.
6. Add recent run history endpoint if run-history entity is included.
7. Add API tests for create, update, run-now, enable, disable, and validation failures.

### Phase 5: UI

1. Add `Schedules` route and nav item.
2. Add schedule list page.
3. Add schedule create/edit form.
4. Add run-now button and confirmation flow.
5. Display next run and last result clearly.
6. Add job-type-specific forms for:
   - Discover
   - ReIndexAll
   - ProcessBatchAnalysis
   - LinkAndDetect
   - DetectCommunities
   - RegenerateMcpDocs
7. Add a typed admin API service for the UI.

### Phase 6: Cleanup

1. Delete obsolete controller-based job code.
2. Remove dead comments referring to external scheduler polling jobs where no longer accurate.
3. Update README and AGENTS guidance to reflect internal scheduling.
4. Update conventions docs if they still describe the old external scheduler model as current.
5. Verify no deployment scripts still assume jobs are HTTP-triggered.

## Suggested File-Level Changes

### New Files

- `src/CodeGraph.Data/IJobScheduleStore.cs`
- `src/CodeGraph.Data.Neo4j/Neo4jJobScheduleStore.cs`
- `src/CodeGraph.Models/JobScheduling/JobType.cs`
- `src/CodeGraph.Models/JobScheduling/...request/response models...`
- `src/CodeGraph.Services/IJobScheduleService.cs`
- `src/CodeGraph.Services/JobScheduleService.cs`
- `src/CodeGraph.Jobs/Scheduling/ScheduleRunnerWorker.cs`
- `src/CodeGraph.Jobs/Scheduling/IJobCommandDispatcher.cs`
- `src/CodeGraph.Jobs/Scheduling/JobCommandDispatcher.cs`
- `src/CodeGraph.Api/Migrations/007_job_schedule_schema.cypher`
- `CodeGraphWeb/src/app/pages/admin/admin-schedules.component.ts`
- `CodeGraphWeb/src/app/core/admin-settings-api.service.ts`

### Files to Refactor

- `src/CodeGraph.Jobs/Program.cs`
- `src/CodeGraph.Jobs/Startup.cs`
- `src/CodeGraph.Api/Startup.cs`
- `src/CodeGraph.Api/Controllers/SettingsController.cs`
- `src/CodeGraph.Services/AdminService.cs`
- `CodeGraphWeb/src/app/pages/admin/admin-layout.component.ts`
- `CodeGraphWeb/src/app/pages/admin/admin-operations.component.ts`
- `CodeGraphWeb/src/app/app.routes.ts`

### Files to Delete

- `src/CodeGraph.Jobs/Controllers/JobsController.cs`
- `src/CodeGraph.Jobs/Jobs/JobFramework.cs`
- any now-unused web-specific code in `CodeGraph.Jobs`

## Overlap Prevention Details

The requirement is to prevent overlapping runs for the same job.

For v1, interpret that as:

- the same schedule record cannot be executed concurrently
- one worker should not start a second run for a schedule already leased
- different schedules may still run independently

If later you want "all schedules of the same `JobType` are mutually exclusive," that should be a separate rule and a separate lock key.

Recommended v1 lock key:

- schedule ID

## Run-Now Behavior

`Run now` should:

1. load the schedule
2. honor the same typed args as the saved schedule
3. fail fast if the schedule is already running
4. execute through the same dispatcher as scheduled runs
5. record a run-history entry with `TriggerType = manual`
6. not mutate the cron expression or time zone

Recommended behavior for `NextRunUtc` after manual run:

- leave the normal scheduled cadence intact
- do not reset the schedule to "now + interval"

This avoids surprise schedule drift.

## Validation Rules

On create/update:

- name is required
- name is unique
- job type is required
- cron is required and valid
- time zone is required and valid
- args must deserialize successfully for the selected job type
- `NextRunUtc` must be computable

On run-now:

- schedule must exist
- schedule must not already be leased/running
- args must still deserialize successfully

## Testing Plan

### Unit Tests

- cron next-run calculation
- typed args serialization/deserialization
- schedule validation
- dispatcher routes to correct job implementation
- manual and scheduled execution share the same command path

### Store Tests

- create/update/delete schedule
- list schedules ordered reasonably
- acquire due schedule only when enabled and due
- expired lease can be reclaimed
- active lease blocks duplicate claim
- mark success updates status and next run
- mark failure updates error and next run

### Worker Tests

- worker runs a due schedule
- worker skips when nothing is due
- worker records failure and continues
- worker does not double-run leased schedule

### API/UI Tests

- create schedule
- edit schedule
- enable/disable schedule
- run now
- bad cron rejected
- bad time zone rejected
- last run status shown in UI

## Rollout Strategy

### Step 1

Build the new scheduling system without deleting the old jobs HTTP layer immediately.

### Step 2

Switch the Settings UI to the new schedule APIs and typed manual command path.

### Step 3

Verify the worker can successfully execute:

- Discover
- ProcessBatchAnalysis
- LinkAndDetect

### Step 4

Delete obsolete controller-based job launching.

### Step 5

Update docs and deployment assumptions.

This staged approach reduces risk while keeping cleanup close behind the new implementation.

## Risks and Mitigations

### Risk: Time zone confusion

Mitigation:

- store UTC only
- store original time zone ID
- preview next run in UI

### Risk: Duplicate execution

Mitigation:

- lease-based claiming in store
- worker only executes claimed schedules
- run-now checks active lease before executing

### Risk: Drift between manual and scheduled behavior

Mitigation:

- one shared dispatcher
- one set of typed request models
- one validation path

### Risk: `CodeGraph.Jobs` becomes hard to reference cleanly

Mitigation:

- keep jobs as orchestration and host code
- keep reusable business logic in `CodeGraph.Services`
- avoid circular dependencies

## Recommended First Implementation Slice

If you want the safest vertical slice, do this first:

1. Add `JobType`, `JobScheduleEntity`, `IJobScheduleStore`, and migration.
2. Implement `ProcessBatchAnalysis` as the first typed schedulable job.
3. Build the worker lease and due-schedule loop.
4. Add minimal Settings UI for:
   - list schedules
   - create `ProcessBatchAnalysis` schedule
   - run now
   - enable/disable
5. Once that works, add the other job types.

That gives you the core scheduling architecture with the lowest blast radius.

