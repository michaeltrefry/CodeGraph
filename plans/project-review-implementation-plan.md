# Repository Code Review Implementation Plan

## Status Snapshot

Last updated: 2026-04-09

Completed:

- Phase 1: storage and models for diagnostics and review persistence
- Phase 2: diagnostics capture and persistence
- Phase 3: project review orchestration and prompt builders
- Phase 4: background execution, API, and SSE streaming
- Phase 5: repo-detail UI integration for the first project-based pass
- Phase 6: repo-level persistence, contracts, and schema
- Phase 7: repo-level orchestration, API, and streaming
- Phase 8: repo-detail UI refactor to a single repository review section
- review runs now capture the commit SHA they were reviewed against

Scope correction now locked in:

- from the UI perspective, there is one code review per repository
- the previously built per-project review flow becomes an internal building block, not the primary product surface

Remaining:

- Phase 9: true incremental update reviews, changed-code blast radius, validation, and migration cleanup

Current repo-review behavior:

- repo-level review persistence is implemented
- repo-level API endpoints and SSE streaming are implemented
- repo-detail UI is repo scoped and supports `Run Code Review`, stale detection, and `Update Review`
- `Update Review` currently selects repo-review mode `update` and records baseline review metadata, but does not yet perform true diff-scoped review work
- current `RepositoryReviewService` still reruns project reviews across all projects using `ProjectReviewService.GenerateReviewAsync(..., "standard", ...)`

Current implementation files:

- backend orchestration: [RepositoryReviewService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Reviews/RepositoryReviewService.cs)
- reusable project review worker seam: [ProjectReviewService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Reviews/ProjectReviewService.cs)
- repo review API: [RepositoryReviewsController.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/Controllers/RepositoryReviewsController.cs)
- repo review persistence: [Neo4jGraphStore.Reviews.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data.Neo4j/Neo4jGraphStore.Reviews.cs)
- repo-detail UI: [repo-detail.component.ts](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.ts)
- repo-detail template: [repo-detail.component.html](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.html)

## Recent Progress Notes

### 2026-04-09 - Scope correction after UI integration

The original implementation plan assumed the review UX would be project based. During UI integration it became clear that the intended product shape was repo based instead:

- one review entry point per repository
- one persisted latest review per repository
- one repo-detail section between `Health` and `Projects`
- completed review output that starts with the overall repository review and then breaks down by project

This is still recoverable without throwing away the completed work. The current project review pipeline, diagnostics capture, background runner, streaming pattern, and commit-SHA persistence are all useful. The change is primarily a product-surface and orchestration refactor:

- repo review becomes the stored and displayed unit of work
- project reviews become internal sub-passes inside the repo review workflow
- incremental updates operate from the previous repo review baseline, not from isolated project reruns initiated by the user

### 2026-04-09 - Phase 6 completed

Implemented repo-level persistence and contracts:

- added `RepositoryReviewRunEntity`, `RepositoryReviewProjectSectionEntity`, and `RepositoryReviewFindingEntity`
- added repo-level request and response models
- extended `IReviewStore` with repo review operations
- implemented repo review storage in Neo4j and the in-memory test store
- added migration `010_repository_reviews.cypher`

Validation:

- `dotnet build CodeGraph.sln` passed

### 2026-04-09 - Phase 7 completed

Implemented the repo-level backend slice:

- added `IRepositoryReviewService` and `IRepositoryReviewBackgroundRunner`
- added `RepositoryReviewService` and `RepositoryReviewBackgroundRunner`
- reused `ProjectReviewService` through a new `GenerateReviewAsync(...)` seam so repo review can call project review passes without persisting separate project runs
- added `RepositoryReviewSynthesisPromptBuilder`
- added `RepositoryReviewsController` with start, latest, get-by-id, and SSE stream endpoints
- registered the new services in `Startup`
- added targeted service and controller tests

Validation:

- `dotnet build CodeGraph.sln` passed
- `dotnet test src/CodeGraph.Tests --filter "FullyQualifiedName~RepositoryReview"` passed

### 2026-04-09 - Phase 8 completed

Refactored the repo detail UI to the repo-level review experience:

