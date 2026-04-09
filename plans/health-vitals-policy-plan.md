# Health Vitals Policy Plan

## Status Snapshot

Last updated: 2026-04-09

Purpose:

- define the next evolution of CodeGraph health metrics for two product contexts:
  - personal-use CodeGraph for a single developer
  - team-oriented `TC.CodeGraphApi` for enterprise use
- preserve one shared metrics pipeline while allowing different policy interpretations
- add long-history repository vitality signals without destabilizing the current health score

Current state in this repo:

- file-level vitals-style metrics already exist in [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs)
- the current scoring model emphasizes complexity, churn, coupling, knowledge risk, lint, trust, and `RiskScore`
- the repo detail UI already exposes top hotspots in [repo-detail.component.html](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.html)
- no profile-aware scoring exists yet
- no repo-level monthly activity or firefighting trend model exists yet
- no bug/fix factor exists yet

Design decision locked in:

- do not build separate health systems for the personal and enterprise variants
- build one shared telemetry model and scoring pipeline
- apply different policy profiles for `Solo` and `Team`
- keep long-history signals as a separate vitality layer first, rather than immediately changing `OverallHealth`

## Goal

Extend CodeGraph health analysis so it can answer two different questions well:

- for personal use: "what parts of this codebase keep costing me time?"
- for team use: "what parts of this codebase are unstable, neglected, or risky because of team dynamics?"

This plan introduces:

- collaboration-aware health policy profiles
- git-history maturity gating
- bug/fix factor
- persistent churn and recurring-change signals
- repository vitality signals such as acceleration/decline and firefighting rate
- a monthly commit activity chart for repo health

## Product Principles

### Shared Telemetry, Different Policy

Both products should compute the same raw metrics where practical. The difference should be how the product interprets and prioritizes them.

Examples:

- `TruckFactor = 1` is expected in a solo project, but concerning in a team project
- repeated bug/fix commits are useful in both modes, but much more severe when many authors are involved
- churn matters in both modes, but only persistent churn over time should be emphasized for solo use

### Stable Health Score, Aggressive Concern Ranking

`OverallHealth` should remain explainable and relatively stable.

The new "this needs attention now" behavior should primarily come from a stronger ranking metric. The current `RiskScore` is the right starting point for that role, though it may be renamed or expanded into a more explicit `ConcernScore`.

### Maturity-Gated History Signals

Long-history git heuristics should not pretend confidence when the repo is young.

If there is not enough history, CodeGraph should explicitly state that repo vitality and long-term trend signals are immature rather than silently omitting them or making weak guesses.

## Shared Model

### New Policy Inputs

Introduce two policy dimensions:

- `CollaborationMode`
  - `Solo`
  - `Team`
- `HistoryMaturity`
  - `Young`
  - `Growing`
  - `Mature`

Suggested initial thresholds:

- `Young`
  - repository age under 6 months, or
  - fewer than 100 commits
- `Growing`
  - repository age 6-12 months, and
  - at least 100 commits but fewer than 300
- `Mature`
  - at least 12 months of history, and
  - at least 300 commits

These thresholds should live in configuration rather than hard-coded constants so the enterprise product can tune them independently.

### New File-Level Metrics

Add the following to [Entities.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/Entities.cs) and flow them through [ProjectHealthResponse.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Models/Responses/ProjectHealthResponse.cs):

- `BugFixCommits90d`
- `BugFixCommits365d`
- `BugFixRatio365d`
- `BugFixAuthors365d`
- `Churn30d`
- `Churn90d`
- `Churn365d`
- `RecurringChurnScore`

Definitions:

- `BugFixCommits90d`: number of commits touching the file in the last 90 days whose commit messages look like bug/fix work
- `BugFixCommits365d`: same over 365 days
- `BugFixRatio365d`: `BugFixCommits365d / TotalCommitsTouchingFile365d`
- `BugFixAuthors365d`: distinct authors involved in those bug/fix commits
- `Churn30d`, `Churn90d`, `Churn365d`: commit counts touching the file in those windows
- `RecurringChurnScore`: a persistence metric that rewards repeated activity across multiple windows rather than one burst

### New Repo-Level Vitality Metrics

