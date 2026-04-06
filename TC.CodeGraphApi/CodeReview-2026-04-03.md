# Code Review: TC.CodeGraphApi

**Date:** 2026-04-03
**Scope:** Full codebase — API, Services, Data (MySQL + Neo4j), Extractors, Tests

---

## Summary

| Category | Critical | High | Medium | Low | Total |
|----------|----------|------|--------|-----|-------|
| Performance | 2 | 5 | 6 | 2 | 15 |
| Correctness | 1 | 1 | 3 | 2 | 7 |
| Code Quality | — | — | 5 | 4 | 9 |
| Configuration | — | 1 | 2 | 2 | 5 |
| Test Coverage | — | — | 3 | — | 3 |
| **Total** | **3** | **7** | **19** | **10** | **39** |

---

## Critical

### C1. Over-fetching entire datasets for single lookups

**`src/TC.CodeGraphApi.Services/Query/ProjectQueryService.cs:45-91`**

`GetDetailAsync()` loads ALL repositories into memory to find one by name, despite `GetRepositoryByNameAsync()` already existing in the store. The same method also fetches up to 100,000 nodes just to get a pagination count.

`ListAsync()` at line 13 also loads all repos into memory, filters in C#, then paginates — all work that should happen in SQL.

**Impact:** O(n) memory and network for single-record lookups. Scales poorly as repo count grows.
**Fix:** Use targeted store queries and implement a `COUNT(*)` query for totals.

### C2. Neo4j `DeleteNodesByProjectAsync` not batched

**`src/TC.CodeGraphApi.Data.Neo4j/Neo4jGraphStore.Nodes.cs:377-387`**

Comment says "Delete in batches" but implementation runs a single `MATCH (n:CodeNode {project: $project}) DETACH DELETE n`. On a large project with millions of nodes this will consume massive transaction memory and lock the entire graph.

**Impact:** Transaction memory explosion, graph-wide lock, potential timeout on large projects.
**Fix:** Use cursor-based batching: `CALL { MATCH (n:CodeNode {project: $project}) WITH n LIMIT 5000 DETACH DELETE n } IN TRANSACTIONS OF 5000 ROWS` or APOC `periodic.iterate`.

### C3. Unbounded result sets with no pagination

**`src/TC.CodeGraphApi.Data/MySqlGraphStore.Nodes.cs`**

Multiple methods return every matching row with no `LIMIT`:

| Method | Line |
|--------|------|
| `FindNodesByLabelAsync` | 129 |
| `FindAllNodesByLabelAsync` | 195 |
| `FindNodesByNameAsync` | 120 |
| `FindNodesByFileAsync` | 139 |

A query for all "Method" nodes across the graph could return hundreds of thousands of rows.

**Impact:** Memory exhaustion under real workloads.
**Fix:** Add `limit`/`offset` parameters with sensible defaults (e.g., 1000).

---

## High

### H1. Neo4j BFS traversal done in application loop

**`src/TC.CodeGraphApi.Data.Neo4j/Neo4jGraphStore.Edges.cs:294-380`**

`TraverseAsync` runs one Cypher query per depth level in a `for` loop (`depth = 1..maxDepth`), fetching the frontier each hop. For `maxDepth=5` that's 5+ database round trips.

**Impact:** Latency multiplied by depth. Significant for deep traversals.
**Fix:** Single variable-length Cypher path query: `MATCH path = (start)-[*1..5]-(end)`.

### H2. Neo4j `CONTAINS` bypasses fulltext index

**`src/TC.CodeGraphApi.Data.Neo4j/Neo4jGraphStore.Nodes.cs:202-245`**

`SearchNodesAsync` uses `WHERE n.name CONTAINS $pattern`, which forces a full property scan on every `CodeNode`. Migration `003_fulltext_indexes.cypher` defines a fulltext index that is never used by this query.

**Impact:** Full table scan on every search request.
**Fix:** Use `CALL db.index.fulltext.queryNodes("code_node_search", $pattern) YIELD node AS n`.

### H3. N+1 queries in GraphQueryEngine

**`src/TC.CodeGraphApi.Services/Query/GraphQueryEngine.cs:102-120`**

Loops over candidate nodes, issuing `FindEdgesByTargetAsync` per node, then nested `TraverseAsync` calls inside the loop.

