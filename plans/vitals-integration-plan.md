# Vitals Integration Plan: Codebase Health Metrics for CodeGraph

## Context

[Vitals](https://github.com/tejas-chopra/vitals) is a codebase health analysis tool that computes churn, complexity, co-change coupling, knowledge risk, and composite health scores from git history and structural analysis. Currently a Python-based Claude Code plugin (~1,900 lines), we want to port its metrics into CodeGraph as an optional analysis step.

**Goal:** After indexing a repository, optionally compute vitals-style metrics per file, store them in a new `file_metrics` table, and feed them into the Claude batch analysis prompts so Claude can factor health signals into its descriptions.

**Scope:** All project types already supported by CodeGraph — .NET (Framework 4.5 through .NET 10), Angular/Node, ColdFusion, SQL.

---

## Architecture Decision: Where It Fits

The vitals step runs **after indexing, before batch analysis submission** in `ProcessRepositoryConsumer`. It needs the indexed graph (to know which files/projects exist) and git history (to compute churn, coupling, knowledge risk). Results feed into `BatchAnalysisService.BuildProjectPrompt()`.

```
ProcessRepositoryConsumer flow:
  1. EnsureLocal (clone/pull)
  2. Index (IndexingPipeline)
  3. ** NEW: Compute Vitals metrics **
  4. Analyze (BatchAnalysisService — prompts now include vitals data)
```

This mirrors the existing optional-step pattern. Vitals doesn't need to be an `ICodeExtractor` (it doesn't produce nodes/edges) — it's a post-indexing enrichment step with its own interface.

---

## Phase 0: Data Layer — New Table & Store Methods

### 0.1 Create `FileMetricsEntity`

**File:** `src/TC.CodeGraphApi.Data/Entities.cs`

```csharp
public class FileMetricsEntity
{
    public long Id { get; set; }
    public string Project { get; set; }          // repo name (FK to repositories)
    public string FilePath { get; set; }          // repo-relative path
    public string? DotnetProject { get; set; }    // .csproj name if applicable

    // Churn (90-day window)
    public int Changes { get; set; }              // commit count touching this file
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public int AuthorCount { get; set; }
    public DateTime? LastChangeAt { get; set; }

    // Complexity
    public int ComplexityScore { get; set; }      // 0-100 (vitals scale)
    public int MaxNestingDepth { get; set; }
    public int DeepNestingLines { get; set; }     // lines at depth >= 3
    public int FunctionCount { get; set; }
    public int LongestFunction { get; set; }      // line count

    // Coupling
    public double MaxCouplingStrength { get; set; } // 0.0-1.0
    public int CouplingPartners { get; set; }       // centrality: unique co-changing files

    // Knowledge Risk
    public int TruckFactor { get; set; }          // min authors covering >50% commits
    public string? TopAuthors { get; set; }       // JSON: [{"name":"alice","commits":42}, ...]

    // Composite
    public double HealthScore { get; set; }       // 1.0-10.0
    public string Role { get; set; } = "core";    // "core" or "test"
    public double RiskScore { get; set; }          // ROI-ranked hotspot score

    public DateTime ComputedAt { get; set; }
}
```

### 0.2 Create `ProjectHealthSummaryEntity`

**File:** `src/TC.CodeGraphApi.Data/Entities.cs`

Aggregated per-DotnetProject (or per-repo for non-.NET projects):

```csharp
public class ProjectHealthSummaryEntity
{
    public long Id { get; set; }
    public string Project { get; set; }           // repo name
    public string? DotnetProject { get; set; }    // null = repo-level aggregate

    public double OverallHealth { get; set; }     // weighted average (1.0-10.0)
    public int TotalFiles { get; set; }
    public int HotspotCount { get; set; }         // files with health < 4.0
    public int AlertCount { get; set; }           // files with health < 2.5
    public string? TopHotspots { get; set; }      // JSON: top 10 by risk_score

    public DateTime ComputedAt { get; set; }
}
```

### 0.3 EF Migration

