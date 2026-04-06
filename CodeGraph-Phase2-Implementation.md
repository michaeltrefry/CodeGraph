# CodeGraph — Phase 2 Implementation Plan

This document details the implementation plan for Phase 2 of CodeGraph. Phase 1 produced a working proof of concept: a synchronous CLI-driven pipeline that indexes repositories into a MySQL graph and generates CODEGRAPH.md files via direct Claude API calls.

Phase 2 makes the system production-ready by introducing:
1. **Job-triggered processing** — TC.CodeGraphJobs becomes a WebApi host invoked by the central job manager
2. **RabbitMQ messaging** — jobs publish messages; consumers in TC.CodeGraphApi do the work
3. **Anthropic Batches API** — async batch analysis replaces synchronous per-project calls (~50% cheaper)
4. **Graph-driven richer analysis** — Claude analyzes graph structure (not source code), producing class/method descriptions with confidence levels

**Design principle carried forward:** Self-maintaining. Jobs are scheduled externally. No human intervention required in steady state.

---

## Architecture Changes

### What Changes

| Component | Phase 1 | Phase 2 |
|---|---|---|
| TC.CodeGraphJobs | BackgroundService (auto-discovery) | WebApi host with controller + jobs |
| Analysis trigger | CLI `analyze` command | Job publishes messages, consumers process |
| Claude API usage | Synchronous per-project calls | Async Batches API, one batch per repo |
| Claude input | Raw source code | Graph nodes + edges (token-efficient) |
| Analysis output | Project-level summaries only | Class + method descriptions, per-node |
| Batch tracking | None | `AnalysisBatch` + `AnalysisBatchRequest` tables |
| Analysis storage | `ProjectAnalysisEntity` | New `NodeAnalysis` table per node |

### What Stays the Same

- `IndexingPipeline` — unchanged, still runs Roslyn/SQL/TS extractors
- `MySqlGraphStore` / `IGraphStore` — unchanged
- `GraphQueryEngine` / `GraphAssistant` / MCP server — unchanged
- `TC.CodeGraphApi.Console` — unchanged (CLI still useful for local dev)
- All extractor projects — unchanged

---

## Phase 2.1 — Database Migrations

### Goal
Add tables to track async batch jobs and store per-node analysis results.

### Migration 005 — Analysis Batch Tracking

```sql
-- sql/migrations/005_analysis_batches.sql

CREATE TABLE analysis_batches (
    id                  BIGINT AUTO_INCREMENT PRIMARY KEY,
    repo                VARCHAR(500)    NOT NULL,
    anthropic_batch_id  VARCHAR(200)    NOT NULL,
    status              VARCHAR(50)     NOT NULL DEFAULT 'submitted',  -- submitted | completed | failed | cancelled
    request_count       INT             NOT NULL DEFAULT 0,
    completed_count     INT             NOT NULL DEFAULT 0,
    submitted_at        DATETIME(6)     NOT NULL,
    completed_at        DATETIME(6)     NULL,
    INDEX idx_ab_repo        (repo),
    INDEX idx_ab_status      (status),
    INDEX idx_ab_anthropic   (anthropic_batch_id)
);

CREATE TABLE analysis_batch_requests (
    id              BIGINT AUTO_INCREMENT PRIMARY KEY,
    batch_id        BIGINT          NOT NULL,
    custom_id       VARCHAR(200)    NOT NULL,   -- used as Anthropic's custom_id; equals node_id as string
    node_id         BIGINT          NULL,        -- FK to nodes.id; null for project-level requests
    node_label      VARCHAR(100)    NOT NULL,
    status          VARCHAR(50)     NOT NULL DEFAULT 'pending',  -- pending | succeeded | errored
    completed_at    DATETIME(6)     NULL,
    FOREIGN KEY (batch_id) REFERENCES analysis_batches(id),
    INDEX idx_abr_batch     (batch_id),
    INDEX idx_abr_node      (node_id),
    INDEX idx_abr_custom    (custom_id)
);
```

### Migration 006 — Node Analysis Results

