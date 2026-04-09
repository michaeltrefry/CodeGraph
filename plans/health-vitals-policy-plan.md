# Health Vitals Solo Plan

## Status Snapshot

Last updated: 2026-04-09

Purpose:

- define the next evolution of CodeGraph health metrics for this repository's solo-developer product
- improve hotspot ranking around repeated personal maintenance pain
- add repository vitality signals without destabilizing the existing health score

Current scope:

- this repository implements `Solo` health policy only
- there is no runtime switching between `Solo` and `Team` in this codebase
- any future `Team` policy belongs to the separate fork and should be planned there, not generalized into this repo prematurely

Current state in this repo:

- file-level vitals-style metrics already exist in [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs)
- the current scoring model emphasizes complexity, churn, coupling, knowledge risk, lint, trust, and `RiskScore`
- the repo detail UI already exposes top hotspots in [repo-detail.component.html](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.html)
- no long-history churn persistence model exists yet
- no bug/fix factor exists yet
- no repo-level monthly activity or firefighting trend model exists yet

Locked decisions:

- this implementation is `Solo` only
- `OverallHealth` stays stable in the first rollout
- new urgency should come from a stronger ranking signal rather than from rewriting `OverallHealth`
- repo vitality is informational in the first rollout
- raw telemetry is persisted once; solo-specific ranking and wording may be derived from that telemetry

## Goal

Extend CodeGraph health analysis so it answers this question better:

- "what parts of this codebase keep costing me time?"

This plan introduces:

- git-history maturity gating
- bug/fix factor for files
- persistent churn and recurring-change signals
- a solo-oriented ranking signal for "look here first"
- repository vitality signals such as activity trend, dormancy, and firefighting rate
- a monthly commit activity chart for repo health

## Product Principles

### Optimize for Solo Maintenance Drag

The health model should prioritize repeated personal cost:

- files that keep getting touched over and over
- files that repeatedly receive fixes
- files whose complexity and coupling make each revisit expensive

Signals that mainly describe team dynamics should not drive this repo's ranking model.

### Stable Health Score, Stronger Ranking

`OverallHealth` should remain explainable and relatively stable.

The new "this needs attention now" behavior should come from a ranking metric. The current `RiskScore` is the right starting point, but we should evolve it into a more explicit `ConcernScore` for the solo experience.

### Maturity-Gated History Signals

Long-history heuristics should be explicit about confidence.

If repo history is too young or too sparse, CodeGraph should say that trend signals are immature rather than silently omitting them or pretending confidence.

### Single Telemetry Pass

Git-history-derived telemetry should be collected in one normalized pass where practical.

The analyzer should not perform separate full-history scans for churn windows, bug/fix classification, firefighting, monthly activity, and maturity when the same commit stream can feed all of them.

## Shared Model for the Solo Plan

### New Policy Inputs

Introduce one maturity dimension for this repo:

- `HistoryMaturity`
  - `Young`
  - `Growing`
  - `Mature`

Suggested initial thresholds:

- `Young`
  - repository age under 6 months, or
  - fewer than 100 commits
- `Growing`
  - not `Young`, and
  - repository age under 12 months or fewer than 300 commits
- `Mature`
  - everything else

This ordering is intentional so every repository maps to exactly one bucket.

These thresholds should live in configuration rather than hard-coded constants.

### New File-Level Metrics

Add the following to [Entities.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/Entities.cs) and flow them through [ProjectHealthResponse.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Models/Responses/ProjectHealthResponse.cs):

- `BugFixCommits90d`
- `BugFixCommits365d`
- `BugFixRatio365d`
- `BugFixWeightedTouches365d`
- `Churn30d`
- `Churn90d`
- `Churn365d`
- `RecurringChurnScore`
- `ConcernScore`

Definitions:

- `BugFixCommits90d`: weighted count of bug/fix commits touching the file in the last 90 days
- `BugFixCommits365d`: weighted count of bug/fix commits touching the file in the last 365 days
- `BugFixRatio365d`: `BugFixCommits365d / TotalWeightedTouches365d`
- `BugFixWeightedTouches365d`: weighted total commit touches for denominator stability
- `Churn30d`, `Churn90d`, `Churn365d`: weighted commit-touch counts in those windows
- `RecurringChurnScore`: a persistence metric that rewards repeated activity across multiple windows rather than one burst
- `ConcernScore`: the primary solo ranking score for "surface this first"

Normalization rule:

- classify commits at commit level, then attribute them to touched files using weighted contribution
- default weighting: distribute the commit's contribution across touched source files in proportion to changed lines
- fallback weighting: if changed-line totals are unavailable or zero, divide contribution equally by touched source file count
- if a commit touches no source files, it should not affect file metrics