- added repo-level frontend models and API service methods
- replaced per-project review state with one repo-level `Code Review` section between `Health` and `Projects`
- added stale detection from `reviewedCommitSha` versus current `lastCommitSha`
- added `Update Review` UI wiring
- rendered overall repo review first, then project-by-project sections
- kept per-project diagnostics drill-down inside the expanded repo review
- removed old project-card review controls from the `Projects` section

Validation:

- `npm run build` in `CodeGraphWeb/` passed
- `repo-detail.component.scss` still exceeds the warning budget, but no longer exceeds the error budget

## Goal

Add a repository-level `Code Review` feature to CodeGraph that performs a deep, evidence-driven review through the existing API and web UI, without requiring any installed local agent on the client machine.

The review should:

- run server-side
- inspect actual source, not just summaries
- use graph context, file metrics, security findings, and diagnostics as lead signals
- review each project deeply enough to support a trustworthy repo-wide conclusion
- persist a single repo-level review result that includes both overall and per-project output
- show the last reviewed date and the commit SHA reviewed against
- continue running if the user navigates away from the page
- support incremental review updates when the repo commit has advanced

## Product Shape

### UX

On the repo detail page, add a new `Code Review` section between `Health` and `Projects`.

If no review exists yet:

- show a `Run Code Review` button
- show a short explanation of what the review does

If a review is queued or running:

- show progress inline in the `Code Review` section
- keep the review running on the server if the user navigates away
- when the user returns, reload the latest repo review run and reconnect to streaming if it is still active

If a completed review exists:

- the section heading is `Code Review`
- the section can be expanded to view the full review
- the expanded view starts with the overall repo review
- the overall repo review is followed by project-by-project sections
- display the review date and the commit SHA reviewed against

If the review is stale:

- compare the review's `reviewedCommitSha` to the repository's current `lastCommitSha`
- show an `Update Review` action when they differ

If `Update Review` is clicked:

- perform an incremental review against the changed code
- use the previous repo review as context
- include surrounding code for changed areas
- include the blast radius of the changed code
- update the repo-level summary and any impacted project sections

Primary integration point:

- [repo-detail.component.html](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.html)

### API

Add a dedicated repo review API rather than driving the product from per-project endpoints.

Suggested endpoints:

- `POST /api/projects/{repo}/code-review`
- `GET /api/projects/{repo}/code-review/latest`
- `GET /api/projects/{repo}/code-review/{reviewRunId}`
- `GET /api/projects/{repo}/code-review/{reviewRunId}/stream`
- keep `GET /api/projects/{repo}/diagnostics` available for project-level evidence drill-down

Legacy project review endpoints may remain temporarily during migration, but they should no longer define the primary UI contract.

### Persistence

Persist:

- repo review runs
- repo-level overview data
- repo-level findings
- project-level review sections within a repo review
- incremental review metadata such as baseline review and baseline commit

Persisting results lets the UI show the latest review, reconnect to in-progress work, avoid unnecessary reruns, and support incremental updates.

## Architectural Direction

This should now be treated as a dedicated vertical slice with five layers:

1. diagnostics capture and persistence
2. project-level inspection primitives
3. repo review orchestration
4. repo review API and streaming
5. repo-detail UI

The important distinction is that project-level review logic is still useful, but it should sit behind a repo-level orchestrator.

The new repo review workflow should:

1. enumerate the repository's projects
2. gather repo-wide evidence and current commit context
3. run project-level review passes internally for each relevant project
4. synthesize a repo-wide conclusion across those project reviews
5. persist one repo review result that contains both overall and per-project output

This should not be implemented as a thin wrapper around the existing generic ask endpoint in [AskController.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/Controllers/AskController.cs).

## Review Principles

The review engine should prioritize:

1. likely bugs and behavioral risks
2. security and data-handling risks
3. reliability issues
4. maintainability and readability issues
5. design problems such as oversized classes and mixed responsibilities
6. dead code and unnecessary abstraction
7. test gaps around risky behavior
8. cross-project risks where the repo-level composition creates failure modes that project-local review would miss

Every finding should be evidence-backed and should not be created solely because a diagnostic exists.

## Why This Change Is Recoverable

The already-built project review work is still valuable:

- diagnostics persistence is still needed
- project-level evidence gathering is still needed
- background execution is still needed
- SSE streaming is still needed
- commit SHA capture is still needed

The implementation pivot is mostly about:

