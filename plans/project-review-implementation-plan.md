# Project Review Implementation Plan

## Status Snapshot

Last updated: 2026-04-09

Completed:

- Phase 1: storage and models
- Phase 2: diagnostics capture and persistence
- Phase 3: review orchestration/service layer
- Phase 4: API, including streaming
- Phase 5: repo-detail UI
- review runs now capture the commit SHA they were reviewed against

Remaining:

- Phase 6: broader validation and prompt tuning

## Recent Progress Notes

### 2026-04-09 — Phase 5 completed

Implemented the repo-detail UI integration for per-project reviews:

- each project card now loads the latest persisted review and diagnostics on page load
- `Generate Review` / `Re-run Review` actions now call the dedicated review API
- queued and running reviews stream live status updates from `GET /api/projects/{repo}/reviews/{id}/stream`
- completed reviews render overview, findings, strengths, reviewed areas, skipped areas, and follow-ups
- persisted diagnostics render summary badges plus a preview list of the highest-severity entries
- file-backed findings and diagnostics navigate into indexed source when possible

Validation and handoff notes:

- `npm run build` in `CodeGraphWeb/` passes
- repo-detail SCSS still exceeds the warning budget, but not the error budget; build remains green
- Phase 6 should focus on live UX validation against real review runs, prompt tuning, and any payload/wording adjustments discovered during manual usage

Suggested Phase 6 checklist:

- run at least a few real reviews across varied repo shapes, including one healthy project and one noisy/hotspot-heavy project
- verify the queued/running/completed/failed UI states against real backend timing, not only fast local success cases
- confirm the overview/findings wording is concise and useful enough to scan on the repo-detail page without feeling like a wall of text
- spot-check file navigation from findings and diagnostics against real indexed source locations
- review whether the current diagnostics preview limit and badge wording are the right level of detail
- tune prompts if findings skew too generic, too diagnostics-driven, or too verbose for the UI surface

### 2026-04-09 — Phase 4 completed

Implemented the dedicated project review API surface and server-side run lifecycle:

- `POST /api/projects/{repo}/reviews` now creates a queued review run and returns immediately
- review execution continues in-process via a background runner rather than holding the request open
- `GET /api/projects/{repo}/reviews/latest?projectName=...` returns the latest persisted review payload
- `GET /api/projects/{repo}/diagnostics?dotnetProject=...` returns structured persisted diagnostics plus aggregate counts
- `GET /api/projects/{repo}/reviews/{id}/stream` streams SSE events for `status`, `progress`, `finding`, `completed`, and `error`

This leaves Phase 5 as the repo-detail UI integration pass on top of the now-stable backend contract.

## Goal

Add a per-project "Generate Review" feature to CodeGraph that performs a deep, evidence-driven code review through the existing API and web UI, without requiring any installed local agent on the client machine.

The review should:

- Run server-side
- Inspect actual project source, not just summaries
- Use graph context, file metrics, security findings, and Roslyn/compiler/analyzer diagnostics as lead signals
- Treat diagnostics as review inputs, not automatic findings
- Return structured, evidence-backed findings with file and line references where possible
- Be persisted so the UI can show the latest review without forcing a rerun

## Why Diagnostics Are In V1

Roslyn/compiler/analyzer warnings should be part of v1, not a later add-on.

The repo already captures Roslyn diagnostics in the C# solution analysis path at [src/CodeGraph.Extractors.CSharp/SolutionAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Extractors.CSharp/SolutionAnalyzer.cs), but today they are reduced to per-file lint counts before reaching vitals. That is useful for health scoring, but insufficient for a review engine that needs the actual rule IDs, messages, severities, and source locations.

Including full diagnostics in v1 avoids a later storage and orchestration redesign.

## Product Shape

### UX

On the repo detail page, each project card should get a `Generate Review` action.

The UI should support:

- idle state
- queued/running state
- latest completed review view
- rerun action
- diagnostics summary badge
- clickable findings that navigate to indexed source when possible

Primary integration point:

- [CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.html](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.html)

### API