**File:** New migration in `src/TC.CodeGraphApi.Data/Migrations/`

```sql
CREATE TABLE file_metrics (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    project VARCHAR(255) NOT NULL,
    file_path VARCHAR(1024) NOT NULL,
    dotnet_project VARCHAR(255) NULL,
    changes INT NOT NULL DEFAULT 0,
    lines_added INT NOT NULL DEFAULT 0,
    lines_removed INT NOT NULL DEFAULT 0,
    author_count INT NOT NULL DEFAULT 0,
    last_change_at DATETIME NULL,
    complexity_score INT NOT NULL DEFAULT 0,
    max_nesting_depth INT NOT NULL DEFAULT 0,
    deep_nesting_lines INT NOT NULL DEFAULT 0,
    function_count INT NOT NULL DEFAULT 0,
    longest_function INT NOT NULL DEFAULT 0,
    max_coupling_strength DOUBLE NOT NULL DEFAULT 0,
    coupling_partners INT NOT NULL DEFAULT 0,
    truck_factor INT NOT NULL DEFAULT 0,
    top_authors JSON NULL,
    health_score DOUBLE NOT NULL DEFAULT 5.0,
    role VARCHAR(16) NOT NULL DEFAULT 'core',
    risk_score DOUBLE NOT NULL DEFAULT 0,
    computed_at DATETIME NOT NULL,
    UNIQUE KEY uq_file_metrics_project_path (project, file_path(512)),
    INDEX ix_file_metrics_project_health (project, health_score),
    INDEX ix_file_metrics_project_risk (project, risk_score DESC)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE project_health_summaries (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    project VARCHAR(255) NOT NULL,
    dotnet_project VARCHAR(255) NULL,
    overall_health DOUBLE NOT NULL DEFAULT 5.0,
    total_files INT NOT NULL DEFAULT 0,
    hotspot_count INT NOT NULL DEFAULT 0,
    alert_count INT NOT NULL DEFAULT 0,
    top_hotspots JSON NULL,
    computed_at DATETIME NOT NULL,
    UNIQUE KEY uq_project_health_project_dp (project, dotnet_project),
    INDEX ix_project_health_project (project)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

### 0.4 Add Store Methods to `IGraphStore`

**File:** `src/TC.CodeGraphApi.Data/IGraphStore.cs`

```csharp
// File metrics (vitals)
Task UpsertFileMetricsBatchAsync(string project, IReadOnlyList<FileMetricsEntity> metrics);
Task<IReadOnlyList<FileMetricsEntity>> GetFileMetricsAsync(string project, string? dotnetProject = null);
Task<IReadOnlyList<FileMetricsEntity>> GetHotspotsAsync(string project, int top = 10);
Task DeleteFileMetricsAsync(string project);

// Project health summaries
Task UpsertProjectHealthSummaryAsync(ProjectHealthSummaryEntity summary);
Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetProjectHealthSummariesAsync(string project);
```

### 0.5 Implement in `MySqlGraphStore`

**File:** `src/TC.CodeGraphApi.Data/MySqlGraphStore.cs`

- `UpsertFileMetricsBatchAsync`: Dapper `INSERT ... ON DUPLICATE KEY UPDATE`, chunked by 500.
- `GetFileMetricsAsync`: `SELECT * FROM file_metrics WHERE project = @project` with optional `AND dotnet_project = @dp`.
- `GetHotspotsAsync`: `SELECT * FROM file_metrics WHERE project = @project ORDER BY risk_score DESC LIMIT @top`.
- `DeleteFileMetricsAsync`: `DELETE FROM file_metrics WHERE project = @project` (for re-computation).

---

## Phase 1: Metrics Computation Service

### 1.1 Create `IVitalsAnalyzer` Interface

**File:** `src/TC.CodeGraphApi.Services/IVitalsAnalyzer.cs`

```csharp
public interface IVitalsAnalyzer
{
    /// <summary>
    /// Computes vitals metrics for all source files in a repository.
    /// Stores results in file_metrics and project_health_summaries tables.
    /// </summary>
    Task ComputeMetricsAsync(string projectName, string repoPath, CancellationToken ct = default);
}
```

### 1.2 Create `VitalsAnalyzer` Implementation

**File:** `src/TC.CodeGraphApi.Services/VitalsAnalyzer.cs`

This is the core port from vitals' Python scripts. The class orchestrates four git-based collectors and one structural analyzer, then computes composite scores.

**Constructor Dependencies:**
```csharp
public class VitalsAnalyzer(
    IGraphStore store,
    ILogger<VitalsAnalyzer> logger) : IVitalsAnalyzer