**Impact:** 10 candidates = 10+ database round trips. Grows linearly with result size.
**Fix:** Batch-fetch edges for all candidate IDs in one query.

### H4. O(n*k) Chunk method

**`src/TC.CodeGraphApi.Data/MySqlGraphStore.cs:294-307`**

Uses `.Skip(i).Take(chunkSize).ToList()` in a loop. `Skip` re-enumerates from the start on each iteration, making the overall complexity O(n*k) where k is the number of chunks.

**Impact:** For 10,000 items in 500-size chunks, the inner Skip enumerates up to ~100,000 elements total.
**Fix:** Use index-based slicing or .NET's built-in `Chunk()` LINQ method.

### H5. `AllNodes` property allocates on every access

**`src/TC.CodeGraphApi.Services/Pipeline/GraphBuffer.cs:40`**

```csharp
public IReadOnlyCollection<GraphNode> AllNodes => _nodes.Values.ToList();
```

Creates a full list copy of potentially millions of nodes every time the property is read. `ConcurrentDictionary.Values` already implements `IReadOnlyCollection`.

**Impact:** Unnecessary large allocation on every access.
**Fix:** Return `_nodes.Values` directly.

### H6. No startup validation for critical configuration

**`src/TC.CodeGraphApi/Startup.cs:184-220`**

Connection strings, embedding model paths, and other critical settings are not validated at startup. Failures surface only at first use, making deployment issues hard to diagnose.

**Impact:** Delayed failure; harder to diagnose in production.
**Fix:** Validate required settings in `CreateServiceProvider` and fail fast.

### H7. OnnxEmbeddingService uses fake tokenizer

**`src/TC.CodeGraphApi.Services/Embeddings/OnnxEmbeddingService.cs`**

`SimpleTokenize()` hashes words to pseudo-token IDs in range `[1000, 30000)`. This doesn't match any real model's vocabulary (e.g., all-MiniLM-L6-v2 uses BPE tokenization). Embeddings produced are likely semantically meaningless.

**Impact:** If embeddings are used for search or similarity, results will be poor.
**Fix:** Integrate a proper BPE tokenizer matching the ONNX model.

---

## Medium

### M1. Sync-over-async `.Result` calls in production code

- **`src/TC.CodeGraphApi.Services/Query/SearchService.cs:33-34`** — `reposTask.Result` / `nodesTask.Result`
- **`src/TC.CodeGraphApi.Services/Analyzers/VitalsAnalyzer.cs:92-95`** — Four `.Result` calls after `Task.WhenAll`

After `await Task.WhenAll(...)`, the tasks are completed, so these won't deadlock. But the pattern is misleading and inconsistent with the rest of the codebase.

**Fix:** Use `await` directly or tuple deconstruction.

### M2. Race condition in AnthropicCircuitBreaker

**`src/TC.CodeGraphApi.Services/Analyzers/AnthropicCircuitBreaker.cs:17-22`**

`_circuitOpenedAt` (DateTime) is read/written across threads without synchronization. `Interlocked` doesn't work with DateTime directly.

**Impact:** Under high concurrency, circuit may not open/close correctly.
**Fix:** Convert to ticks (`long`) and use `Interlocked.Exchange`, or add a lock.

### M3. ExclusionService cache locking without volatile

**`src/TC.CodeGraphApi.Services/ExclusionService.cs:29-30`**

Double-check locking on `_cachedRules` without `volatile`. Other threads may read a stale value.

**Fix:** Add `volatile` to the field or use `Lazy<T>`.

### M4. SecurityAnalyzer cache grows unbounded

**`src/TC.CodeGraphApi.Services/Analyzers/SecurityAnalyzer.cs:100-101`**

`ConcurrentDictionary` has TTL check on read but no eviction of expired entries. Old entries accumulate forever.

**Impact:** Slow memory leak over application lifetime.
**Fix:** Switch to `IMemoryCache` with expiration, or add periodic cleanup.

### M5. Neo4j — seven sequential DELETEs in one transaction

**`src/TC.CodeGraphApi.Data.Neo4j/Neo4jGraphStore.cs:198-240`**