```sql
-- sql/migrations/006_node_analysis.sql

CREATE TABLE node_analysis (
    node_id         BIGINT          NOT NULL PRIMARY KEY,   -- FK to nodes.id
    description     TEXT            NOT NULL,
    confidence      VARCHAR(20)     NOT NULL DEFAULT 'medium',   -- high | medium | low
    model_used      VARCHAR(100)    NULL,
    created_at      DATETIME(6)     NOT NULL,
    updated_at      DATETIME(6)     NOT NULL,
    INDEX idx_na_node (node_id)
);
```

---

## Phase 2.2 — New Entities and Models

### 2.2.1 — DB Entities (TC.CodeGraphApi.Data/Entities.cs)

Add to `Entities.cs`:

```csharp
public class AnalysisBatchEntity
{
    public long Id { get; set; }
    public string Repo { get; set; } = "";
    public string AnthropicBatchId { get; set; } = "";
    public string Status { get; set; } = "submitted";
    public int RequestCount { get; set; }
    public int CompletedCount { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class AnalysisBatchRequestEntity
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public string CustomId { get; set; } = "";
    public long? NodeId { get; set; }
    public string NodeLabel { get; set; } = "";
    public string Status { get; set; } = "pending";
    public DateTime? CompletedAt { get; set; }
}

public class NodeAnalysisEntity
{
    public long NodeId { get; set; }
    public string Description { get; set; } = "";
    public string Confidence { get; set; } = "medium";
    public string? ModelUsed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

### 2.2.2 — Message Contract (TC.CodeGraphApi.Models)

Add new file `TC.CodeGraphApi.Models/Messages/ProcessRepository.cs`:

```csharp
namespace TC.CodeGraphApi.Models.Messages;

/// <summary>
/// Published by ProcessRepositoriesJob. Consumed by ProcessRepositoryConsumer in TC.CodeGraphApi.
/// Instructs the consumer to index and/or analyze a single repository.
/// </summary>
public class ProcessRepository
{
    /// <summary>Short repo name, e.g. "TC.OrdersApi"</summary>
    public string Name { get; set; } = "";

    /// <summary>Absolute local path to the repository root</summary>
    public string Path { get; set; } = "";

    /// <summary>Run the graph indexing pipeline (Roslyn, SQL, TS extractors)</summary>
    public bool ShouldIndex { get; set; }

    /// <summary>Submit an analysis batch to the Anthropic Batches API</summary>
    public bool ShouldAnalyze { get; set; }

    /// <summary>
    /// Compare current HEAD SHA against SyncStateEntity.LastCommitSha.
    /// If they match, skip all processing for this repo.
    /// </summary>
    public bool SkipIfUpToDate { get; set; }
}
```

### 2.2.3 — Analysis Result Types (TC.CodeGraphApi.Services/Models)

Add to `AnalysisTypes.cs`:

```csharp
namespace TC.CodeGraphApi.Services.Models;

/// <summary>Claude's parsed response for a single class batch request.</summary>
public record ClassAnalysisResult(
    string ClassDescription,
    string Confidence);
```

---

## Phase 2.3 — IGraphStore Extensions

### Goal
Add data access methods needed by the consumer and batch result processor.

Add to `IGraphStore` interface and implement in `MySqlGraphStore`:

```csharp
// Batch tracking
Task<long> CreateAnalysisBatchAsync(AnalysisBatchEntity batch, CancellationToken ct = default);
Task<IReadOnlyList<AnalysisBatchEntity>> GetPendingBatchesAsync(string? repo = null, CancellationToken ct = default);
Task UpdateBatchStatusAsync(long batchId, string status, int completedCount, DateTime? completedAt, CancellationToken ct = default);
Task CreateBatchRequestsAsync(IEnumerable<AnalysisBatchRequestEntity> requests, CancellationToken ct = default);
Task UpdateBatchRequestStatusAsync(string customId, string status, DateTime completedAt, CancellationToken ct = default);