- changing the persisted review unit from project to repo
- moving the user entry point from project cards to one repo section
- adding repo-level synthesis over project-level review passes
- adding incremental update behavior at the repo level

## Existing Seams To Reuse

### Backend

- [ProjectReviewService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Reviews/ProjectReviewService.cs)
- [ProjectReviewBackgroundRunner.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Reviews/ProjectReviewBackgroundRunner.cs)
- [ProjectReviewsController.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/Controllers/ProjectReviewsController.cs)
- [SolutionAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Extractors.CSharp/SolutionAnalyzer.cs)
- [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs)
- [Neo4jGraphStore.Reviews.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data.Neo4j/Neo4jGraphStore.Reviews.cs)

### Frontend

- [api.service.ts](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/core/api.service.ts)
- [repo-detail.component.ts](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.ts)
- [repo-detail.component.html](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.html)

## Data Model

### Store Interface Direction

Extend the review store with repo-level review operations. Diagnostics can remain project scoped.

Suggested direction:

```csharp
public interface IReviewStore
{
    Task DeleteProjectDiagnosticsAsync(string project);
    Task UpsertProjectDiagnosticsBatchAsync(string project, IReadOnlyList<ProjectDiagnosticEntity> diagnostics);
    Task<IReadOnlyList<ProjectDiagnosticEntity>> GetProjectDiagnosticsAsync(string project, string? dotnetProject = null);

    Task<long> CreateRepositoryReviewRunAsync(RepositoryReviewRunEntity run);
    Task UpdateRepositoryReviewRunStatusAsync(long reviewRunId, string status, string? overviewJson = null, DateTime? completedAt = null, string? error = null);
    Task UpsertRepositoryReviewFindingsAsync(long reviewRunId, IReadOnlyList<RepositoryReviewFindingEntity> findings);
    Task UpsertRepositoryReviewProjectSectionsAsync(long reviewRunId, IReadOnlyList<RepositoryReviewProjectSectionEntity> sections);
    Task<RepositoryReviewRunEntity?> GetRepositoryReviewRunAsync(long reviewRunId);
    Task<RepositoryReviewRunEntity?> GetLatestRepositoryReviewRunAsync(string repo);
    Task<IReadOnlyList<RepositoryReviewFindingEntity>> GetRepositoryReviewFindingsAsync(long reviewRunId);
    Task<IReadOnlyList<RepositoryReviewProjectSectionEntity>> GetRepositoryReviewProjectSectionsAsync(long reviewRunId);
}
```

The existing project review entities and methods can be retained temporarily while the repo-level path is brought online, but the target steady state should be a repo-level persisted review model.

### Suggested Entities

```csharp
public class RepositoryReviewRunEntity
{
    public long Id { get; set; }
    public string Repo { get; set; } = "";
    public string? ReviewedCommitSha { get; set; }
    public long? BaselineReviewRunId { get; set; }
    public string? BaselineCommitSha { get; set; }
    public string Status { get; set; } = "queued";
    public string ReviewMode { get; set; } = "full";
    public string PromptVersion { get; set; } = "v1";
    public string? OverviewJson { get; set; }
    public string? ModelUsed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public class RepositoryReviewProjectSectionEntity
{
    public long Id { get; set; }
    public long ReviewRunId { get; set; }
    public string ProjectName { get; set; } = "";
    public string Overview { get; set; } = "";
    public string StrengthsJson { get; set; } = "[]";
    public string ReviewedAreasJson { get; set; } = "[]";
    public string SkippedAreasJson { get; set; } = "[]";
    public string FollowUpsJson { get; set; } = "[]";
    public bool ReusedFromBaseline { get; set; }
}

public class RepositoryReviewFindingEntity
{
    public long Id { get; set; }
    public long ReviewRunId { get; set; }
    public string? ProjectName { get; set; }
    public int Ordinal { get; set; }
    public string Severity { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int? LineStart { get; set; }
    public int? LineEnd { get; set; }
    public string SuggestedImprovement { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string? ProvenanceJson { get; set; }
}
```

Notes:

- `ProjectName` on a finding is optional so repo-wide findings can exist without being forced into a single project bucket
- `BaselineReviewRunId` and `BaselineCommitSha` support incremental update reviews
- `ReusedFromBaseline` lets the UI and future debugging distinguish freshly reviewed project sections from carried-forward ones

## Review Orchestration

### New Service Direction