`DeleteAnalysisDataForProjectAsync` issues 7 separate `MATCH ... DETACH DELETE` queries sequentially within a single transaction.

**Impact:** Inefficient transaction execution; each query traverses the graph independently.
**Fix:** Consolidate related deletes or batch with APOC.

### M6. Neo4j — wiki deletion runs 5 queries for one page

**`src/TC.CodeGraphApi.Data.Neo4j/Neo4jWikiStore.cs:255-291`**

`DeletePageAsync` runs 5 sequential MATCH queries all starting from the same node.

**Fix:** Consolidate into a single query with `OPTIONAL MATCH` for children:
```cypher
MATCH (p:WikiPage {appId: $id})
OPTIONAL MATCH (p)<-[:CHILD_OF*]-(child:WikiPage)
DETACH DELETE child, p
```

### M7. Neo4j — `toLower()` prevents index usage

**`src/TC.CodeGraphApi.Data.Neo4j/Neo4jWikiStore.cs:183-200`**

`WHERE toLower(p.slug) CONTAINS toLower($pattern)` applies case normalization per row, preventing any index usage.

**Fix:** Store a lowercase variant at write time or use fulltext search.

### M8. Neo4j — no connection pool configuration

**`src/TC.CodeGraphApi.Data.Neo4j/Neo4jSessionFactory.cs:15-23`**

Driver created with all defaults. No `MaxConnectionPoolSize`, `MaxConnectionAcquireTime`, or connection lifetime configuration.

**Impact:** Defaults are fine for development but won't scale under production load without tuning.
**Fix:** Add explicit pool configuration tied to app settings.

### M9. Missing `ConfigureAwait(false)` throughout library code

Only 1 instance found in the entire codebase (`TypeScriptServerManager.cs:171`). All service/data layer code should use `ConfigureAwait(false)` to avoid capturing synchronization context unnecessarily.

**Impact:** Minor per-call overhead, adds up under high concurrency.

### M10. Bare catch block in AdminController

**`src/TC.CodeGraphApi/Controllers/AdminController.cs:250`**

```csharp
catch (Exception)
{
    return Conflict("An exclusion rule for this target already exists.");
}
```

Catches all exceptions and returns the same Conflict response regardless of cause (could be a database timeout, connection failure, etc.).

**Fix:** Catch `DbUpdateException` specifically.

### M11. FileStream returned without disposal guarantee

**`src/TC.CodeGraphApi.Services/AttachmentService.cs:62`**

Returns a raw `FileStream` in a tuple. If the caller doesn't dispose it, file handles leak.

**Fix:** Wrap in a disposable response type or document the disposal contract.

### M12. MemoryController missing username validation

**`src/TC.CodeGraphApi/Controllers/MemoryController.cs:14`**

`username` parameter not validated for null/whitespace before `ToLowerInvariant()` calls downstream. Will throw `NullReferenceException` on null input.

**Fix:** Add `if (string.IsNullOrWhiteSpace(username)) return BadRequest(...)`.

### M13. Repeated JSON deserialization on every node load

**`src/TC.CodeGraphApi.Data/MySqlGraphStore.cs:276-292`**

`MapNodeEntity` calls `DeserializeJson()` for the `Properties` dictionary on every node loaded from the database.

**Impact:** Millions of nodes = millions of JSON parse operations. Not avoidable entirely, but could benefit from System.Text.Json source generators.

### M14. No controller unit tests

No tests exist for `AdminController`, `ProjectsController`, `WikiController`, `AskController`, or `MemoryController`. Validation logic, error handling, and routing are untested.

### M15. No concurrency tests

`ExclusionService` cache locking (M3) and `AnthropicCircuitBreaker` (M2) have no multi-threaded test coverage.

### M16. Test code uses blocking `.Result`

Multiple extractor test files use `.Result` instead of `async Task` patterns:
- `TerraformExtractorTests.cs:20`
- `SqlExtractorTests.cs:20`
- `ColdFusionExtractorTests.cs:20`
- `AnsibleExtractorTests.cs:20`
- `InMemoryGraphStore.cs:183`

---

## Low

### L1. Inconsistent null checking style

18+ files mix `!= null` and `is not null`. No functional difference but reduces consistency.

**Fix:** Standardize on C# 9+ `is not null`.

### L2. Silent string truncation without logging