This avoids over-penalizing broad hotfixes, sweeping reverts, and mechanical fix commits while still letting the files that absorbed most of the change carry most of the signal.

### New Repo-Level Vitality Metrics

Add the following to [ProjectHealthSummaryEntity](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/Entities.cs#L191) or to a dedicated `RepositoryVitalitySummaryEntity` if that keeps the core health entity cleaner:

- `MonthlyCommitCounts`
- `ActivityStatus`
- `VelocityLast6Months`
- `VelocityPrior6Months`
- `VelocityChangePercent`
- `DormantMonths12m`
- `MaxInactiveStreakMonths`
- `FirefightingCommits90d`
- `FirefightingCommits365d`
- `FirefightingRate90d`
- `FirefightingRate365d`
- `HasSufficientHistoryForTrends`

Definitions:

- `MonthlyCommitCounts`: JSON array of month buckets for charting
- `ActivityStatus`: `Active`, `Stable`, `Slowing`, `Dormant`, `Revived`, or `PossiblyAbandoned`
- `VelocityLast6Months`: total commits in the most recent 6 full months
- `VelocityPrior6Months`: total commits in the 6 months before that
- `VelocityChangePercent`: percent change between those windows
- `DormantMonths12m`: count of zero-commit months in the last 12 months
- `MaxInactiveStreakMonths`: longest recent run of zero-commit months
- `FirefightingCommits90d` and `365d`: repo-level commits classified as firefighting
- `FirefightingRate90d` and `365d`: firefighting commits divided by total commits in those windows

## Signal Classification Rules

### Bug/Fix Classification

Start with keyword classification over commit messages.

Initial bug/fix keywords:

- `fix`
- `bug`
- `broken`
- `defect`
- `patch`

Notes:

- use case-insensitive whole-word or token-boundary matching
- keep the keyword set configurable
- keep commit-level classification simple in the first pass and improve with real repo data if needed

### Firefighting Classification

Initial firefighting keywords:

- `hotfix`
- `incident`
- `urgent`
- `emergency`
- `rollback`
- `revert`

Notes:

- firefighting is a repo vitality signal first, not a file-quality signal
- firefighting should be summarized at the repo level and not directly inflate file ranking on its own

### Recurring Churn

`RecurringChurnScore` should not be a simple sum of recent changes.

Recommended first-pass heuristic:

- compute weighted file touch counts in 30d, 90d, and 365d windows
- score higher when the file shows meaningful activity in all three windows
- score lower when activity is concentrated in just one short period

One simple initial formula:

```text
normalized30 = min(churn30d / 10, 1.0)
normalized90 = min(churn90d / 25, 1.0)
normalized365 = min(churn365d / 100, 1.0)
RecurringChurnScore = round((0.2 * normalized30) + (0.3 * normalized90) + (0.5 * normalized365), 2)
```

The point is persistence, not raw volume.

## Scoring Strategy

### Preserve `HealthScore`

The current `HealthScore` in [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs#L860) remains the baseline code-health score in the first rollout.

Do not immediately mix in every new signal. That would make the score harder to understand and harder to compare over time.

### Add `ConcernScore`

Add a ranking metric specifically for "surface this first."

Recommended direction:

- keep `RiskScore` for compatibility during rollout
- add `ConcernScore` as the preferred ranking metric in responses and UI
- derive `ConcernScore` from persisted raw telemetry rather than storing separate policy variants

Important constraint:

- `ConcernScore` must not collapse to zero just because 90-day churn is low

Recommended structure:

```text
ConcernScore =
  BaseConcern
  + PersistentChurnContribution
  + BugFixContribution
```

Where:

- `BaseConcern` starts from the existing risk path, but with a non-zero floor for structurally unhealthy files
- `PersistentChurnContribution` comes from `RecurringChurnScore`
- `BugFixContribution` comes from `BugFixRatio365d` and weighted bug/fix touch count

Suggested initial shape:

```text
BaseConcern = max(RiskScore, (10 - HealthScore) * 2)
PersistentChurnContribution = RecurringChurnScore * 8
BugFixContribution = min(BugFixRatio365d * 12, 8) + min(BugFixCommits365d, 4)
ConcernScore = round(BaseConcern + PersistentChurnContribution + BugFixContribution, 1)
```

This should be calibrated with tests and real repo samples, not treated as final truth.

## Repo Vitality Layer

This remains intentionally separate from file-level code health.

### Activity Status

Suggested heuristics for `ActivityStatus`:

- `Stable`
  - consistent monthly commit volume with moderate variance
- `Slowing`
  - last 6 months down at least 30 percent versus the prior 6 months
- `Dormant`
  - at least 2 recent zero-commit months
- `PossiblyAbandoned`
  - at least 3 recent zero-commit months and low trailing activity
- `Revived`
  - previously dormant but recent activity has resumed materially
- `Active`
  - healthy recent activity without concerning decline

These statuses are labels, not moral judgments. Stable low activity can be healthy for a mature system.

### Firefighting Status

Suggested heuristics:

- `Low`
  - firefighting rate under 10 percent
- `Moderate`
  - 10-25 percent
- `High`
  - 25-40 percent
- `Critical`
  - above 40 percent

In the solo plan, this is still informational. It is useful as a "why does this repo feel noisy lately?" signal, not as a proxy for team dysfunction.

## Data Model Changes

### Phase 1: File and Repo Entities

Update [Entities.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/Entities.cs):

- extend `FileMetricsEntity` with the new bug/fix, weighted touch, multi-window churn, and `ConcernScore` fields
- either extend `ProjectHealthSummaryEntity` with repo vitality fields or add a new `RepositoryVitalitySummaryEntity`

Recommendation:

- keep file metrics on `FileMetricsEntity`
- use a separate `RepositoryVitalitySummaryEntity` if repo vitality exceeds a handful of fields or starts to evolve independently
- otherwise keep the first cut simple and store repo vitality on `ProjectHealthSummaryEntity`

### Phase 2: Response Models

Update [ProjectHealthResponse.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Models/Responses/ProjectHealthResponse.cs):

- add the new file metric properties to `FileMetrics`
- add `HistoryMaturity` to `ProjectHealthSummary`
- add a `RepositoryVitalitySummary` section to `ProjectHealthResponse`

Suggested additions:

```csharp
public enum HistoryMaturity
{
    Young,
    Growing,
    Mature
}
```

```csharp
public record RepositoryVitalitySummary(
    HistoryMaturity HistoryMaturity,
    bool HasSufficientHistoryForTrends,
    string ActivityStatus,
    string FirefightingStatus,
    IReadOnlyList<MonthlyCommitPoint> MonthlyCommits,
    int VelocityLast6Months,
    int VelocityPrior6Months,
    double VelocityChangePercent,
    int DormantMonths12m,
    int MaxInactiveStreakMonths,
    int FirefightingCommits90d,
    int FirefightingCommits365d,
    double FirefightingRate90d,
    double FirefightingRate365d);
```

## Service Changes

### Phase 3: Introduce a Solo Health Policy Layer

Add a dedicated policy abstraction under `src/CodeGraph.Services/Analyzers/`:

- `ISoloHealthPolicy`
- `SoloHealthPolicy`
- `HealthPolicyContext`

Responsibilities:

- determine `HistoryMaturity`
- compute `ConcernScore`
- provide solo-oriented labels and explanatory strings

This keeps [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs) from turning into one giant conditional tree.

### Phase 4: Normalize Git History Once

Before layering new metrics into [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs), add an internal history model that can feed all git-derived signals from one pass.

Recommended internal model:

- a normalized commit record with commit date, message classification, and touched source files
- per-file weighted touch accumulation by time window
- repo-level monthly buckets and firefighting counts from the same commit stream

Recommended internal methods:

- `ReadHistoryAsync(repoPath, maxDays)`
- `ClassifyCommit(message, options)`
- `BuildFileHistoryMetrics(history, now)`
- `BuildRepositoryVitalitySummary(history, now)`
- `DetermineHistoryMaturity(history, now)`
- `ComputeRecurringChurnScore(...)`
- `ComputeConcernScore(...)`

### Phase 5: Extend `VitalsAnalyzer`

Enhance [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs) to orchestrate:

- normalized git history collection
- multi-window weighted churn metrics
- weighted bug/fix file metrics
- repo-level monthly activity analysis
- repo-level firefighting analysis
- solo-aware concern ranking

### Phase 6: Prompt Updates

Update the health analysis prompts in [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs#L270) so the generated analysis reflects the solo layer.

Per-project / per-file prompt additions:

- repeated-fix hotspots
- persistent churn concerns
- "this keeps costing time" style maintenance signals

Repo-level prompt additions:

- project vitality status
- whether the repo appears stable, slowing, dormant, or revived
- firefighting rate as a recent maintenance-noise signal
- explicit acknowledgement when long-term signals are immature

## Persistence Changes

### Phase 7: Store Interface

Update [IMetricsStore.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/IMetricsStore.cs):

- support saving and fetching the new file-level fields
- add methods for repo vitality summary persistence if a dedicated entity is introduced

Persistence rule:

- store raw telemetry and computed solo outputs that are stable for this repo
- do not build per-profile persistence machinery in this codebase

### Phase 8: Neo4j Implementation

Update [Neo4jGraphStore.Metrics.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data.Neo4j/Neo4jGraphStore.Metrics.cs):

- persist new `FileMetricsEntity` fields
- persist repo vitality summary fields
- map the new properties in retrieval methods

If a dedicated vitality entity is used, add unique constraints and indexes in a new migration under [src/CodeGraph.Api/Migrations](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/Migrations).

## Query and API Changes

### Phase 9: Query Layer

Update [ProjectQueryService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Query/ProjectQueryService.cs):

- map new file fields
- prefer `ConcernScore` for hotspot ordering
- map repo vitality summary
- include `HistoryMaturity` in health responses

### Phase 10: MCP and Assistant Surface

Update [CodeGraphMcpServer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Assistant/CodeGraphMcpServer.cs) and any mirrored assistant tools:

- include repo vitality in `get_project_health`
- mention when trend signals are unavailable due to limited history
- prefer `ConcernScore` language when describing hotspots

Example `get_project_health` additions:

- `Activity: Slowing`
- `Firefighting: High`
- `History maturity: Mature`
- `Recent velocity: -42% vs prior 6 months`

## UI Plan

### Phase 11: Repo Detail Health UI

Update [repo-detail.component.html](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.html) and related frontend models:

- add a `Project Vitality` section or card near the top of the existing Health area
- add a monthly commits chart
- add vitality badges:
  - `Stable`
  - `Slowing`
  - `Dormant Risk`
  - `High Firefighting`
- add solo-sensitive copy

Recommended emphasis:

- `Repeated Fix Areas`
- `Persistent Work Friction`
- `Maintenance Hotspots`

### Monthly Commit Chart

The chart should show at least the trailing 12 months.

Preferred UX:

- x-axis: month buckets
- y-axis: commit counts
- tooltip on each month
- secondary annotations for zero-commit stretches or major drops

If the frontend charting stack is intentionally minimal, a simple SVG sparkline or bar chart is sufficient for the first pass.

## Configuration

### Phase 12: Health Configuration

Add repo-level or system-level settings for:

- `Health:BugFixKeywords`
- `Health:FirefightingKeywords`
- `Health:YoungHistoryMonths`
- `Health:GrowingHistoryMonths`
- `Health:YoungHistoryCommitCount`
- `Health:GrowingHistoryCommitCount`

Do not add a collaboration-mode switch in this repo for the first implementation.

## Rollout Sequence

### Rollout A: Solo CodeGraph

Implement in this order:

1. add normalized git history collection
2. add bug/fix metrics and multi-window recurring churn metrics
3. add `ConcernScore` without changing `OverallHealth`
4. add repo vitality computation and chart data
5. expose vitality in API, MCP, and UI as informational
6. tune summary prompts for solo maintenance drag

Expected outcome:

- better hotspot ordering for "this keeps costing me time"
- minimal disruption to the current health score

## Testing Plan

### Unit Tests

Add or extend tests in [VitalsAnalyzerTests.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Tests/Services/VitalsAnalyzerTests.cs):

- bug/fix keyword classification
- firefighting keyword classification
- weighted bug/fix attribution across multi-file commits
- recurring churn scoring
- history maturity classification
- `ConcernScore` behavior for low-recent-churn but high-fix-history files
- monthly activity status derivation
- firefighting status derivation

### Query/Mapping Tests

Add tests covering:

- persistence and retrieval of new metrics fields
- response mapping in [ProjectQueryService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Query/ProjectQueryService.cs)
- MCP formatting in [CodeGraphMcpServer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Assistant/CodeGraphMcpServer.cs)

### UI Tests

Add frontend tests for:

- vitality badge rendering
- chart visibility and empty-history behavior
- hotspot ordering by `ConcernScore`

## Open Decisions

These should be settled before or during implementation:

- whether repo vitality belongs on `ProjectHealthSummaryEntity` or a dedicated entity
- whether `RiskScore` should remain alongside `ConcernScore` for one release or be replaced immediately
- whether monthly activity should count all commits or only commits touching source files
- whether weighted file attribution should use touched-file count only or also incorporate changed lines

## Recommended First Slice

The most pragmatic first slice for this repository is:

1. add `HistoryMaturity`
2. add normalized git history collection
3. add bug/fix metrics and recurring churn
4. add `ConcernScore` as the new ranking signal
5. add repo vitality summary with monthly commit counts and firefighting rates
6. expose vitality in API, MCP, and repo detail UI
7. keep `OverallHealth` unchanged until the new signals have been observed in real repos

This delivers the new value without prematurely abstracting for the team fork.