Add a dedicated repo-level service:

- `src/CodeGraph.Services/Reviews/IRepositoryReviewService.cs`
- `src/CodeGraph.Services/Reviews/RepositoryReviewService.cs`

Primary entry point:

```csharp
Task<long> StartReviewAsync(string repo, string mode, CancellationToken ct);
```

The current project review service should be refactored into an internal collaborator or extracted worker that the repo review orchestrator can call project by project.

### Full Review Workflow

#### Pass 1: Repo Inventory

Gather:

- current repo commit SHA
- list of projects in the repo
- repo summary and node counts
- project analyses
- hotspots
- security findings
- diagnostics by project
- graph relationships across projects

#### Pass 2: Project Review Sub-Passes

For each project:

- build a ranked inspection queue from diagnostics, metrics, security, and graph context
- inspect actual source
- verify candidate findings
- produce a project-level structured result

These project results are internal workflow artifacts even if they are also persisted as project sections within the repo review.

#### Pass 3: Cross-Project Synthesis

Use the project results plus repo-wide context to produce:

- overall repo review overview
- repo-level strengths
- repo-level follow-ups
- cross-project findings
- normalized project section ordering

#### Pass 4: Final Verification

Before persisting:

- recheck cited file and line references
- dedupe overlapping findings across project and repo levels
- ensure each finding is grounded in direct evidence

### Incremental Update Workflow

If a prior completed repo review exists and its `reviewedCommitSha` differs from the current repo commit:

1. diff `baselineCommitSha -> currentCommitSha`
2. collect changed files
3. map changed files to impacted projects where possible
4. gather surrounding code for changed files
5. expand the inspection set via blast radius signals such as callers, callees, impacted models, routes, consumers, publishers, and related tests
6. load the prior repo review as context
7. rerun focused project review sub-passes only for impacted projects unless the diff is too large or ambiguous
8. resynthesize the overall repo review
9. carry forward unchanged project sections only when that is lower risk than rerunning them

Fallback rules:

- if there is no usable baseline review, run a full review
- if the diff is too broad, run a full review
- if blast-radius expansion reaches too much of the repo, run a full review

### Remaining Gap In Current Code

The current implementation only partially supports update mode:

- `RepositoryReviewRunEntity` stores `BaselineReviewRunId` and `BaselineCommitSha`
- the UI correctly offers `Update Review` when the review is stale
- `RepositoryReviewService.StartReviewAsync(...)` preserves baseline metadata when a safe completed baseline exists
- repo synthesis can receive the prior repo review as context

What is still missing:

- no actual `git diff baseline..head` file collection
- no mapping from changed files to impacted projects
- no surrounding-code expansion for changed files
- no blast-radius traversal from changed symbols/files
- no selective rerun of only impacted project sections
- no safe carry-forward of unchanged project sections
- no progress messages specific to the changed-code/update path
- no tests proving that update mode is actually narrower than a full rerun

## Phase 9 Design: True Changed-Code Plus Blast Radius Updates

### Desired Outcome

When `Update Review` is clicked and a safe completed baseline exists:

- review only the changed portions of the repository plus their meaningful blast radius
- preserve prior review context
- rerun only impacted project sections when safe
- resynthesize the full repo review with both refreshed and carried-forward context
- fall back to full review automatically when the update scope is too risky or too broad

### Suggested Architecture Additions

Add a dedicated update-planning layer inside [RepositoryReviewService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Reviews/RepositoryReviewService.cs), not inside the controller.

Suggested internal types:

```csharp
internal sealed record RepositoryReviewExecutionPlan(
    string Mode,
    string? BaselineCommitSha,
    string? CurrentCommitSha,
    bool UseFullReview,
    string? FullReviewReason,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> ImpactedProjects,
    IReadOnlyDictionary<string, ProjectReviewExecutionPlan> ProjectPlans);

internal sealed record ProjectReviewExecutionPlan(
    string ProjectName,
    bool ReuseBaselineSection,
    string? ReuseReason,
    IReadOnlyList<string> SeedFiles,
    IReadOnlyList<string> BlastRadiusFiles,
    IReadOnlyList<string> CandidateTests,
    string ReviewMode);
```

The repo service should build one execution plan first, then execute it.

### Step 1: Diff Collection

