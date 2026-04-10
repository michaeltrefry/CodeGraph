# Health Vitals Solo Implementation Plan

## Status Snapshot

Last updated: 2026-04-09

Depends on:

- [health-vitals-policy-plan.md](/Users/michael/Repos/CodeGraph/plans/health-vitals-policy-plan.md)

Scope locked for this repo:

- implement the Solo-only health vitals upgrade in this repository
- do not add runtime switching between `Solo` and `Team`
- do not build fork-oriented abstractions unless they directly simplify the Solo implementation
- keep `OverallHealth` stable in the first rollout
- make `ConcernScore` the new hotspot-ranking signal
- keep repo vitality informational in the first release

Primary delivery outcome:

- hotspot ordering better reflects repeated personal maintenance drag
- repo health surfaces long-history vitality context without destabilizing the existing score model

## Current Code Seams

The current implementation already gives us clean extension points:

- analyzer entry point: [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs)
- file and summary entities: [Entities.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/Entities.cs)
- metrics store contract: [IMetricsStore.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/IMetricsStore.cs)
- Neo4j persistence: [Neo4jGraphStore.Metrics.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data.Neo4j/Neo4jGraphStore.Metrics.cs)
- API/query mapping: [ProjectQueryService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Query/ProjectQueryService.cs)
- MCP formatting: [CodeGraphMcpServer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Assistant/CodeGraphMcpServer.cs)
- repo detail UI state: [repo-detail.component.ts](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.ts)
- analyzer tests: [VitalsAnalyzerTests.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Tests/Services/VitalsAnalyzerTests.cs)

## Implementation Principles

### Principle 1: Build from One History Pass

All new git-history-derived signals should come from one normalized history reader inside the analyzer.

That single pass should feed:

- file churn windows
- weighted bug/fix attribution
- repo monthly activity buckets
- firefighting metrics
- repo age and maturity classification

### Principle 2: Persist Raw-ish Solo Data, Not Profile Machinery

For this repository, persistence should capture:

- weighted file history metrics
- `ConcernScore`
- repo vitality summary fields

It should not capture:

- profile variants
- alternate ranking outputs for another product
- team-mode switches

### Principle 3: Keep the First Slice Vertical

The first implementation should fully light up a narrow but real path:

1. analyzer computes new metrics
2. store persists them
3. query layer exposes them
4. MCP and UI show them
5. tests lock behavior down

## Proposed Delivery Phases

## Phase 1: Data Contracts and Store Shape

Goal:

- extend the data model so the rest of the stack has stable contracts

Files:

- [Entities.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/Entities.cs)
- [IMetricsStore.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/IMetricsStore.cs)
- [ProjectHealthResponse.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Models/Responses/ProjectHealthResponse.cs)

Changes:

- extend `FileMetricsEntity` with:
  - `BugFixCommits90d`
  - `BugFixCommits365d`
  - `BugFixRatio365d`
  - `BugFixWeightedTouches365d`
  - `Churn30d`
  - `Churn90d`
  - `Churn365d`
  - `RecurringChurnScore`
  - `ConcernScore`
- extend `ProjectHealthSummaryEntity` with:
  - `HistoryMaturity`
  - `HasSufficientHistoryForTrends`
  - `ActivityStatus`
  - `FirefightingStatus`
  - `MonthlyCommitCounts`
  - `VelocityLast6Months`
  - `VelocityPrior6Months`
  - `VelocityChangePercent`
  - `DormantMonths12m`
  - `MaxInactiveStreakMonths`
  - `FirefightingCommits90d`
  - `FirefightingCommits365d`
  - `FirefightingRate90d`
  - `FirefightingRate365d`
- add response-model equivalents to `ProjectHealthResponse`
- add `RepositoryVitalitySummary` to the API response
- add `HistoryMaturity` enum in the models layer

Open choice to settle during implementation:

- keep vitality on `ProjectHealthSummaryEntity` for the first cut unless it starts to make the mapping layer messy

Definition of done:

- the models compile cleanly
- no service logic depends on placeholder strings for maturity or vitality fields
- response contracts clearly separate file-level metrics from repo vitality

## Phase 2: Neo4j Persistence and Mapping

Goal:

- persist the new health telemetry cleanly and retrieve it without ambiguity

Files:

- [Neo4jGraphStore.Metrics.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data.Neo4j/Neo4jGraphStore.Metrics.cs)
- [src/CodeGraph.Api/Migrations](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/Migrations)

Changes:

- update file-metric upsert payloads with all new fields
- update `GetHotspotsAsync(...)` ordering to use `concernScore` instead of `riskScore`
- map new node properties in `MapFileMetricsNode(...)`
- persist vitality fields on `ProjectHealthSummary`
- map vitality fields in `MapHealthSummaryNode(...)`
- add a migration to backfill or safely introduce the new summary properties if needed

