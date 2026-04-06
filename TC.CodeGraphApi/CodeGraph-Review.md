# CodeGraph — Code Review

**Reviewed:** 2026-03-10
**Reviewer:** Claude Sonnet 4.6
**Scope:** Architecture soundness, implementation vs. requirements, code quality, next steps

---

## Part 1 — Architecture

**The architecture is sound.** The layered dependency graph (`Models → Data → Services → Extractors → Api/Console`) is clean, follows company conventions, and the decisions documented in the arch spec are well-justified in the code.

Specific architectural choices that hold up under scrutiny:

- **EF Core + Dapper hybrid** — the code follows this correctly. EF Core handles CRUD entities; Dapper handles the recursive CTE traversal. No drift from the intent.
- **MySQL-specific SQL** — `ON DUPLICATE KEY UPDATE`, `LIKE CONCAT('%', ?, '%')`, `BIGINT AUTO_INCREMENT` — all correct. No SQLite idioms leaked in.
- **Roslyn `SemanticModel`** — `SolutionAnalyzer` opens a full `MSBuildWorkspace` for semantic resolution, with file-only fallback when no `.sln` is present. The design is correct.
- **Incremental indexing via file hashes** — `XxHash3` for content hashing, stored per-project. Design is right.
- **`FoundationalKnowledge` context** — passed into `ExtractorContext` so extractors downstream can recognize in-house attributes. The abstraction is correct even though it is not fully populated yet.

**One structural gap:** `IGraphStore` is missing `FindNodeByIdAsync(long id)`. Several callers need to resolve a node ID to a `GraphNode` object, and without this primitive they have invented broken workarounds (see Part 2). This should be a first-class operation on the interface.

---

## Part 2 — Implementation vs. Requirements

### Phase 1 — Domain Model + Storage ✅ Complete

The schema, entities, EF context, and `MySqlGraphStore` are production-quality. Column mappings are explicit. Batch upsert uses dynamically-built SQL with `DynamicParameters` — correct approach for MySQL without a bind variable limit.

### Phase 2 — Claude Analysis + Documentation ✅ Complete

`ClaudeCodeAnalyzer`, `CodeGraphDocGenerator`, and `ICodeAnalyzer` are all implemented. Prompts are business-context-aware (domain resale terms in the system prompt). JSON parsing handles markdown code fences. Confidence levels are propagated through the entire pipeline correctly.

### Phase 3 — Indexing Pipeline 🟡 Partial

The `IndexingPipeline` itself is good. File discovery, hash filtering, structural node creation, and the batch flush sequence are all correct. However:

**Gap 1 — The CLI `BuildPipeline()` registers zero extractors:**

```csharp
// No extractors registered yet — Phase 3 adds the Roslyn extractor
var extractors = Enumerable.Empty<ICodeExtractor>();
```

Running `index` or `index-all` today creates project/folder/file nodes and NuGet refs, but extracts zero code structure. The CLI cannot be used end-to-end yet.

**Gap 2 — `BuildFoundationalKnowledge()` is a stub:**

```csharp
Task<FoundationalKnowledge> BuildFoundationalKnowledge(IGraphStore store)
{
    // TODO: Phase 3 — query the graph for foundational patterns
    return Task.FromResult(new FoundationalKnowledge());
}
```

The bootstrap order (foundational repos indexed first) is implemented, but the second step — learning the in-house attribute patterns from indexed foundational repos and passing that knowledge downstream — is not yet implemented. MassTransit patterns in application repos will not be recognized until this is done.

**Gap 3 — `TC.CodeGraphJobs` is empty.** No `RepositorySyncWorker`. The background sync layer is fully absent.

**Gap 4 — `Startup.cs` is a stub.** The REST API host registers no services, no DI, no controllers. It cannot serve requests.

### Phase 4 — Extractors 🟡 Partial