// Node analysis results
Task UpsertNodeAnalysisAsync(NodeAnalysisEntity analysis, CancellationToken ct = default);
Task<NodeAnalysisEntity?> GetNodeAnalysisAsync(long nodeId, CancellationToken ct = default);

// Graph context for batch prompt building
Task<IReadOnlyList<NodeEntity>> GetClassNodesWithEdgesAsync(string project, CancellationToken ct = default);
Task<IReadOnlyList<NodeEntity>> GetChildNodesAsync(long parentNodeId, CancellationToken ct = default);
Task<IReadOnlyList<EdgeEntity>> GetNodeEdgesAsync(long nodeId, CancellationToken ct = default);          // outbound
Task<IReadOnlyList<EdgeEntity>> GetInboundEdgesAsync(long nodeId, CancellationToken ct = default);       // inbound (one-hop callers/consumers)

// Commit SHA check
Task<SyncStateEntity?> GetSyncStateAsync(string project, CancellationToken ct = default);
Task UpsertSyncStateAsync(SyncStateEntity state, CancellationToken ct = default);
```

---

## Phase 2.4 — Batch Analysis Service (TC.CodeGraphApi.Services)

### Goal
New service that builds and submits Anthropic batch requests from graph data, and processes completed batch results.

### 2.4.1 — IBatchAnalysisService

```csharp
namespace TC.CodeGraphApi.Services;

public interface IBatchAnalysisService
{
    /// <summary>
    /// Queries the graph for all class nodes with edges in the given repo,
    /// builds one batch request per class (including its methods/properties
    /// and one-hop neighbourhood), submits to the Anthropic Batches API,
    /// and persists AnalysisBatch + AnalysisBatchRequest rows.
    /// </summary>
    Task SubmitAnalysisBatchAsync(string repoName, CancellationToken ct = default);

    /// <summary>
    /// Checks all pending batches (optionally scoped to one repo) for completion.
    /// For completed batches, retrieves results and upserts NodeAnalysis rows.
    /// Called by ProcessBatchResultsJob.
    /// </summary>
    Task ProcessCompletedBatchesAsync(string? repo = null, CancellationToken ct = default);
}
```

### 2.4.2 — BatchAnalysisService Implementation

Key responsibilities:

**`SubmitAnalysisBatchAsync`:**
1. Call `GetClassNodesWithEdgesAsync(repoName)` — only classes that have at least one edge
2. For each class node:
   - Fetch child nodes (methods, properties) via `GetChildNodesAsync` — names used as context, no per-member output requested
   - Fetch outbound edges via `GetOutboundEdgesAsync` (CALLS, PUBLISHES, HTTP_CALLS, QUERIES, IMPLEMENTS, INHERITS, INJECTS)
   - Fetch inbound edges via `GetInboundEdgesAsync` (what CALLS this, what CONSUMES its events)
   - Build prompt (see prompt template below)
3. Submit all requests as one `MessageBatch` to Anthropic SDK
4. Persist `AnalysisBatchEntity` + one `AnalysisBatchRequestEntity` per class

**Prompt template per class:**
```
You are analyzing a C# class from a code graph. Based solely on the structural relationships below — not source code — provide a concise natural language description of what this class does and its business purpose.

Class: {QualifiedName}
Label: {Label}

Methods: {method1}, {method2}, ...
Properties: {prop1}, {prop2}, ...

Outbound relationships:
- IMPLEMENTS: {InterfaceName}
- INHERITS: {BaseClassName}
- CALLS: {ServiceA.MethodX}, {RepositoryB.GetById}, ...
- PUBLISHES: {OrderCreatedEvent}
- HTTP_CALLS: {POST /api/payments}
- QUERIES: {orders, order_items}
- INJECTS: {IOrderRepository}, {IPaymentService}

Inbound relationships (callers/consumers):
- Called by: {OrderController.CreateOrder}, {OrderSyncWorker.ProcessOrder}
- Consumed by: {OrderProjectionConsumer} (via OrderCreatedEvent)

Respond with JSON only (no markdown fences):
{
  "classDescription": "2-3 sentence description in business terms",
  "confidence": "high|medium|low"
}