**`src/TC.CodeGraphApi.Data/MySqlGraphStore.Nodes.cs:14-15`**

Node names silently truncated to 1000 characters with no warning logged.

**Fix:** Log a warning when truncation occurs.

### L3. Empty `_GlobalUsings.cs` files

**`src/TC.CodeGraphApi/_GlobalUsings.cs`** and **`src/TC.CodeGraphApi.Console/_GlobalUsings.cs`**

If empty, remove them.

### L4. AskController returns empty 400 response

**`src/TC.CodeGraphApi/Controllers/AskController.cs:25-29`**

Sets `Response.StatusCode = 400` but writes no body. Clients get no indication of what's wrong.

**Fix:** Write an error message to the response body.

### L5. WikiController does too much in one action

**`src/TC.CodeGraphApi/Controllers/WikiController.cs:33-62`**

Single `GetPage` method handles revisions, attachments, and regular pages via string parsing. Should be separate endpoints.

### L6. Hardcoded magic values

| Value | Location |
|-------|----------|
| `TsPort = 3100` | `Startup.cs:7` |
| `MaxDepth = 3` | `WikiService.cs:10` |
| `BatchSize = 500` | `CodeGraphStorageOptions.cs:8` |
| `Regex timeout = 5s` | `AdminService.cs:117` |

These should be configurable via app settings.

### L7. No JSON source generators

All serialization is reflection-based. Adding `[JsonSerializable]` source generators for hot-path types (`GraphNode`, `GraphEdge`, API DTOs) would eliminate reflection overhead.

### L8. Neo4j — no transaction timeouts or retry logic

No timeout bounds on long-running transactions. No retry for transient Neo4j failures (network blips, leader switches in cluster mode).

### L9. Blocking async call in Startup

**`src/TC.CodeGraphApi/Startup.cs:174`**

```csharp
exclusionService.SeedFromConfigAsync(gitLabOptions.ExcludedGroups).GetAwaiter().GetResult();
```

Can cause deadlocks in some hosting scenarios. Use async startup or synchronous alternative.

### L10. Fire-and-forget without completion tracking

**`src/TC.CodeGraphApi/Program.cs:29`**

```csharp
_ = Task.Run(async () => { await mcpDocService.RegenerateAsync(); ... });
```

No way to track completion or ensure it finishes before shutdown. Exception handling is present but completion is unobserved.

---

## Positives

Things the codebase does well:

- **Neo4j session lifecycle** — All 158+ session usages properly use `await using`. No leaks.
- **Query parameterization** — Both Dapper (MySQL) and Neo4j queries are fully parameterized. No injection risk.
- **Neo4j batch upserts** — Correctly uses `UNWIND` pattern for bulk operations.
- **Neo4j migration system** — Solid, with `IF NOT EXISTS` guards and tracked history.
- **Controller thinness** — Controllers consistently delegate to services.
- **HTTP status codes** — Consistent and appropriate use of 200/400/404/409.
- **EF Core + Dapper hybrid** — Good separation of concerns between CRUD and complex queries.
- **Async disposal** — `await using` pattern used consistently for database connections.
- **MySQL indexing** — Good coverage on high-selectivity columns and foreign keys.
- **Logging** — Consistent use of `ILogger<T>` with structured logging.

---

## Priority Fix Order

Quick wins with high impact:

| # | Issue | Impact | Effort |
|---|-------|--------|--------|
| 1 | C1 — Over-fetching in ProjectQueryService | High perf | Low |
| 2 | C3 — Unbounded result sets | High perf/stability | Low |
| 3 | H5 — AllNodes `.ToList()` on property | High perf | Trivial |
| 4 | H4 — Chunk() O(n*k) | Medium perf | Low |
| 5 | H2 — CONTAINS bypasses fulltext index | High perf (Neo4j) | Low |
| 6 | C2 — Unbatched Neo4j deletion | High stability | Low |
| 7 | H1 — BFS traversal round trips | Medium perf (Neo4j) | Medium |
| 8 | H3 — N+1 in GraphQueryEngine | Medium perf | Medium |
| 9 | M2 — CircuitBreaker race condition | Medium correctness | Low |
| 10 | H7 — Fake ONNX tokenizer | High correctness | Medium |