Implementation notes:

- if old nodes do not have new properties yet, mappings should default safely
- preserve compatibility with existing repositories that already have health data

Definition of done:

- new file metrics round-trip through Neo4j
- `GetHotspotsAsync(...)` returns files ordered by `ConcernScore`
- repo summaries round-trip vitality fields without null-handling bugs

## Phase 3: Internal Analyzer History Model

Goal:

- introduce a single normalized git-history reader before changing ranking logic

Files:

- [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs)
- optionally new helper file(s) under [src/CodeGraph.Services/Analyzers](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers)

Recommended new internal types:

- `HistoryMaturity`
- `CommitClassification`
- `NormalizedCommitRecord`
- `NormalizedFileTouch`
- `FileHistoryMetrics`
- `RepositoryVitalityComputation`

Changes:

- add a new history reader that parses commit date, author, message, and touched source files
- classify each commit as:
  - normal
  - bug/fix
  - firefighting
- compute weighted file-touch contributions based on touched source file count
- compute weighted file-touch contributions based on changed lines, with equal-share fallback when needed
- accumulate:
  - 30d / 90d / 365d weighted churn
  - weighted bug/fix counts
  - monthly repo commit buckets
  - repo firefighting counts
  - repo age and commit-count maturity inputs

Do not do yet:

- prompt changes
- UI changes
- broad scoring refactors outside the new metrics path

Definition of done:

- analyzer can produce normalized history metrics from one git pass
- file-level and repo-level history outputs are both sourced from the same commit stream
- old `ComputeChurnAsync(...)` is either retired or clearly scoped to legacy usage

## Phase 4: Solo Policy and `ConcernScore`

Goal:

- translate raw telemetry into a solo-maintenance ranking signal

Files:

- [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs)
- optionally new file(s) under [src/CodeGraph.Services/Analyzers](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers)

Changes:

- add `SoloHealthPolicy` or equivalent internal helper
- implement:
  - `DetermineHistoryMaturity(...)`
  - `ComputeRecurringChurnScore(...)`
  - `ComputeConcernScore(...)`
  - `DetermineActivityStatus(...)`
  - `DetermineFirefightingStatus(...)`
- keep `RiskScore` intact for compatibility
- make `ConcernScore` the preferred ordering signal for hotspots

Concrete rules for the first cut:

- `ConcernScore` must have a non-zero floor for structurally unhealthy files
- weighted bug/fix history should materially influence ranking even if 90-day churn is light
- firefighting stays repo-level and informational

Definition of done:

- analyzer persists both old score data and new `ConcernScore`
- hotspot ranking changes in a way that matches the Solo policy
- repo summary includes maturity and vitality labels

## Phase 5: Health Summary Aggregation and Prompt Updates

Goal:

- include the new solo signals in both structured summaries and generated prose

Files:

- [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs)

Changes:

- update summary aggregation so repo-level summaries store vitality fields
- ensure top-hotspot summary JSON prefers `ConcernScore`
- update health prompts to mention:
  - repeated-fix hotspots
  - persistent maintenance drag
  - trend immaturity when applicable
  - repo vitality as informational context

Prompt wording target:

- emphasize "repeated fix areas"
- emphasize "persistent work friction"
- avoid team-dynamics language

Definition of done:

- repo analysis prompt receives vitality context
- file/project analysis prompt receives recurring-churn and bug/fix signals
- generated health prose stays Solo-oriented

## Phase 6: Query Layer and API Response Wiring

Goal:

- expose the new metrics to the web app and MCP tools cleanly

Files:

- [ProjectQueryService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Query/ProjectQueryService.cs)
- [GraphQueryEngine.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Query/GraphQueryEngine.cs) if needed
- [ProjectHealthResponse.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Models/Responses/ProjectHealthResponse.cs)

Changes:

- update `MapFileMetrics(...)` for new properties
- update `MapHealthSummary(...)` for maturity/vitality fields
- include `RepositoryVitalitySummary` in `GetHealthAsync(...)`
- ensure hotspots are sorted by `ConcernScore`

Definition of done:

- `GET /api/projects/{repo}/health` includes the new file-level and repo-level fields
- existing callers continue to work when vitality data is absent
- top hotspots align with `ConcernScore`

## Phase 7: MCP and Assistant Output

Goal:

- surface the new health model in text tools without overwhelming the user

Files:

- [CodeGraphMcpServer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Assistant/CodeGraphMcpServer.cs)
- [GraphAssistant.Tools.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Assistant/GraphAssistant.Tools.cs)

Changes:

- update `get_project_health` output to show:
  - `ConcernScore` for hotspots
  - bug/fix and recurring-churn context
  - repo vitality labels
  - maturity/insufficient-history notes