Use "low" confidence if relationships are sparse.
```

**`ProcessCompletedBatchesAsync`:**
1. Query `GetPendingBatchesAsync(repo)`
2. For each batch, call Anthropic SDK to check status
3. If `ended`: download results, iterate over `MessageBatchIndividualResponse`
4. For each succeeded result:
   - Parse JSON response into `ClassAnalysisResult`
   - Upsert `NodeAnalysisEntity` for the class node
5. Update `AnalysisBatchEntity.status`, `completed_count`, `completed_at`
6. Update each `AnalysisBatchRequestEntity.status`

---

## Phase 2.5 — Consumer (TC.CodeGraphApi/Consumers)

### Goal
Receive `ProcessRepository` messages and orchestrate indexing and batch submission.

Create `TC.CodeGraphApi/Consumers/ProcessRepositoryConsumer.cs`:

```csharp
namespace TC.CodeGraphApi.Consumers;

public class ProcessRepositoryConsumer : TcConsumer<ProcessRepository, ProcessRepositoryConsumer>
{
    private readonly IScope _scope;

    public ProcessRepositoryConsumer(IScope scope, ILogger<ProcessRepositoryConsumer> logger)
        : base(logger)
    {
        _scope = scope;
    }

    public override async Task Consume(ProcessRepository message, ConsumeContext<ProcessRepository> consumeContext)
    {
        using var childScope = _scope.CreateChildScope();
        var graphStore = childScope.GetInstance<IGraphStore>();
        var pipeline = childScope.GetInstance<IndexingPipeline>();
        var batchService = childScope.GetInstance<IBatchAnalysisService>();

        // 1. Skip if up to date
        if (message.SkipIfUpToDate)
        {
            var syncState = await graphStore.GetSyncStateAsync(message.Name);
            var currentSha = GetHeadCommitSha(message.Path);
            if (syncState?.LastCommitSha == currentSha)
            {
                Logger.LogInformation("Skipping {Repo} — already at HEAD {Sha}", message.Name, currentSha);
                return;
            }
        }

        // 2. Index
        if (message.ShouldIndex)
        {
            Logger.LogInformation("Indexing {Repo}", message.Name);
            await pipeline.IndexProjectAsync(message.Name, message.Path);

            var sha = GetHeadCommitSha(message.Path);
            await graphStore.UpsertSyncStateAsync(new SyncStateEntity
            {
                Project = message.Name,
                LastCommitSha = sha,
                LastSyncAt = DateTime.UtcNow,
                Status = "indexed"
            });
        }

        // 3. Analyze
        if (message.ShouldAnalyze)
        {
            // Guard: graph must exist before analysis
            var hasNodes = await graphStore.GetClassNodesWithEdgesAsync(message.Name);
            if (!hasNodes.Any())
            {
                if (!message.ShouldIndex)
                    throw new InvalidOperationException(
                        $"Cannot analyze {message.Name}: no graph data exists and ShouldIndex=false.");

                Logger.LogWarning("No class nodes with edges found for {Repo} after indexing — skipping analysis", message.Name);
                return;
            }

            Logger.LogInformation("Submitting analysis batch for {Repo}", message.Name);
            await batchService.SubmitAnalysisBatchAsync(message.Name);
        }
    }

    private static string? GetHeadCommitSha(string repoPath)
    {
        using var repo = new LibGit2Sharp.Repository(repoPath);
        return repo.Head.Tip?.Sha;
    }
}
```

Register the consumer in `TC.CodeGraphApi/Startup.cs` alongside existing MassTransit/RabbitMQ configuration.

---

## Phase 2.6 — TC.CodeGraphJobs Conversion

### Goal
Convert TC.CodeGraphJobs from a BackgroundService worker host to a WebApi host with controller-based job triggering. Remove `RepositorySyncWorker`.

### 2.6.1 — Project Changes

**Remove:** `RepositorySyncWorker.cs`, `SyncOptions.cs`

**Add NuGet packages:**
```
TC.JobUtilities (internal package)
Swashbuckle.AspNetCore
```

**Update `Program.cs`** to host as WebApi with Swagger:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register IJobRunner from TC.JobUtilities
builder.Services.AddJobRunner();

// Register jobs
builder.Services.AddTransient<ProcessRepositoriesJob>();
builder.Services.AddTransient<ProcessBatchResultsJob>();

// RabbitMQ / ServiceBus registration
builder.Services.AddTcServiceBus(builder.Configuration);

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();
app.Run();
```