| Extractor | Status | Notes |
|-----------|--------|-------|
| Roslyn (C#) | ✅ Excellent | Full semantic analysis, all patterns detected |
| SQL (ScriptDom) | ✅ Present | Tables, views, procedures, foreign keys |
| ColdFusion (regex) | ✅ Present | Components, functions, HTTP calls, queries |
| TypeScript (Node.js sidecar) | ❌ Missing | `.csproj` exists, no source files |

### Phase 5 — Jobs ❌ Not started

---

## Part 3 — Bugs

### Bug 1 — CrossRepoLinker: `FindNodeByIdAsync` / `FindNodeByEdgeSourceAsync` (Critical)

**File:** `src/TC.CodeGraphApi.Services/CrossRepoLinker.cs` lines 238–250

```csharp
private async Task<GraphNode?> FindNodeByEdgeSourceAsync(GraphEdge edge)
{
    var nodes = await _store.FindEdgesBySourceAsync(edge.SourceId); // dead — unused
    var results = await _store.TraverseAsync(edge.SourceId, TraceDirection.Outbound, 0);
    return results.FirstOrDefault()?.Node;
}

private async Task<GraphNode?> FindNodeByIdAsync(long nodeId)
{
    var results = await _store.TraverseAsync(nodeId, TraceDirection.Outbound, 0);
    return results.FirstOrDefault()?.Node;
}
```

**Problem:** `TraverseAsync` returns nodes *reachable from* the start node (its neighbors), not the start node itself. Both methods are trying to look up the node *at* a given ID, but they return a wrong, unrelated node instead. `IGraphStore` has no `FindNodeByIdAsync` primitive.

**Impact:** `LinkMessagingAsync` and `LinkNuGetPackagesAsync` silently produce zero cross-repo edges because they can never resolve source node projects correctly. The primary value of the system — cross-service dependency tracing — does not work.

**Fix:** Add `Task<GraphNode?> FindNodeByIdAsync(long id)` to `IGraphStore`. Implement it as a single-row primary key lookup in `MySqlGraphStore`. Replace both usages in `CrossRepoLinker`.

---

### Bug 2 — GraphQueryEngine: Dead code query in `FindConsumersAsync`

**File:** `src/TC.CodeGraphApi.Services/GraphQueryEngine.cs` line 111

```csharp
var sourceNodes = await _store.SearchNodesAsync(null, "%", limit: 1); // result never used
```

This fires a database query on every `FindConsumers` call and discards the result. Should be removed.

---

## Part 4 — Performance Issues

### Issue 1 — `GetArchitectureAsync`: N+1 queries for hotspot detection

**File:** `src/TC.CodeGraphApi.Services/GraphQueryEngine.cs` lines 175–183

```csharp
foreach (var method in methods.Take(100))
{
    var inbound = await _store.FindEdgesByTargetAsync(method.Id, EdgeType.CALLS);
    if (inbound.Count >= 3) { ... }
}
```

Up to 100 sequential round trips to find hotspots. A single `SELECT target_id, COUNT(*) FROM edges WHERE type = 'CALLS' GROUP BY target_id HAVING COUNT(*) >= 3` query replaces the loop.

### Issue 2 — `FindArchivalCandidatesAsync`: N+1 queries

**File:** `src/TC.CodeGraphApi.Services/GraphQueryEngine.cs` lines 204–214

```csharp
foreach (var project in projects)
{
    var crossRepoEdges = await _store.FindCrossRepoEdgesAsync(project.Name);
    if (crossRepoEdges.Count == 0)
        candidates.Add(project);
}
```

One query per project. Replace with a single query that returns all projects having no cross-repo edges.

### Issue 3 — `stats` command: loads all nodes into memory

**File:** `src/TC.CodeGraphApi.Console/Program.cs` lines 202–207

```csharp
foreach (var label in Enum.GetValues<NodeLabel>())
{
    var nodes = await store.FindAllNodesByLabelAsync(label);
    if (nodes.Count > 0)
        Console.WriteLine($"  {label,-20} {nodes.Count,8:N0}");
}
```

`FindAllNodesByLabelAsync` materializes every node for every label. With 620 repos this will load millions of rows. Replace with `SELECT label, COUNT(*) FROM nodes GROUP BY label`.

---

## Part 5 — Smaller Issues

**`Chunk<T>` re-implemented manually** in `MySqlGraphStore` — `.NET 6+` includes `IEnumerable<T>.Chunk(n)`.

**`CodeGraphDbContext` verbose column mappings** — Pomelo's EF Core provider maps `PascalCase` to `snake_case` by convention. The explicit column mappings duplicate what convention would infer. Not a bug, but adds noise.

**`SolutionAnalyzer` parallel project compilation** — `Parallel.ForEachAsync` over projects is reasonable, but `MSBuildWorkspace` is not documented as thread-safe across concurrent project loads. This may surface intermittent failures on large solutions. Consider sequential project loading with parallel file analysis within each project.

**`ClaudeCodeAnalyzer` error propagation** — If Claude fails on one project during `Task.WhenAll`, the exception aborts the entire repo analysis. For a self-maintaining system, the correct behavior is to log the failure, continue with the remaining projects, and assign `Confidence=Low` to the failed one.

**`TraverseAsync` CTE with `DISTINCT`** — The same node can appear at multiple depths (reachable via different paths) and `DISTINCT` does not deduplicate these because depth is included in the projection. Consumers of `TraverseAsync` should be aware they may receive the same node more than once.

**`GetFoundationalRepos()` hardcoded** — Returns `TC.Common.ServiceStack`, `TC.Common.ServiceBus`, `TC.Common.Models` as a hardcoded list. Should be loaded from configuration (`appsettings.json` or environment) per the architecture spec.

---

## Part 6 — What Is Working Well

- **Roslyn extractor quality** is genuinely strong. It correctly handles classes, interfaces, records, structs, enums, delegates, methods, properties, constructors, DI registrations (`AddScoped<IFoo, Foo>()`), HTTP route attribute detection with class-level prefix combination, `[controller]` token substitution, MassTransit `Consumer<T>` detection, `ServiceBus.Publish` detection with generic type argument extraction, `HttpClient` call detection with interpolated string URL pattern extraction, and cyclomatic complexity computation.
- **Domain model** is clean and complete. All node types and edge types from the architecture spec are represented.
- **Storage layer** is production-quality with correct MySQL idioms throughout.
- **MCP server** is functional with all 12 planned tools implemented.
- **Incremental indexing** design (hash-based file change detection) is correct.
- **Bootstrap order** (foundational repos first, then application repos) is implemented in `index-all`.
- **Self-healing philosophy** is visible throughout — per-file failures are logged and skipped, the pipeline never lets one bad file abort the rest.

---

## Part 7 — Next Steps

Ordered by impact on getting the system functionally usable.

### 1. Wire extractors into `BuildPipeline()` in Console (Immediate)

Register `RoslynExtractor` (via `SolutionAnalyzer`), `SqlExtractor`, and `ColdFusionExtractor`. This is a wiring change only — no new logic required. Without it, `index` and `index-all` produce only structural skeleton nodes.

### 2. Fix `CrossRepoLinker` — add `FindNodeByIdAsync` to `IGraphStore` (Immediate)

Add the method, implement it in `MySqlGraphStore` as a primary key lookup, and replace the two broken usages in `CrossRepoLinker`. This fixes the critical silent failure in cross-repo dependency linking.

### 3. Register services in `Startup.cs` (Short-term)

The API host is a non-functional stub. Wire DI for `IGraphStore`, `IndexingPipeline`, `GraphQueryEngine`, `CodeGraphMcpServer`, and all extractors. Use the Console's composition as the reference pattern.

### 4. Implement `BuildFoundationalKnowledge()` (Short-term)

After foundational repos are indexed, query the graph for known attribute and base class patterns. Populate the `PublishAttributes`, `ConsumeAttributes`, and `PatternBaseClasses` dictionaries in `FoundationalKnowledge`. Pass this to subsequent application repo indexing so MassTransit patterns are correctly identified.

### 5. Implement `TC.CodeGraphJobs` — `RepositorySyncWorker` (Short-term)

A basic `BackgroundService` that periodically scans a configured directory (or eventually GitLab) for new/changed repos, re-runs `IndexingPipeline`, and then calls `CrossRepoLinker.LinkAsync`. Even a simple poll-on-interval implementation proves the self-maintaining property.

### 6. End-to-end smoke test with real repos (Short-term)

Index `TC.Common.ServiceStack` as foundational, then index two repos known to communicate (a publisher and a consumer of the same event). Verify:
- Nodes appear in the graph for classes, methods, routes
- `PUBLISHES` and `CONSUMES` edges appear
- Cross-repo edges are created by the linker
- `find_consumers` MCP tool returns the consumer
- `trace_call_path` traces across the boundary

This will surface any remaining wiring issues before expanding to the full 620 repos.

### 7. Fix `stats` command performance (Short-term)

Replace `FindAllNodesByLabelAsync` loops with `SELECT label, COUNT(*) FROM nodes GROUP BY label` and the equivalent for edges. Add this as `GetNodeCountsByLabelAsync` / `GetEdgeCountsByTypeAsync` on `IGraphStore`.

### 8. `ClaudeCodeAnalyzer` error resilience (Short-term)

Wrap individual project analyses in `try/catch` inside the `AnalyzeRepositoryAsync` fan-out. Log failures, substitute a low-confidence placeholder, and continue with the remaining projects.

### 9. GitLab integration (Phase 3)

Once the local proof of concept is stable, implement `IGitLabService` for repository discovery, clone/pull via LibGit2Sharp, and webhook receiver for push-triggered re-indexing. This is what converts the system from a local tool to a self-maintaining service.

### 10. TypeScript extractor (Phase 4)

The Node.js sidecar for TypeScript/Angular is the remaining language gap. Required for full-stack tracing from Angular frontends through C# APIs to database. The `TC.CodeGraphApi.Extractors.TypeScript` project is scaffolded but empty.

---

## Summary

| Area | Status |
|------|--------|
| Architecture | ✅ Sound |
| Domain model | ✅ Complete |
| Storage layer | ✅ Production-quality |
| Roslyn extractor | ✅ Excellent |
| SQL extractor | ✅ Present |
| ColdFusion extractor | ✅ Present |
| Claude analyzer | ✅ Complete |
| CODEGRAPH.md generation | ✅ Complete |
| MCP server | ✅ Functional (12 tools) |
| CLI commands | 🟡 Structured but extractors not wired |
| Cross-repo linker | 🔴 Critical bug — silent no-op |
| API host (Startup.cs) | 🔴 Stub only |
| Background jobs | 🔴 Empty project |
| TypeScript extractor | 🔴 Not implemented |
| GitLab integration | 🔴 Not started |

The system is approximately 60% complete. The foundation is strong and the design decisions are correct throughout. Steps 1–2 above (wiring extractors + fixing the linker) are the highest-leverage changes — they would make the system produce real, queryable graph data for the first time.