Implement a helper in [RepositoryReviewService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Reviews/RepositoryReviewService.cs) to diff:

- `baselineCommitSha -> currentCommitSha`

Recommended command:

- `git diff --name-only <baseline> <head>`

Recommended extra signal:

- `git diff --unified=0 <baseline> <head>` for changed hunks

Persisted repo review metadata already contains the baseline SHA. The repo service should:

1. resolve repo root
2. confirm both SHAs are available
3. collect changed files
4. normalize to repo-relative paths
5. drop deleted files from direct source inspection while still using them as blast-radius triggers when appropriate

Fallback to full review when:

- repo root is unavailable
- git diff fails
- baseline SHA is missing
- changed file count exceeds a configured threshold

### Step 2: Changed File Classification

Classify changed files before choosing project scope:

- source files
- tests
- config and dependency files
- generated files
- docs-only files

Suggested rules:

- ignore docs-only changes for code review reruns unless they alter generated code inputs
- treat `*.csproj`, `packages.lock.json`, `Directory.Build.*`, solution files, DI setup, startup wiring, and config files as potentially broad-impact changes
- treat deletions as broad-impact if the deleted symbol was referenced elsewhere
- treat changes in shared models/contracts as high blast-radius candidates

### Step 3: Map Changed Files To Projects

Use existing repo data to map changed files to projects:

- `StoredProjectAnalysis.ProjectName`
- node `DotnetProject`
- file metrics `DotnetProject`
- matching by file path from graph nodes

Suggested priority order:

1. direct `DotnetProject` from indexed file node
2. file metrics `dotnetProject`
3. nearest project by csproj path/root inference
4. mark as repo-shared if still ambiguous

Fallback to full review when:

- too many changed files cannot be mapped
- shared infra changes touch most projects

### Step 4: Surrounding Code Expansion

For each changed source file selected for direct inspection:

- load the file
- extract changed line spans from unified diff
- include a configurable window around each changed span, for example 40-80 lines
- if spans are dense, include the full file instead of many fragments

For update-mode prompts, pass:

- changed span snippets
- a small amount of leading and trailing surrounding code
- the full file only when the file is small or the edits are widespread

This likely requires a new prompt input shape for project review update mode rather than reusing the current full-file-only inspection prompt unchanged.

### Step 5: Blast Radius Expansion

Blast radius should use both graph and local-repo evidence.

Primary candidates:

- callers and callees of changed methods
- interfaces and implementations related to changed services/classes
- routes/controllers affected by changed services
- DTOs/models/events touched by changed contracts
- MassTransit consumers/publishers connected to changed event types
- tests directly targeting changed classes/files
- files with strong coupling to changed files from metrics/graph relationships

Suggested CodeGraph helpers to incorporate:

- trace call paths for changed methods where available
- find publishers and consumers for changed event/message types
- impact analysis for changed nodes/files where indexed coverage exists
- file metrics coupling and risk signals for neighboring files

Fallback heuristics when graph coverage is incomplete:

- same namespace
- same folder
- same basename test file pairing
- same interface or implementation naming convention

### Step 6: Impacted Project Selection

After changed-file and blast-radius expansion, determine project plans:

- rerun only impacted projects if the impacted set is clearly smaller than the whole repo
- carry forward untouched project sections from the baseline review
- mark carried-forward sections with `ReusedFromBaseline = true`

Fallback to full review when:

- impacted projects exceed a threshold such as 60-70% of repo projects
- blast-radius expansion becomes too large
- a shared infra change reasonably invalidates most baseline sections

### Step 7: Update-Aware Project Review Execution

The current [ProjectReviewService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Reviews/ProjectReviewService.cs) only exposes:

- `GenerateReviewAsync(repo, projectName, mode, ct)`

To support true update reviews well, add a more explicit execution seam, for example:

```csharp
Task<ProjectReviewResponse> GenerateReviewAsync(
    string repo,
    string projectName,
    ProjectReviewExecutionInput input,
    CancellationToken ct = default);
```

Suggested input fields:

- review mode: `full` or `update`
- changed files for this project
- changed snippets / surrounding snippets
- blast-radius files
- baseline project section if available
- update reason summary

This lets update-mode project review:

- focus on the changed area first
- still inspect related blast-radius files
- compare against prior project review context

### Step 8: Repo-Level Synthesis With Mixed Fresh And Reused Sections