### 2.6.2 — ProcessRepositoriesJob

```csharp
namespace TC.CodeGraphJobs.Jobs;

/// <summary>
/// Reads a list of repositories from StartJob.Args and publishes one
/// ProcessRepository message per repo. Short-lived — returns after publishing.
///
/// Args:
///   "repos" — semicolon-separated list of "Name::Path" pairs
///             e.g. "TC.OrdersApi::C:\repos\TC.OrdersApi;TC.BillingApi::C:\repos\TC.BillingApi"
///   "shouldIndex"   — "true"|"false" (default: "true")
///   "shouldAnalyze" — "true"|"false" (default: "true")
///   "skipIfUpToDate" — "true"|"false" (default: "true")
/// </summary>
public class ProcessRepositoriesJob(
    ILogger<ProcessRepositoriesJob> logger,
    ITcServiceBus serviceBus,
    Guid instanceKey)
    : Job(logger, serviceBus, instanceKey)
{
    protected override async Task ExecuteAsync(StartJob startJob)
    {
        var reposArg = startJob.Args.GetValueOrDefault("repos", "");
        if (string.IsNullOrWhiteSpace(reposArg))
        {
            logger.LogWarning("ProcessRepositoriesJob called with no repos argument");
            return;
        }

        var shouldIndex    = !startJob.Args.TryGetValue("shouldIndex",    out var si) || si != "false";
        var shouldAnalyze  = !startJob.Args.TryGetValue("shouldAnalyze",  out var sa) || sa != "false";
        var skipIfUpToDate = !startJob.Args.TryGetValue("skipIfUpToDate", out var sk) || sk != "false";

        var repos = reposArg
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split("::", 2))
            .Where(parts => parts.Length == 2)
            .ToList();

        logger.LogInformation("Publishing {Count} ProcessRepository message(s)", repos.Count);

        foreach (var parts in repos)
        {
            await serviceBus.Publish(new ProcessRepository
            {
                Name           = parts[0].Trim(),
                Path           = parts[1].Trim(),
                ShouldIndex    = shouldIndex,
                ShouldAnalyze  = shouldAnalyze,
                SkipIfUpToDate = skipIfUpToDate
            });
        }
    }
}
```

### 2.6.3 — ProcessBatchResultsJob

```csharp
namespace TC.CodeGraphJobs.Jobs;

/// <summary>
/// Polls Anthropic Batches API for completed batches and stores results.
/// Designed to run on a schedule (e.g. every 30 minutes).
///
/// Args:
///   "repo" — optional; scopes polling to a single repo
/// </summary>
public class ProcessBatchResultsJob(
    ILogger<ProcessBatchResultsJob> logger,
    ITcServiceBus serviceBus,
    IBatchAnalysisService batchService,
    Guid instanceKey)
    : Job(logger, serviceBus, instanceKey)
{
    protected override async Task ExecuteAsync(StartJob startJob)
    {
        startJob.Args.TryGetValue("repo", out var repo);
        await batchService.ProcessCompletedBatchesAsync(repo);
    }
}
```

### 2.6.4 — JobsController