```

**`ComputeMetricsAsync` Flow:**
1. Get indexed file nodes from store: `store.GetAllNodesByProjectAsync(project)` — filter to `Label == File`
2. Build file path → DotnetProject mapping from node data
3. Run git-based metrics (all via `Process.Start("git", ...)` from `repoPath`):
   - `ComputeChurnAsync(repoPath)` → `Dictionary<string, ChurnData>`
   - `ComputeCouplingAsync(repoPath)` → `Dictionary<string, CouplingData>`
   - `ComputeKnowledgeRiskAsync(repoPath)` → `Dictionary<string, KnowledgeData>`
4. Run structural metrics:
   - `ComputeComplexityBatch(repoPath, filePaths)` → `Dictionary<string, ComplexityData>`
5. Classify files as core/test (path heuristic from vitals)
6. Compute per-file health score (vitals formula)
7. Compute per-file risk score (ROI-ranked)
8. Build `FileMetricsEntity` list, upsert via store
9. Aggregate per-DotnetProject and repo-level `ProjectHealthSummaryEntity`, upsert

### 1.3 Git Analysis Methods (ported from `git_analysis.py`)

All methods shell out to `git` via `Process.Start()`. This matches the proven vitals approach and avoids pulling LibGit2Sharp into the hot path (LibGit2Sharp doesn't support `--numstat` or `shortlog` anyway).

**`ComputeChurnAsync(repoPath, days=90)`**
- Command: `git log --numstat --format=%H%x00%aI%x00%aN --no-merges --since={days}.days.ago`
- Parse: commit header (hash, date, author) + numstat lines (added, removed, path)
- Output: per-file `{ Changes, LinesAdded, LinesRemoved, AuthorCount, LastChangeAt }`

**`ComputeCouplingAsync(repoPath, days=180)`**
- Command: `git log --name-only --format=%aI --no-merges --since={days}.days.ago`
- Group files by calendar day
- Compute pairwise co-change counts (cap at 50 files/day to avoid combinatorial explosion)
- Output: per-file `{ MaxCouplingStrength, CouplingPartners }`

**`ComputeKnowledgeRiskAsync(repoPath, years=2)`**
- Command: `git log --format=%aN\t%H --no-merges --no-renames --name-only --since={years}.years.ago`
- Count commits per author per file
- Truck factor: minimum authors covering >50% of commits
- Output: per-file `{ TruckFactor, TopAuthors[] }`

### 1.4 Complexity Analysis (ported from `complexity.py`)

**`ComputeComplexityBatch(repoPath, filePaths)`**

Two modes, matching vitals:

1. **C# files (.cs)** — Since Roslyn is already a dependency, use `Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText()` for per-file complexity:
   - Walk syntax tree for nesting depth (`if`, `for`, `while`, `try`, `switch` statements)
   - Count methods, measure longest method (line span)
   - Count lines at nesting depth >= 3
   - Compute complexity score 0-100 using vitals formula

2. **All other files** — Indentation-based heuristic (direct port from vitals):
   - Detect indent unit (tab or N spaces)
   - Measure max nesting depth from whitespace
   - Count deep-nesting lines (depth >= 3)
   - Estimate function boundaries from indentation transitions
   - Same scoring formula

**Complexity Score Formula (0-100, from vitals):**
- Gate: `max_nesting <= 2` → score 0 (config/data files, filtered out)
- Score = `nesting_score(35%) + deep_ratio(25%) + longest_function(20%) + file_length(10%) + branches(10%)`

### 1.5 Health Score Calculation (ported from `health_score.py`)

**Per-file health (1.0-10.0):**
```
health = 0.30 * complexity_subscore
       + 0.30 * churn_subscore
       + 0.20 * coupling_subscore
       + 0.20 * knowledge_subscore