Repo synthesis should explicitly know which project sections were:

- freshly rerun
- reused from baseline

The synthesis prompt should receive:

- update scope summary
- changed file list
- impacted project list
- rerun project sections
- reused baseline project sections
- prior repo review overview/findings

The model should be instructed to:

- refresh repo-level conclusions based on the rerun scope
- avoid overstating certainty for reused sections
- preserve still-valid findings only when they remain supported

### Step 9: Progress Reporting

The current stream emits only generic queued/running/completed progress.

Update-mode progress should add meaningful messages such as:

- `Collecting changed files from baseline commit`
- `Mapping changed files to impacted projects`
- `Expanding blast radius`
- `Reviewing impacted project CodeGraph.Api`
- `Reusing baseline section for CodeGraph.Jobs`
- `Synthesizing updated repository review`

If needed, add richer SSE event types later, but simple status/progress messages are enough for the next slice.

### Step 10: Validation Matrix

Add tests for:

- baseline exists and only one project is rerun
- changed shared contract forces multiple projects to rerun
- broad config or dependency change falls back to full review
- missing baseline SHA falls back to full review
- deleted file still affects impacted project selection
- carried-forward project sections are marked `ReusedFromBaseline = true`
- repo synthesis receives both rerun and reused sections
- update mode preserves baseline metadata on the new repo review run

Manual validation scenarios:

- one-file service change
- event/contract change
- startup/DI wiring change
- package upgrade
- docs-only change
- deletion of a central class

## Prompting

Use repo-level prompt builders in addition to the existing project-level review prompt builders.

Suggested prompt roles:

- `ProjectReviewWorkflowPromptBuilder`: inspect one project deeply and return structured project-review evidence
- `ProjectReviewSynthesisPromptBuilder`: normalize a single project review result
- `RepositoryReviewWorkflowPromptBuilder`: synthesize repo-wide conclusions from project-level results and repo-wide context
- `RepositoryReviewSynthesisPromptBuilder`: normalize the final repo response payload

The repo-level prompts should explicitly instruct the model to:

- treat project reviews as inputs, not unquestioned truth
- call out cross-project risks separately from project-local findings
- reuse prior repo review context during update runs without blindly carrying old findings forward

## API Contract

### Start Review

`POST /api/projects/{repo}/code-review`

Request:

```json
{
  "mode": "full"
}
```

Possible modes:

- `full`
- `update`

The server may still choose `full` when `update` is requested but no safe baseline exists.

Response:

```json
{
  "reviewRunId": 123,
  "status": "queued"
}
```

### Latest Review

`GET /api/projects/{repo}/code-review/latest`

Response shape:

```json
{
  "run": {
    "id": 123,
    "repo": "CodeGraph",
    "reviewedCommitSha": "abc123...",
    "baselineReviewRunId": 98,
    "baselineCommitSha": "def456...",
    "status": "completed",
    "reviewMode": "update",
    "createdAt": "2026-04-09T00:00:00Z",
    "completedAt": "2026-04-09T00:10:00Z",
    "modelUsed": "gpt-5"
  },
  "overview": "string",
  "findings": [],
  "strengths": [],
  "reviewedAreas": [],
  "skippedAreas": [],
  "followUps": [],
  "projectReviews": []
}
```

### Streaming

`GET /api/projects/{repo}/code-review/{reviewRunId}/stream`

Suggested SSE event types:

- `status`
- `progress`
- `projectStarted`
- `projectCompleted`
- `finding`
- `completed`
- `error`

Progress messages should be meaningful at the repo level, for example:

- `Enumerating projects and evidence`
- `Reviewing CodeGraph.Api`
- `Reviewing CodeGraph.Services`
- `Synthesizing repository review`

## API Models

Add request and response models under:

- `src/CodeGraph.Models/Requests/`
- `src/CodeGraph.Models/Responses/`

Suggested direction:

```csharp
public record StartRepositoryReviewRequest(string Mode = "full");

public record StartRepositoryReviewResponse(long ReviewRunId, string Status);

public record RepositoryReviewRunResponse(
    long Id,
    string Repo,
    string? ReviewedCommitSha,
    long? BaselineReviewRunId,
    string? BaselineCommitSha,
    string Status,
    string ReviewMode,
    string PromptVersion,
    string? ModelUsed,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? Error);

public record RepositoryReviewProjectSectionResponse(
    string ProjectName,
    string Overview,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> ReviewedAreas,
    IReadOnlyList<string> SkippedAreas,
    IReadOnlyList<string> FollowUps,
    IReadOnlyList<RepositoryReviewFindingResponse> Findings,
    bool ReusedFromBaseline);

public record RepositoryReviewFindingResponse(
    string Severity,
    string Category,
    string Title,
    string Explanation,
    string Evidence,
    string FilePath,
    int? LineStart,
    int? LineEnd,
    string SuggestedImprovement,
    string Confidence,
    string? ProjectName);

public record RepositoryReviewResponse(
    RepositoryReviewRunResponse Run,
    string Overview,
    IReadOnlyList<RepositoryReviewFindingResponse> Findings,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> ReviewedAreas,
    IReadOnlyList<string> SkippedAreas,
    IReadOnlyList<string> FollowUps,
    IReadOnlyList<RepositoryReviewProjectSectionResponse> ProjectReviews);
```

## Controller Plan

Add a dedicated controller for repo reviews, for example:

- `src/CodeGraph.Api/Controllers/RepositoryReviewsController.cs`

Responsibilities:

- start a repo review
- fetch the latest repo review
- fetch a specific repo review run
- stream repo review progress

Project diagnostics can remain on the existing diagnostics surface if that keeps the responsibilities cleaner.

## Neo4j Schema

Add a new migration for repo-level review nodes and indexes.

Suggested schema additions:

- unique constraint on `RepositoryReviewRun.appId`
- index on `(repo, createdAt)` for repo review runs
- index on `(repo, reviewedCommitSha, createdAt)` for stale-check and update lookup
- unique constraint on `RepositoryReviewProjectSection.appId`
- index on `(reviewRunId, projectName)` for project sections
- unique constraint on `RepositoryReviewFinding.appId`
- index on `(reviewRunId, ordinal)` for findings

Do not delete the current project review schema until the repo-level path is live and migrated.

## Frontend Plan

### API Service

Extend [api.service.ts](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/core/api.service.ts) with repo-level methods:

- `startRepositoryReview(repo, mode)`
- `getLatestRepositoryReview(repo)`
- `getRepositoryReview(repo, reviewRunId)`
- `streamRepositoryReview(repo, reviewRunId)`

Keep project diagnostics access for per-project evidence display inside the expanded repo review.

### Repo Detail Page

Refactor [repo-detail.component.ts](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.ts) and [repo-detail.component.html](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.html) so the review state is repo scoped, not project scoped.

Track:

- latest repo review
- active repo review run
- stale status based on commit mismatch
- expanded or collapsed state for the review section
- project subsection expansion state if needed

Update the template to include:

- one `Code Review` section between `Health` and `Projects`
- `Run Code Review` when no review exists
- `Update Review` when the latest completed review is stale
- live running status and progress copy
- repo-level overview and findings
- project-by-project review sections beneath the overall review
- review metadata such as reviewed date and commit SHA

The page should reconnect cleanly to an in-progress review after navigation by:

- loading the latest repo review on page load
- checking whether the latest run is `queued` or `running`
- reconnecting to the SSE stream for that run if it is still active

## Test Plan

Add coverage for:

### Review Service

- full repo review over multiple projects
- repo synthesis includes project outputs
- cross-project findings can be emitted without forcing a single project owner
- stale detection compares `reviewedCommitSha` to current repo commit
- update reviews load baseline review context
- update reviews scope work to changed files plus blast radius
- update-mode project execution can accept narrowed evidence instead of always doing a full project rerun
- update mode falls back to full review when no safe baseline exists

### API

- start repo review endpoint
- latest repo review endpoint
- repo review stream event shapes
- in-progress review retrieval and reconnect behavior

### Frontend

- `Code Review` section renders between `Health` and `Projects`
- no-review state shows `Run Code Review`
- stale review state shows `Update Review`
- running state renders progress
- completed state renders repo overview first, then project sections
- revisiting the page reconnects to an in-progress repo review

### Migration Safety

- repo-detail page no longer depends on project-by-project review state
- old project review data does not break the new repo review UI
- repo review persistence coexists with legacy project review persistence during rollout

## Ordered Implementation Checklist

### Phase 6: Repo-Level Persistence and Contracts