Add a dedicated review API rather than overloading the existing ask/chat surface.

Suggested endpoints:

- `POST /api/projects/{repo}/reviews`
- `GET /api/projects/{repo}/reviews/latest?projectName=...`
- `GET /api/projects/{repo}/reviews/{id}/stream`
- `GET /api/projects/{repo}/diagnostics?dotnetProject=...`

### Persistence

Persist:

- structured project diagnostics
- review runs
- review findings
- high-level review overview data

Persisting results lets the UI show the latest review and avoids rerunning expensive LLM work unnecessarily.

## Architectural Direction

This should be a dedicated vertical slice with four layers:

1. diagnostics capture and persistence
2. review orchestration
3. review API
4. repo-detail UI

This should not be implemented as a thin wrapper around the existing generic ask endpoint in [src/CodeGraph.Api/Controllers/AskController.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/Controllers/AskController.cs).

The backend may still use agent-like orchestration internally, but the public feature should be a purpose-built project review workflow.

## Review Principles

The review engine should prioritize:

1. likely bugs and behavioral risks
2. security and data-handling risks
3. reliability issues
4. maintainability and readability issues
5. design problems such as oversized classes and mixed responsibilities
6. dead code and unnecessary abstraction
7. test gaps around risky behavior

The system should actively look for:

- large classes doing too much
- long methods with multiple phases or responsibilities
- deep nesting and complex control flow
- duplicate logic
- poor naming that hides intent
- dead code and unused abstractions
- swallowed exceptions and weak failure handling
- fragile async, nullability, disposal, or state-management behavior
- code that should be split into smaller classes or extracted into clearer units
- missing tests for edge cases and failure paths

Every finding should be evidence-backed and should not be created solely because a diagnostic exists.

## Existing Seams To Reuse

### Backend

- [src/CodeGraph.Extractors.CSharp/SolutionAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Extractors.CSharp/SolutionAnalyzer.cs)
- [src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs)
- [src/CodeGraph.Services/Query/ProjectQueryService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Query/ProjectQueryService.cs)
- [src/CodeGraph.Data/IGraphStore.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/IGraphStore.cs)
- [src/CodeGraph.Data/IMetricsStore.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/IMetricsStore.cs)

### Frontend

- [CodeGraphWeb/src/app/core/api.service.ts](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/core/api.service.ts)
- [CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.ts](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.ts)
- [CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.html](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.html)

## Data Model

### New Store Interface

Add a focused store interface:

```csharp
// src/CodeGraph.Data/IReviewStore.cs
public interface IReviewStore
{
    Task DeleteProjectDiagnosticsAsync(string project);
    Task UpsertProjectDiagnosticsBatchAsync(string project, IReadOnlyList<ProjectDiagnosticEntity> diagnostics);
    Task<IReadOnlyList<ProjectDiagnosticEntity>> GetProjectDiagnosticsAsync(string project, string? dotnetProject = null);

    Task<long> CreateProjectReviewRunAsync(ProjectReviewRunEntity run);
    Task UpdateProjectReviewRunStatusAsync(long reviewRunId, string status, string? overviewJson = null, DateTime? completedAt = null, string? error = null);
    Task UpsertProjectReviewFindingsAsync(long reviewRunId, IReadOnlyList<ProjectReviewFindingEntity> findings);
    Task<ProjectReviewRunEntity?> GetLatestProjectReviewRunAsync(string project, string projectName);
    Task<IReadOnlyList<ProjectReviewFindingEntity>> GetProjectReviewFindingsAsync(long reviewRunId);
}
```

Then extend [src/CodeGraph.Data/IGraphStore.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/IGraphStore.cs) to include `IReviewStore`.

### New Entities

Add to [src/CodeGraph.Data/Entities.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/Entities.cs):