Add the following to [ProjectHealthSummaryEntity](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/Entities.cs#L191) or to a dedicated `RepositoryVitalitySummaryEntity` if keeping the health entity small becomes preferable:

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
- `FirefightingAuthors90d`
- `FirefightingTrend`
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
- `FirefightingAuthors90d`: distinct authors participating in recent firefighting commits
- `FirefightingTrend`: recent 90-day firefighting rate versus the prior 90-day window

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
- classify at commit level, then attribute the commit to touched files
- keep the keyword set configurable because enterprise teams often use local conventions

### Firefighting Classification

Initial firefighting keywords:

- `hotfix`
- `incident`
- `urgent`
- `emergency`
- `rollback`
- `revert`

Notes:

- firefighting is a repo/process signal first, not a file-quality signal
- for enterprise use, this should be shown prominently even when the repo's code structure is otherwise decent

### Recurring Churn

`RecurringChurnScore` should not be a simple sum of recent changes.

Recommended initial heuristic:

- compute file commit counts in 30d, 90d, and 365d windows
- score higher when the file shows meaningful activity in all three windows
- score lower when all activity is concentrated in just one short period

One simple first-pass formula:

```text
normalized30 = min(churn30d / 10, 1.0)
normalized90 = min(churn90d / 25, 1.0)
normalized365 = min(churn365d / 100, 1.0)
RecurringChurnScore = round((0.2 * normalized30) + (0.3 * normalized90) + (0.5 * normalized365), 2)
```

The point is persistence, not volume.

## Policy Profiles

### Solo Profile

Primary question:

- where is the code creating repeated personal maintenance drag?

Interpretation rules:

- truck factor should contribute little or nothing to score
- bug/fix factor should strongly affect hotspot ranking
- churn only becomes concerning when it persists across multiple time windows
- repo vitality signals should mostly be informational unless the history is clearly mature

Recommended weighting direction:

- complexity: high
- recurring churn: high
- bug/fix ratio: high
- coupling: medium
- lint/trust: medium
- truck factor: informational or very low

Recommended summary wording:

- "Work Friction"
- "Repeated Fix Areas"
- "Persistent Maintenance Hotspots"

### Team Profile

Primary question:

- where is the system unstable, neglected, or risky because of team ownership and maintenance behavior?

Interpretation rules:

- truck factor remains meaningful
- multi-author bug/fix activity is a major warning sign
- short-window churn matters sooner than it does in solo mode
- repo vitality signals should be prominent and summary-driving

Recommended weighting direction:

- bug/fix ratio: very high
- bug/fix author spread: very high
- truck factor / ownership concentration: high
- churn: high
- coupling: medium
- complexity: medium
- lint/trust: medium

Recommended summary wording:

- "Team Risk"
- "Operational Vitality"
- "Shared Instability"

## Scoring Strategy

### Preserve `HealthScore`

The current `HealthScore` in [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs#L860) should remain the baseline code-health score in the first rollout.

Do not immediately mix in every new signal. That would make the score harder to understand and harder to compare over time.

### Add `ConcernScore`

Add a ranking metric specifically for "surface this first."

This can either:

- extend the existing `RiskScore`, or
- replace it with a clearer `ConcernScore` name while keeping `RiskScore` as a compatibility alias for one release

Recommended structure:

```text
ConcernScore =
  BaseRisk
  * (1 + PersistentChurnBoost)
  * (1 + BugFixBoost)
  * (1 + TeamAmplifier)
```

Where:

- `BaseRisk` starts from the existing health/churn/coupling/role path
- `PersistentChurnBoost` comes from `RecurringChurnScore`
- `BugFixBoost` comes from `BugFixRatio365d`
- `TeamAmplifier` is near zero in `Solo`, but meaningful in `Team` when many authors participate in fixes

Suggested initial boosts:

- `PersistentChurnBoost = RecurringChurnScore * 0.75`
- `BugFixBoost = min(BugFixRatio365d * 2.0, 1.5)`
- `TeamAmplifier = TeamMode ? min(BugFixAuthors365d * 0.15, 0.75) : 0`

This should be calibrated with tests, not treated as final truth.

## Repo Vitality Layer

This is intentionally separate from file-level code health.

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

These statuses should be treated as labels, not as moral judgments. Stable low activity can be healthy for a mature system.

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

In `Team` mode, escalate severity when firefighting spans multiple authors.

### Inactive Owner and Neglect Signals

Enterprise-only interpretation should consider:

- sudden drop in commit activity after a dominant contributor disappears
- long inactivity streaks on a once-active project
- high recent firefighting combined with declining overall throughput

These should surface as summary callouts before they ever affect `OverallHealth`.

## Data Model Changes

### Phase 1: File and Repo Entities

Update [Entities.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/Entities.cs):

- extend `FileMetricsEntity` with new bug/fix and multi-window churn fields
- either extend `ProjectHealthSummaryEntity` with repo vitality fields or add a new `RepositoryVitalitySummaryEntity`

Recommendation:

- keep file metrics on `FileMetricsEntity`
- use a separate `RepositoryVitalitySummaryEntity` if repo vitality becomes more than a handful of fields
- if minimizing schema churn matters more right now, repo vitality can live on `ProjectHealthSummaryEntity` for the first cut

### Phase 2: Response Models

Update [ProjectHealthResponse.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Models/Responses/ProjectHealthResponse.cs):

- add new file metric properties to `FileMetrics`
- add profile metadata and history maturity to `ProjectHealthSummary`
- add a `RepositoryVitalitySummary` section to `ProjectHealthResponse`

Suggested additions:

```csharp
public enum CollaborationMode
{
    Solo,
    Team
}

public enum HistoryMaturity
{
    Young,
    Growing,
    Mature
}
```

```csharp
public record RepositoryVitalitySummary(
    CollaborationMode CollaborationMode,
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
    double FirefightingRate365d,
    int FirefightingAuthors90d);
```

## Service Changes

### Phase 3: Introduce a Health Policy Layer

Add a dedicated policy abstraction under `src/CodeGraph.Services/Analyzers/`:

- `IHealthPolicy`
- `SoloHealthPolicy`
- `TeamHealthPolicy`
- `HealthPolicyContext`

Responsibilities:

- determine `HistoryMaturity`
- compute profile-sensitive weighting decisions
- compute `ConcernScore`
- provide summary labels and explanatory strings

This keeps [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs) from turning into one giant conditional tree.

### Phase 4: Extend `VitalsAnalyzer`

Enhance [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs) to orchestrate:

- multi-window churn collection
- bug/fix file metrics
- repo-level monthly activity analysis
- repo-level firefighting analysis
- profile-aware concern ranking

Recommended internal methods:

- `ComputeChurnAsync(repoPath, days)`
- `ComputeBugFixMetricsAsync(repoPath, days)`
- `ComputeMonthlyActivityAsync(repoPath, months)`
- `ComputeFirefightingMetricsAsync(repoPath, days)`
- `DetermineHistoryMaturityAsync(repoPath)`
- `ComputeRecurringChurnScore(...)`
- `ComputeConcernScore(...)`
- `BuildRepositoryVitalitySummary(...)`

### Phase 5: Prompt Updates

Update the health analysis prompts in [VitalsAnalyzer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Analyzers/VitalsAnalyzer.cs#L270) so the generated analysis reflects the new layer.

Per-project / per-file prompt additions:

- repeated-fix hotspots
- persistent churn concerns
- in team mode, shared ownership or fix-spread concerns

Repo-level prompt additions:

- project vitality status
- whether the repo appears stable, slowing, dormant, or revived
- firefighting rate and whether it is concentrated or team-wide
- explicit acknowledgement when long-term signals are immature

## Persistence Changes

### Phase 6: Store Interface

Update [IMetricsStore.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/IMetricsStore.cs):

- support saving and fetching the new file-level fields
- add methods for repo vitality summary persistence if a dedicated entity is introduced

### Phase 7: Neo4j Implementation

Update [Neo4jGraphStore.Metrics.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data.Neo4j/Neo4jGraphStore.Metrics.cs):

- persist new `FileMetricsEntity` fields
- persist repo vitality summary fields
- map the new properties in retrieval methods

If a dedicated vitality entity is used, add unique constraints and indexes in a new migration under [src/CodeGraph.Api/Migrations](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/Migrations).

## Query and API Changes

### Phase 8: Query Layer

Update [ProjectQueryService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Query/ProjectQueryService.cs):

- map new file fields
- map repo vitality summary
- include `CollaborationMode` and `HistoryMaturity` in health responses

### Phase 9: MCP and Assistant Surface

Update [CodeGraphMcpServer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Assistant/CodeGraphMcpServer.cs) and any mirrored assistant tools:

- include repo vitality in `get_project_health`
- include activity and firefighting labels in `get_fleet_health` for team-oriented rollouts
- mention when trend signals are unavailable due to limited history

Example `get_project_health` additions:

- `Activity: Slowing`
- `Firefighting: High`
- `History maturity: Mature`
- `Recent velocity: -42% vs prior 6 months`

## UI Plan

### Phase 10: Repo Detail Health UI

Update [repo-detail.component.html](/Users/michael/Repos/CodeGraph/CodeGraphWeb/src/app/pages/repo-detail/repo-detail.component.html) and related frontend models:

- add a `Project Vitality` section or card near the top of the existing Health area
- add a monthly commits chart
- add vitality badges:
  - `Stable`
  - `Slowing`
  - `Dormant Risk`
  - `High Firefighting`
- add profile-sensitive copy

For personal CodeGraph:

- emphasize "Repeated Fix Areas" and "Persistent Work Friction"
- de-emphasize truck factor wording

For enterprise CodeGraph:

- emphasize "Team Risk" and "Project Vitality"
- show monthly commit trend and firefighting summary prominently

### Monthly Commit Chart

The chart should show at least the trailing 12 months.

Preferred UX:

- x-axis: month buckets
- y-axis: commit counts
- tooltip on each month
- secondary annotations for zero-commit stretches or major drops

If the frontend charting stack is intentionally minimal, a simple SVG sparkline/bar chart is sufficient for the first pass.

## Configuration

### Phase 11: Health Profile Configuration

Add a repo-level or system-level setting:

- `Health:CollaborationMode` defaulting to `Solo` in this repo
- optional future support for `Auto`

Add configurable keyword lists:

- `Health:BugFixKeywords`
- `Health:FirefightingKeywords`

Add configurable maturity thresholds:

- `Health:YoungHistoryMonths`
- `Health:GrowingHistoryMonths`
- `Health:YoungHistoryCommitCount`
- `Health:GrowingHistoryCommitCount`

## Rollout Sequence

### Rollout A: Personal CodeGraph First

Implement first in this order:

1. add new file-level bug/fix and multi-window churn metrics
2. add `ConcernScore` without changing `OverallHealth`
3. add `CollaborationMode = Solo`
4. add repo vitality computation and chart data
5. show repo vitality as informational in the UI
6. tune the personal summary prompts

Expected outcome:

- better hotspot ordering for "this keeps costing me time"
- minimal disruption to the current health score

### Rollout B: Enterprise Transfer

When carrying to `TC.CodeGraphApi`, change mainly the policy pack and prominence:

1. switch default profile to `Team`
2. make truck factor meaningful again
3. strongly amplify multi-author bug/fix and firefighting
4. promote monthly commit trend and vitality to first-class summary status
5. add enterprise-specific explanation strings around neglect and ownership risk

Expected outcome:

- stronger insight into unstable, neglected, and under-owned systems

## Testing Plan

### Unit Tests

Add or extend tests in [VitalsAnalyzerTests.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Tests/Services/VitalsAnalyzerTests.cs):

- bug/fix keyword classification
- firefighting keyword classification
- recurring churn scoring
- history maturity classification
- `Solo` versus `Team` concern ranking differences
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
- `Solo` versus `Team` wording differences if profile is exposed to the frontend

## Open Decisions

These should be settled before or during implementation:

- whether repo vitality belongs on `ProjectHealthSummaryEntity` or a dedicated entity
- whether to rename `RiskScore` to `ConcernScore`
- whether collaboration mode is stored per repository, per environment, or globally
- whether monthly activity should count all commits or only commits touching source files
- how aggressively to normalize author identities before trusting contributor-spread signals

## Recommended First Slice

The most pragmatic first slice for this repository is:

1. add `CollaborationMode` and `HistoryMaturity`
2. add bug/fix metrics and recurring churn
3. introduce `ConcernScore` as the new ranking signal
4. add repo vitality summary with monthly commit counts and firefighting rates
5. expose vitality in API, MCP, and repo detail UI
6. keep `OverallHealth` unchanged until the new signals have been observed in real repos

This delivers the new value without forcing immediate score recalibration.