- [x] Add `RepositoryReviewRunEntity`
- [x] Add `RepositoryReviewProjectSectionEntity`
- [x] Add `RepositoryReviewFindingEntity`
- [x] Add repo-level request and response models
- [x] Extend `IReviewStore` with repo-level review methods
- [x] Implement repo-level store methods in `Neo4jGraphStore`
- [x] Add Neo4j migration for repo-level review storage

### Phase 7: Repo-Level Orchestration and API

- [x] Add `IRepositoryReviewService`
- [x] Add `RepositoryReviewService`
- [x] Extract or adapt the current project review engine into a reusable project-review worker
- [x] Implement repo-wide evidence gathering and project enumeration
- [x] Implement repo-level synthesis over project review outputs
- [x] Add repo review background execution
- [x] Add repo review controller and SSE stream

### Phase 8: Repo Detail UI Refactor

- [x] Replace project-card review controls with one repo-level `Code Review` section
- [x] Position the section between `Health` and `Projects`
- [x] Show `Run Code Review` when no review exists
- [x] Show live progress while a review is running
- [x] Render repo overview first, then project sections
- [x] Show reviewed date and reviewed commit SHA
- [x] Detect stale reviews and show `Update Review`
- [x] Reconnect to in-progress reviews after navigation

### Phase 9: Incremental Update Reviews and Validation

- [ ] Implement diffing from baseline reviewed commit to current commit
- [ ] Persist or pass changed hunk data for update-mode prompt inputs
- [ ] Classify changed files and fall back to full review for broad-impact changes
- [ ] Map changed files to impacted projects
- [ ] Gather surrounding code and blast radius for changed areas
- [ ] Build an execution plan that decides rerun-versus-reuse per project
- [ ] Add update-aware project review execution inputs beyond the current `GenerateReviewAsync(repo, projectName, mode, ...)`
- [ ] Feed prior repo review output in as update context
- [ ] Reuse unchanged project sections only when safe
- [ ] Mark reused project sections with `ReusedFromBaseline = true`
- [ ] Add richer update-mode progress messages to SSE
- [ ] Add backend unit and integration tests
- [ ] Add frontend component tests
- [ ] Remove or demote obsolete project-review UI/API paths once the repo-level flow is stable

## Handoff Notes

### What The Next Thread Should Assume

- repo-level review is the product surface and should stay that way
- the current backend and UI are working for full repo reviews
- `Update Review` is currently a UX and metadata scaffold, not yet a true diff-scoped review
- the best next starting point is Phase 9 inside [RepositoryReviewService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Reviews/RepositoryReviewService.cs)

### Best Next Starting Files

- [RepositoryReviewService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Reviews/RepositoryReviewService.cs)
- [ProjectReviewService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Reviews/ProjectReviewService.cs)
- [RepositoryReviewSynthesisPromptBuilder.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Reviews/RepositoryReviewSynthesisPromptBuilder.cs)
- [RepositoryReviewsController.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/Controllers/RepositoryReviewsController.cs)
- [RepositoryReviewServiceTests.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Tests/Services/RepositoryReviewServiceTests.cs)

### Safe First Slice For The Next Thread

1. Add diff collection and changed-file classification in `RepositoryReviewService`.
2. Add project-impact mapping and full-review fallback rules.
3. Introduce an internal execution-plan object and test it thoroughly.
4. Only then extend `ProjectReviewService` with update-aware narrowed evidence inputs.

## Key Decisions Locked In

- the product surface is a repo-level code review, not a project-by-project review UI
- project review passes are an implementation detail inside the repo review workflow
- the feature remains API-driven and server-hosted
- diagnostics remain lead signals, not automatic findings
- reviews are persisted
- review runs capture the commit SHA they were reviewed against
- stale review detection is based on commit mismatch
- update reviews are incremental when safe and full when necessary
- background review execution must continue if the user navigates away

## Follow-On Refinements After Initial Delivery

Once the repo-level path is working end to end, refine:

- prompt and rubric tuning based on real usage
- conventions-aware overlays from the conventions wiki
- review depth modes such as `quick`, `standard`, and `deep`
- richer progress messages for long-running repo reviews
- side-by-side diff views between baseline and updated reviews
- additional review signals beyond Roslyn, such as test coverage heuristics or domain-specific analyzers
