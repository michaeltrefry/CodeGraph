# Code Review Findings

Date: 2026-04-03
Repository: `TC.CodeGraphApi`

## Findings

### 1. [P1] Repo semaphore can release the wrong instance after config changes

Affected file:
- `src/TC.CodeGraphApi.Services/ProjectService.cs`

Why it matters:
- `ProcessRepository` waits on `RepoSemaphore`, but the `finally` block calls the property again instead of releasing the same instance.
- If `IndexingOptions.MaxParallelRepos` changes while work is in flight, the getter can create a new semaphore and `Release()` will target the wrong object.

Likely impact:
- Permit leaks, incorrect concurrency limits, or runtime exceptions under config changes.

Suggested direction:
- Cache the semaphore in a local variable before `WaitAsync` and release that exact instance in `finally`.
- Add a regression test that changes the configured max between acquire and release.

### 2. [P2] Job orchestration had no test coverage despite an existing test project

Affected file:
- `src/TC.CodeGraphJobs.Tests/TC.CodeGraphJobs.Tests.csproj`

Why it matters:
- The repository already had a dedicated jobs test project, but it contained no authored test files.
- That left job entrypoints and argument parsing unguarded.

Likely impact:
- Regressions in repo parsing, URL/path branching, and job forwarding could slip through without focused tests.

Suggested direction:
- Add focused unit tests for:
  - `ProcessRepositoriesJob` argument parsing and message publishing
  - `ProcessBatchResultsJob` repo forwarding
  - `DiscoverRepositoriesJob` success/failure behavior

Status:
- Corrected. Focused job unit tests have now been added and are passing.

### 3. [P2] DiscoverRepositoriesJob is implemented but was not registered/exposed

Affected files:
- `src/TC.CodeGraphJobs/Jobs/DiscoverRepositoriesJob.cs`
- `src/TC.CodeGraphJobs/Startup.cs`
- `src/TC.CodeGraphJobs/Controllers/JobsController.cs`

Why it matters:
- The job implementation appears intentional, but it was not wired into the runnable jobs surface.
- This is a registration/exposure gap, not proven dead code.

Likely impact:
- The job cannot be invoked through the normal jobs host path even though its implementation exists.

Suggested direction:
- Register the job in startup and expose an endpoint/controller action for it alongside the other jobs.

Status:
- Corrected. The job has now been registered in startup and exposed through the jobs controller.

### 4. [P2] Search path scales by loading the full repo catalog on every query

Affected files:
- `src/TC.CodeGraphApi.Services/Query/SearchService.cs`
- `src/TC.CodeGraphApi.Services/Query/ProjectQueryService.cs`

Why it matters:
- `SearchService.SearchAsync()` loads all repositories and filters in memory.
- `ProjectQueryService.ListAsync()` also loads the full repository list before filtering and paging.

Likely impact:
- As repository count grows, these endpoints become full-scan paths and spend more time enumerating everything than serving the requested page.

Suggested direction:
- Push filtering and paging down into the store layer, or add targeted repository-name queries instead of full enumeration.

## Open Questions / Assumptions

- The semaphore issue assumes `MaxParallelRepos` can change at runtime. If that setting is immutable for process lifetime, the risk is lower, but the implementation is still fragile.
- The jobs test coverage finding was based on repository file search results at the time of review.

## Brief Assessment

The main issues from the review were concentrated in orchestration and scalability rather than broad correctness problems: one real concurrency hazard, one missing job registration, one search/query scaling concern, and a clear testing gap in the jobs layer.

## Corrected Since Review

- `DiscoverRepositoriesJob` registration/exposure has been fixed.
- Jobs unit test coverage has been added for the previously untested job entrypoints, and those tests are now passing.
- The remaining open items from this review are the `ProjectService` semaphore behavior and the query/search full-scan performance pattern.