```csharp
public class ProjectDiagnosticEntity
{
    public string Project { get; set; } = "";
    public string? DotnetProject { get; set; }
    public string Source { get; set; } = "roslyn";
    public string DiagnosticKey { get; set; } = "";
    public string DiagnosticId { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Category { get; set; }
    public string FilePath { get; set; } = "";
    public int? LineStart { get; set; }
    public int? LineEnd { get; set; }
    public DateTime ComputedAt { get; set; }
}

public class ProjectReviewRunEntity
{
    public long Id { get; set; }
    public string Project { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string? ReviewedCommitSha { get; set; }
    public string Status { get; set; } = "queued";
    public string ReviewMode { get; set; } = "standard";
    public string PromptVersion { get; set; } = "v1";
    public string? OverviewJson { get; set; }
    public string? ModelUsed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public class ProjectReviewFindingEntity
{
    public long Id { get; set; }
    public long ReviewRunId { get; set; }
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

Note:

- `ReviewedCommitSha` was added during implementation so future incremental review-update flows can diff from the exact commit a review was based on.

## Diagnostics Capture Plan

### Current State

The C# indexing path already gathers Roslyn diagnostics in [src/CodeGraph.Extractors.CSharp/SolutionAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Extractors.CSharp/SolutionAnalyzer.cs), but it only stores aggregated counts in `LintResultCache`.

### Required Change

Add a sibling cache such as:

```csharp
// src/CodeGraph.Services/Analyzers/DiagnosticDetailCache.cs
public class DiagnosticDetailCache
{
    public void Set(string projectName, IReadOnlyList<ProjectDiagnosticEntity> diagnostics) { ... }
    public IReadOnlyList<ProjectDiagnosticEntity> Take(string projectName) { ... }
    public bool HasResults(string projectName) { ... }
}
```

Update `SolutionAnalyzer` to:

- keep the existing per-file error/warning counting behavior
- also capture full diagnostic detail for each source-backed Roslyn diagnostic
- include `DotnetProject`, `DiagnosticId`, `Severity`, `Message`, `Category`, `FilePath`, and line span

Update `VitalsAnalyzer` to:

- take diagnostics from `DiagnosticDetailCache`
- persist them through the store
- continue computing aggregate lint counts for file metrics

Diagnostics should be deleted and replaced when file metrics are recomputed for a repo.

## Review Orchestration

### New Service

Add a dedicated service:

- `src/CodeGraph.Services/Reviews/IProjectReviewService.cs`
- `src/CodeGraph.Services/Reviews/ProjectReviewService.cs`

Primary entry point:

```csharp
Task<long> StartReviewAsync(string repo, string projectName, string mode, CancellationToken ct);
```

### Review Workflow

The review should be multi-pass.

#### Pass 1: Inventory

Gather:

- project analysis summary
- node counts
- dotnet project file metrics
- hotspots
- security findings
- diagnostics
- candidate tests
- relevant graph relationships

#### Pass 2: Risk Queue Construction

Rank inspection targets using a combination of:

- diagnostic severity and count
- file risk score
- complexity
- file role
- class size or method size heuristics
- security findings

#### Pass 3: Source Inspection

Read actual source for the highest-value targets and inspect:

- suspicious classes
- large methods
- warning-heavy files
- high-risk files
- files adjacent to likely failures or smells

#### Pass 4: Deepening

For suspicious flows, follow:

- callers/callees
- related services/controllers
- related models or DTOs
- tests or absence of tests

#### Pass 5: Verification

Before persisting findings:

- reread cited files or nodes
- verify line references
- discard weak or duplicated findings
- ensure each finding is grounded in direct evidence

#### Pass 6: Final Synthesis

Return structured output with:

- overview
- findings
- strengths
- reviewed areas
- skipped areas
- follow-ups

## Prompting

Use two prompt builders rather than one:

- `ProjectReviewWorkflowPromptBuilder`
- `ProjectReviewSynthesisPromptBuilder`

### Workflow Prompt Responsibilities

- define the review rubric
- instruct the model to treat diagnostics as leads, not proof
- focus on bugs, security, maintainability, readability, dead code, and oversized classes
- tell the model to inspect source before concluding

### Synthesis Prompt Responsibilities

- transform verified notes into strict structured JSON
- enforce finding fields and output ordering
- minimize vague commentary

### Required Finding Fields

Each finding should include:

- severity
- category
- title
- explanation
- evidence
- file path
- line start
- line end
- suggested improvement
- confidence

## API Contract

### Start Review

`POST /api/projects/{repo}/reviews`

Request:

```json
{
  "projectName": "CodeGraph.Api",
  "mode": "standard"
}
```

Response:

```json
{
  "reviewRunId": 123,
  "status": "queued"
}
```

### Latest Review

`GET /api/projects/{repo}/reviews/latest?projectName=CodeGraph.Api`

Response shape:

```json
{
  "run": {
    "id": 123,
    "status": "completed",
    "project": "CodeGraph",
    "projectName": "CodeGraph.Api",
    "reviewedCommitSha": "abc123...",
    "createdAt": "2026-04-08T00:00:00Z",
    "completedAt": "2026-04-08T00:10:00Z",
    "modelUsed": "gpt-5"
  },
  "overview": "string",
  "findings": [],
  "strengths": [],
  "reviewedAreas": [],
  "skippedAreas": [],
  "followUps": []
}
```

### Streaming

`GET /api/projects/{repo}/reviews/{id}/stream`

Suggested SSE event types:

- `status`
- `progress`
- `finding`
- `completed`
- `error`

### Diagnostics

`GET /api/projects/{repo}/diagnostics?dotnetProject=CodeGraph.Api`

Return:

- structured diagnostics for the selected project
- aggregate counts by severity

## API Models

Add request and response models under:

- `src/CodeGraph.Models/Requests/`
- `src/CodeGraph.Models/Responses/`

Suggested response types:

```csharp
public record StartProjectReviewRequest(string ProjectName, string Mode = "standard");