```csharp
namespace TC.CodeGraphJobs.Controllers;

public class JobsController(ILogger<JobsController> logger, IJobRunner runner)
    : JobExecutionController(logger, runner)
{
    /// <summary>
    /// Publish ProcessRepository messages for a list of repositories.
    /// Args: repos (required), shouldIndex, shouldAnalyze, skipIfUpToDate
    /// </summary>
    [HttpPost(nameof(ProcessRepositoriesJob))]
    public StartJobResult ProcessRepositoriesJob([FromBody] StartJob request)
    {
        return runner.RunJob<ProcessRepositoriesJob>(new StartJob { Key = request?.Key ?? Guid.NewGuid(), Args = request?.Args ?? [] });
    }

    /// <summary>
    /// Poll Anthropic Batches API for completed results and store them.
    /// Args: repo (optional)
    /// </summary>
    [HttpPost(nameof(ProcessBatchResultsJob))]
    public StartJobResult ProcessBatchResultsJob([FromBody] StartJob request)
    {
        return runner.RunJob<ProcessBatchResultsJob>(new StartJob { Key = request?.Key ?? Guid.NewGuid(), Args = request?.Args ?? [] });
    }
}
```

---

## Phase 2.7 — DI Registration

### TC.CodeGraphApi/Startup.cs additions

```csharp
// Existing registrations remain. Add:
builder.RegisterType<BatchAnalysisService>().As<IBatchAnalysisService>().InstancePerLifetimeScope();

// Consumer registration (MassTransit / TcServiceBus config)
// Add ProcessRepositoryConsumer to the consumer registration block
```

### TC.CodeGraphApi.Data additions

Register new Dapper type handlers and ensure `AnalysisBatchEntity`, `AnalysisBatchRequestEntity`, `NodeAnalysisEntity` are mapped in `MySqlGraphStore`.

---

## Build Order

| Step | Task |
|---|---|
| 1 | Apply migrations 005 + 006 (`dotnet run --project TC.CodeGraphApi.Console -- migrate`) |
| 2 | Add `ProcessRepository` message to `TC.CodeGraphApi.Models` |
| 3 | Add new entities to `TC.CodeGraphApi.Data/Entities.cs` |
| 4 | Implement new `IGraphStore` methods in `MySqlGraphStore` |
| 5 | Implement `BatchAnalysisService` in `TC.CodeGraphApi.Services` |
| 6 | Add `ProcessRepositoryConsumer` to `TC.CodeGraphApi/Consumers/` |
| 7 | Register consumer + `IBatchAnalysisService` in `TC.CodeGraphApi/Startup.cs` |
| 8 | Convert `TC.CodeGraphJobs` to WebApi host; remove `RepositorySyncWorker` |
| 9 | Add `ProcessRepositoriesJob`, `ProcessBatchResultsJob`, `JobsController` |
| 10 | Build and test end-to-end with 2–3 local repos via Swagger |

---

## Testing Approach

**Manual smoke test via Swagger** (`TC.CodeGraphJobs`):
```json
POST /ProcessRepositoriesJob
{
  "key": "00000000-0000-0000-0000-000000000001",
  "args": {
    "repos": "TC.OrdersApi::C:\\repos\\TC.OrdersApi",
    "shouldIndex": "true",
    "shouldAnalyze": "true",
    "skipIfUpToDate": "false"
  }
}
```

Then poll:
```json
POST /ProcessBatchResultsJob
{
  "args": { "repo": "TC.OrdersApi" }
}
```

**Verify:**
1. `analysis_batches` row created with `status = submitted`
2. `analysis_batch_requests` rows — one per class with edges
3. After results arrive: `node_analysis` rows populated
4. `sync_state.last_commit_sha` updated after indexing

---

## Open Questions / Deferred

- **CODEGRAPH.md generation** — Phase 1 generated these from `ProjectAnalysis`. Phase 2 will produce richer `NodeAnalysis` data. A future pass should update the doc generator to incorporate class/method descriptions.
- **GitLab integration** — Still deferred. `Path` in `ProcessRepository` is a local filesystem path. GitLab repo URLs and auto-cloning are Phase 3+.
- **Cross-repo linking after batch** — `CrossRepoLinker` currently runs after indexing in the CLI. In Phase 2, the consumer runs indexing but does not re-run cross-repo linking. A `CrossRepoLinkingJob` should be added in a follow-up.
- **Analysis of projects with no class nodes** — Some repos (pure SQL, config-only) will produce no batch requests. The consumer logs a warning and skips gracefully; no error queue.