- keep the output compact enough for tool responses

Definition of done:

- MCP health output matches the Solo policy language
- no team-mode terminology leaks into this repo's tool output

## Phase 8: Repo Detail UI

Goal:

- make the new signals visible in the existing health section without a major layout rewrite

Files:

- [repo-detail.component.ts](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.ts)
- [repo-detail.component.html](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.html)
- [repo-detail.component.scss](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.scss)
- frontend models under [CodeGraphWeb/src/app/core/models.ts](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/core/models.ts)

Changes:

- extend frontend health models with vitality and `ConcernScore`
- add a `Project Vitality` card near the top of the health area
- add a minimal monthly-commit chart
- update hotspot rendering to prefer:
  - `ConcernScore`
  - repeated-fix indicators
  - recurring churn indicators
- add empty-state messaging for immature history

Recommended first-pass UI shape:

- reuse the existing health panel
- avoid introducing a brand-new route or tab
- keep the chart simple, likely SVG or bars

Definition of done:

- a user can see why a hotspot ranks high without reading raw JSON
- vitality is visible but clearly secondary to health/hotspot ranking
- empty or young-history repos render gracefully

## Phase 9: Test Coverage

Goal:

- lock down the new scoring and history behavior before tuning

Files:

- [VitalsAnalyzerTests.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Tests/Services/VitalsAnalyzerTests.cs)
- new targeted tests under [src/CodeGraph.Tests](/Users/michael/Repos/CodeGraph/src/CodeGraph.Tests)
- Angular tests near the repo detail component if coverage already exists there

Add tests for:

- bug/fix keyword classification
- firefighting keyword classification
- weighted file attribution for multi-file fix commits
- history maturity classification
- recurring churn scoring
- `ConcernScore` floor behavior when recent churn is low
- hotspot sorting by `ConcernScore`
- vitality-status derivation
- API/query mapping for new fields
- repo-detail rendering for vitality and immature-history states

Recommended test strategy:

- start with analyzer unit tests because that logic is the hardest to verify manually
- add mapping tests next
- finish with one or two UI tests for rendering and empty-state behavior

Definition of done:

- analyzer behavior is covered by deterministic tests
- query mappings catch missing-property regressions
- UI handles both rich-history and low-history repos

## Recommended Execution Order

Use this order to minimize rework:

1. Phase 1: contracts and response shapes
2. Phase 2: Neo4j persistence
3. Phase 3: normalized history reader
4. Phase 4: scoring and vitality rules
5. Phase 9 analyzer tests for the new logic
6. Phase 5 prompt and summary updates
7. Phase 6 query/API mapping
8. Phase 7 MCP formatting
9. Phase 8 UI
10. final test pass and calibration

Why this order:

- the contracts need to settle before analyzer and query work
- the history reader needs to exist before scoring can be trusted
- testing the analyzer before UI work gives faster feedback and safer tuning

## First Coding Slice

If we want the narrowest valuable first implementation, do this first:

1. extend `FileMetricsEntity`, `ProjectHealthSummaryEntity`, and `ProjectHealthResponse`
2. update Neo4j metric persistence and mapping
3. add the normalized history reader inside `VitalsAnalyzer`
4. compute and persist:
   - `BugFixCommits365d`
   - `BugFixRatio365d`
   - `Churn30d`
   - `Churn90d`
   - `Churn365d`
   - `RecurringChurnScore`
   - `ConcernScore`
5. switch hotspot ordering from `RiskScore` to `ConcernScore`
6. add analyzer and mapping tests

This slice alone would already improve hotspot ranking meaningfully before the UI chart and repo vitality card land.

## Validation Plan

After each major phase:

- run `dotnet build CodeGraph.sln`

After analyzer and persistence changes:

- run `dotnet test src/CodeGraph.Tests --filter "FullyQualifiedName~VitalsAnalyzer"`

After query and API mapping changes:

- run targeted tests for query services if added

After frontend changes:

- run `npm run build` in [CodeGraphWeb](/Users/michael/Repos/CodeGraph/CodeGraphWeb)

Before considering the feature complete:

- run `dotnet test CodeGraph.sln`
- manually inspect one mature repo and one young repo in the UI
- verify that hotspot order changed for understandable reasons

## Explicit Non-Goals for This Plan

Do not include these in the first implementation:

- team-mode scoring
- runtime switching between policy packs
- separate persistence for alternate policy outputs
- ML-based commit-message classification
- cross-repo vitality rollups
- rewriting `OverallHealth`

## Handoff Notes

When implementation starts, the first code touch should be in the contracts and persistence layer, not the UI.

The riskiest part of the feature is not the chart or wording. It is getting the normalized history model, weighted bug/fix attribution, and `ConcernScore` behavior correct without making the analyzer slow or fragile.