public record StartProjectReviewResponse(long ReviewRunId, string Status);

public record ProjectReviewRunResponse(
    long Id,
    string Project,
    string ProjectName,
    string? ReviewedCommitSha,
    string Status,
    string ReviewMode,
    string PromptVersion,
    string? ModelUsed,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? Error);

public record ProjectReviewFindingResponse(
    string Severity,
    string Category,
    string Title,
    string Explanation,
    string Evidence,
    string FilePath,
    int? LineStart,
    int? LineEnd,
    string SuggestedImprovement,
    string Confidence);

public record ProjectReviewResponse(
    ProjectReviewRunResponse Run,
    string Overview,
    IReadOnlyList<ProjectReviewFindingResponse> Findings,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> ReviewedAreas,
    IReadOnlyList<string> SkippedAreas,
    IReadOnlyList<string> FollowUps);
```

## Controller Plan

Add:

- `src/CodeGraph.Api/Controllers/ProjectReviewsController.cs`

Do not overload [src/CodeGraph.Api/Controllers/ProjectsController.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/Controllers/ProjectsController.cs) beyond lightweight cross-links if needed.

The dedicated controller should handle:

- starting a review
- fetching the latest review
- streaming run progress
- returning diagnostics

## Neo4j Schema

Add a new migration file after [src/CodeGraph.Api/Migrations/001_schema.cypher](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/Migrations/001_schema.cypher), for example:

- `src/CodeGraph.Api/Migrations/00x_project_reviews.cypher`

Suggested schema additions:

- unique constraint on `ProjectReviewRun.id`
- index on `(project, projectName, createdAt)` for review runs
- index on `(project, projectName, reviewedCommitSha, createdAt)` for future review-update lookup
- unique constraint on `(project, diagnosticKey)` for diagnostics
- index on `(project, dotnetProject, severity)` for diagnostics
- index on `ProjectReviewFinding.reviewRunId`

## Configuration

Extend [src/CodeGraph.Services/Configuration/AnalysisOptions.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Configuration/AnalysisOptions.cs):

```csharp
public class ReviewOptions
{
    public string Model { get; set; } = "";
    public int MaxFilesToInspect { get; set; } = 25;
    public int MaxSourceCharsPerFile { get; set; } = 12000;
    public int MaxInspectionPasses { get; set; } = 4;
    public int MaxFindings { get; set; } = 20;
}
```

Then add:

```csharp
public ReviewOptions Review { get; set; } = new();
```

This keeps review-specific budgets out of the generic assistant configuration.

## Frontend Plan

### API Service

Extend [CodeGraphWeb/src/app/core/api.service.ts](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/core/api.service.ts) with:

- `startProjectReview(repo, projectName, mode)`
- `getLatestProjectReview(repo, projectName)`
- `getProjectDiagnostics(repo, projectName?)`
- optional `streamProjectReview(reviewRunId)`

### Repo Detail Page

Extend [CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.ts](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.ts) to track:

- latest review per project card
- run state
- diagnostics summary
- rerun state

Update [CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.html](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.html) to include:

- `Generate Review`
- running status
- latest review render
- finding cards
- diagnostics badge such as `2 errors · 14 warnings`

## Test Plan

Add coverage for:

### Diagnostics

- Roslyn diagnostics are captured with file and line detail
- diagnostics are persisted and queryable by repo and dotnet project
- file-level lint counts remain correct after detail capture is added

### Review Service

- diagnostics seed the inspection queue
- diagnostics do not automatically become findings
- large classes and long methods can still become findings without diagnostics
- final findings include file paths and evidence

### API

- start review endpoint
- latest review endpoint
- diagnostics endpoint
- streaming endpoint event shapes

### Frontend

- generate review action
- loading/running state
- latest review rendering
- diagnostics summary rendering

## Ordered Implementation Checklist

### Phase 1: Storage and Models

- [x] Add `IReviewStore`
- [x] Extend `IGraphStore`
- [x] Add `ProjectDiagnosticEntity`
- [x] Add `ProjectReviewRunEntity`
- [x] Add `ProjectReviewFindingEntity`
- [x] Add request/response models
- [x] Add Neo4j migration for diagnostics and reviews
- [x] Implement store methods in `Neo4jGraphStore`
- [x] Capture the commit SHA reviewed against on `ProjectReviewRunEntity`

### Phase 2: Diagnostics Persistence

- [x] Add `DiagnosticDetailCache`
- [x] Capture structured Roslyn diagnostics in `SolutionAnalyzer`
- [x] Preserve current aggregate `LintResultCache` behavior
- [x] Persist detailed diagnostics during vitals computation
- [x] Add store reads for diagnostics by repo and dotnet project

### Phase 3: Review Engine

- [x] Add `IProjectReviewService`
- [x] Add `ProjectReviewService`
- [x] Add review prompt builders
- [x] Add project-scoped evidence gathering helpers
- [x] Implement queue ranking from diagnostics, metrics, and security signals
- [x] Implement source inspection
- [x] Implement verification pass
- [x] Persist review results

### Phase 4: API

- [ ] Add `ProjectReviewsController`
- [ ] Add `POST /api/projects/{repo}/reviews`
- [ ] Add `GET /api/projects/{repo}/reviews/latest`
- [ ] Add `GET /api/projects/{repo}/reviews/{id}/stream`
- [ ] Add `GET /api/projects/{repo}/diagnostics`

### Phase 5: UI

- [ ] Extend Angular models
- [ ] Extend `ApiService`
- [ ] Add project review actions to repo detail page
- [ ] Add diagnostics summary badges
- [ ] Add review result rendering
- [ ] Add rerun flow

### Phase 6: Validation

- [~] Add backend unit tests
- [ ] Add backend integration tests
- [ ] Add frontend component tests
- [ ] Tune prompt wording based on first real review runs

## Key Decisions Locked In

- No installed client-side agent is required
- The feature is API-driven and server-hosted
- Diagnostics are part of v1
- Diagnostics are lead signals, not automatic findings
- Reviews are persisted
- The review workflow is dedicated, not a generic chat wrapper

## Follow-On Refinements After Initial Delivery

Once the v1 path is working end to end, refine:

- prompt/rubric tuning
- conventions-aware review overlays from the conventions wiki
- review depth modes such as `quick`, `standard`, and `deep`
- additional signals beyond Roslyn, such as test coverage heuristics or domain-specific analyzers