```

Each subscore maps raw metric to 1-10 using vitals' piecewise linear ranges.

**Risk Score (ROI-ranked hotspot ordering):**
```
risk = (10 - health) * changes * role_weight * centrality_boost
centrality_boost = 1.0 + (coupling_partners * 0.2)
role_weight = 1.0 (core) | 0.3 (test)
```

**Project-level health (weighted average):**
- Weight = `11 - file_health` (unhealthy files weighted more heavily)
- Matches vitals' approach where a few critical hotspots drag down overall score

---

## Phase 2: Pipeline Integration

### 2.1 Wire into `ProcessRepositoryConsumer`

**File:** `src/TC.CodeGraphApi/Consumers/ProcessRepositoryConsumer.cs`

Add `IVitalsAnalyzer?` as an optional dependency (nullable, following existing pattern):

```csharp
// After indexing, before analysis:
if (vitalsAnalyzer is not null && message.ShouldIndex)
{
    logger.LogInformation("Computing vitals metrics for {Project}", message.Name);
    await vitalsAnalyzer.ComputeMetricsAsync(message.Name, repoPath, ct);
}
```

**Message extension:** Add `bool ShouldComputeVitals { get; set; } = true;` to `ProcessRepository` message (defaults to true — opt-out rather than opt-in, since it's cheap to compute).

### 2.2 Register in DI

**File:** `src/TC.CodeGraphApi/Startup.cs`

```csharp
container.RegisterType<VitalsAnalyzer>().As<IVitalsAnalyzer>().Scoped(Scope.Transient);
```

### 2.3 Feed Metrics into Claude Prompts

**File:** `src/TC.CodeGraphApi.Services/BatchAnalysisService.cs`

Modify `BuildProjectPrompt()` to include vitals data:

1. Load metrics: `var metrics = await store.GetFileMetricsAsync(repoName, projectName);`
2. Load project health: `var health = await store.GetProjectHealthSummariesAsync(repoName);`
3. Append a new section to the prompt after the graph section:

```
## Codebase Health Metrics

Project health: 5.8/10 (WARNING — 3 hotspots, 1 alert)

### Hotspot Files (highest risk first)
| File | Health | Churn | Complexity | Coupling | Truck Factor |
|------|--------|-------|------------|----------|-------------|
| Services/OrderProcessor.cs | 2.3 | 47 changes | 82/100 | 0.91 (12 partners) | 1 (sole author) |
| Controllers/CheckoutController.cs | 3.1 | 31 changes | 68/100 | 0.74 (8 partners) | 1 |
| ...

Factor these health metrics into your analysis. Files with low health scores (< 4.0)
are statistically 15x more likely to contain bugs. Note single-author files (truck factor 1)
as knowledge risks. High coupling suggests hidden dependencies that may not be visible
in the code structure alone.
```

This section is only added when metrics exist for the project — graceful degradation if vitals step was skipped.

### 2.4 Feed Metrics into Repo Synthesis Prompt

**File:** `src/TC.CodeGraphApi.Services/BatchAnalysisService.cs`

Modify `SynthesizeRepoSummaryAsync()` to include repo-level health:

```
## Repository Health Overview
Overall health: 6.2/10
Total source files analyzed: 342
Hotspots (health < 4.0): 12
Alerts (health < 2.5): 3

Top risk areas:
1. TC.OrdersApi.Services — health 4.1, 5 hotspots
2. TC.OrdersApi.Data — health 5.3, 2 hotspots
3. TC.OrdersApi — health 7.2, 0 hotspots

Include a "Health & Risk" section in your repository summary that highlights
the most concerning areas and recommends prioritized remediation.
```

---

## Phase 3: API Exposure

### 3.1 Add Endpoints to `ProjectsController`

**File:** `src/TC.CodeGraphApi/Controllers/ProjectsController.cs`

```csharp
// GET /api/projects/{name}/health — project health summary + top hotspots
// GET /api/projects/{name}/metrics?dotnetProject=X&top=20 — file-level metrics
// GET /api/projects/{name}/hotspots?top=10 — ranked hotspot list
```

These are read-only query endpoints that return stored metrics.

### 3.2 Add Health Data to Existing Project Detail Endpoint

**File:** `src/TC.CodeGraphApi/Controllers/ProjectsController.cs`

Extend `GET /api/projects/{name}` response to include health summary alongside existing node counts and analysis data.

### 3.3 Add MCP Tool (optional, lower priority)

**File:** `src/TC.CodeGraphApi.Services/CodeGraphMcpServer.cs`

Add `get_health_metrics` tool that returns hotspots for a given project — useful for the `/api/ask` conversational interface.

---

## Phase 4: Console Command

### 4.1 Add `vitals` Command

**File:** `src/TC.CodeGraphApi.Console/Program.cs`

```
codegraph vitals <repo-name> [--top 10]
```

Runs `VitalsAnalyzer.ComputeMetricsAsync()` on demand for a single repo and prints a summary table to stdout. Useful for testing and one-off analysis without triggering full pipeline.

---

## Implementation Order & Estimates

| Step | Dependencies | Description |
|------|-------------|-------------|
| **0.1-0.2** | None | Entity definitions |
| **0.3** | 0.1-0.2 | Migration |
| **0.4-0.5** | 0.3 | Store interface + MySQL implementation |
| **1.1** | None | Interface definition |
| **1.2-1.5** | 0.4, 1.1 | Core metrics engine (largest piece of work — port from Python) |
| **2.1-2.2** | 1.2 | Pipeline wiring + DI |
| **2.3-2.4** | 0.5, 2.1 | Prompt enrichment |
| **3.1-3.2** | 0.5 | API endpoints |
| **3.3** | 3.1 | MCP tool |
| **4.1** | 1.2 | Console command |

**Critical path:** Phase 0 → Phase 1 → Phase 2 (prompt enrichment is where the real value lands).

Phases 3 and 4 can be done in parallel or deferred — the metrics are useful even if only consumed by Claude via prompts.

---

## Design Decisions & Trade-offs

### Why shell out to `git` instead of LibGit2Sharp?
LibGit2Sharp doesn't support `git log --numstat`, `shortlog`, or `--name-only` format. The vitals Python implementation has been tested on repos with 34,000+ commits and 268 contributors. Shelling out to `git` is proven, fast, and matches the battle-tested approach.

### Why not use Roslyn for all complexity analysis?
Roslyn only parses C#/VB. The indentation heuristic covers TypeScript, ColdFusion, SQL, and any future language without adding parser dependencies. For C# files, we *do* use Roslyn (since it's already loaded) for higher accuracy.

### Why a separate table instead of `NodeEntity.Properties`?
- Queryable: `ORDER BY risk_score DESC LIMIT 10` is a single indexed query vs. JSON extraction
- Aggregatable: project-level health requires SQL aggregation across files
- Temporal: `computed_at` enables future trend tracking (compare snapshots over time)
- Clean separation: metrics are an enrichment layer, not part of the structural graph

### Why opt-out (`ShouldComputeVitals = true`) instead of opt-in?
Git analysis on a typical repo (< 5,000 commits) takes 2-5 seconds. The value is high and the cost is low. Projects that need to skip it (e.g., repos with no git history, or re-indexing only) can set the flag to false.

### Why compute coupling over 180 days but churn over 90 days?
Matches vitals' research-backed defaults: churn is a short-term signal (what's actively changing), coupling is a structural signal that needs more history to be statistically meaningful.
