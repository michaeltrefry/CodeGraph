# CodeGraph Refactoring Plan: Performance & Clean Code

This plan was drafted before the repository rename/restructure and before the storage layer moved off the old MySQL-backed shape. Where legacy names appear below, map them as follows:

- `TC.CodeGraphApi.*` -> `CodeGraph.*`
- `MySqlGraphStore` -> `Neo4jGraphStore`
- `ClaudeCodeAnalyzer` -> the former direct-analysis stack that has since been removed

## Context

CodeGraph indexes ~620 repositories into a MySQL knowledge graph. The API and batch analysis services have grown organically and now contain **N+1 query patterns**, **full-table scans loaded into memory**, **long methods violating SRP**, **overly broad exception handling**, and **near-zero test coverage** on the largest service classes (BatchAnalysisService at 940 lines, IndexingPipeline at 518 lines). This plan addresses performance bottlenecks first, then code quality, then test gaps.

---

## Phase 0: Data Layer Primitives (unblocks all performance fixes)

### 0.1 Add `FindNodesByIdBatchAsync` to IGraphStore
- **Files:** `src/TC.CodeGraphApi.Data/IGraphStore.cs`, `MySqlGraphStore.cs`, test InMemoryGraphStore
- **Change:** New method `Task<Dictionary<long, GraphNode>> FindNodesByIdBatchAsync(IReadOnlyList<long> ids)`. Dapper `SELECT ... WHERE id IN (...)`, chunked by 1000.
- **Verify:** Unit test — insert 5 nodes, batch-fetch 3, assert all returned. Test empty input and missing IDs.

### 0.2 Add `GetNodeCountsByDotnetProjectAsync` to IGraphStore
- **Files:** `src/TC.CodeGraphApi.Data/IGraphStore.cs`, `MySqlGraphStore.cs`, test InMemoryGraphStore
- **Change:** New method returning `Dictionary<string, Dictionary<string, int>>` via `SELECT dotnet_project, label, COUNT(*) FROM nodes WHERE project = @project GROUP BY dotnet_project, label`.
- **Verify:** Unit test with nodes across multiple dotnet projects and labels.

### 0.3 Add `SearchNodesAsync` overload with dotnet_project filter
- **Files:** `src/TC.CodeGraphApi.Data/IGraphStore.cs`, `MySqlGraphStore.cs`
- **Change:** Add optional `string? dotnetProject` parameter to `SearchNodesAsync` so filtering happens at the DB level instead of loading 10K nodes into memory.
- **Verify:** Existing search tests still pass. New test filtering by dotnet project.

---

## Phase 1: Critical Performance Fixes

### 1.1 Fix N+1 in `NodesController.ResolveNeighborsAsync`
- **File:** `src/TC.CodeGraphApi/Controllers/NodesController.cs:89-99`
- **Change:** Replace `foreach` loop with single `FindNodesByIdBatchAsync(neighborIds)` call.
- **Verify:** Hit `/api/nodes/{id}` for a node with 50+ edges — confirm single batch query in logs.

### 1.2 Fix N+1 in `CrossRepoLinker`
- **File:** `src/TC.CodeGraphApi.Services/CrossRepoLinker.cs:150,165,171,176,237,243`
- **Change:** In each linking method, collect all needed node IDs upfront, call `FindNodesByIdBatchAsync` once, replace loop lookups with dictionary access. Remove trivial wrapper methods at lines 274-278.
- **Verify:** Existing `CrossRepoLinkerTests` pass. Link a repo with 50+ messaging edges — confirm 1-2 queries not 200+.

### 1.3 Fix full table scan in `ProjectsController.Detail`
- **File:** `src/TC.CodeGraphApi/Controllers/ProjectsController.cs:50-61`
- **Change:** Replace `store.SearchNodesAsync(name, "%", limit: 100000)` + in-memory GroupBy with `GetNodeCountsByDotnetProjectAsync(name)`. Eliminates loading 100K rows.
- **Verify:** `/api/projects/{name}` returns same response shape. Compare before/after response.

### 1.4 Fix in-memory filtering in `ProjectsController.Nodes`
- **File:** `src/TC.CodeGraphApi/Controllers/ProjectsController.cs:104-109`
- **Change:** Use the new `dotnetProject` parameter on `SearchNodesAsync` instead of fetching 10K nodes and filtering in-memory.
- **Verify:** `/api/projects/{name}/nodes?dotnetProject=TC.OrdersApi.Services` returns correct filtered results.

---

## Phase 2: Secondary Performance Fixes

### 2.1 Single-pass edge grouping in `BatchAnalysisService.BuildProjectPrompt`
- **File:** `src/TC.CodeGraphApi.Services/BatchAnalysisService.cs:366-390`
- **Change:** Replace 3 separate LINQ filter+group passes with one `foreach` loop building `outboundBySource`, `inboundByTarget`, `childrenByParent` simultaneously.
- **Verify:** Run batch analysis on a test repo — compare prompt output before/after (must be identical).

### 2.2 Cache `.csproj` discovery in `IndexingPipeline`
- **File:** `src/TC.CodeGraphApi.Services/IndexingPipeline.cs:295,368`
- **Change:** Call `Directory.GetFiles("*.csproj", AllDirectories)` once in `IndexProjectAsync`, pass results to both `CreateStructuralNodes` and `ExtractNuGetReferences`.
- **Verify:** Existing tests pass. Log output shows single csproj enumeration.

### 2.3 Add secondary name index to `GraphBuffer`
- **File:** `src/TC.CodeGraphApi.Services/GraphBuffer.cs:27-31`
- **Change:** Add `ConcurrentDictionary<string, ConcurrentBag<GraphNode>> _nodesByName` populated in `AddNode()`. `FindByName()` does O(1) lookup instead of linear scan.
- **Verify:** Existing tests pass. Add unit test with 1000 nodes verifying FindByName performance.

---

## Phase 3: Method Extraction (SRP)

### 3.1 Break up `BatchAnalysisService.BuildProjectPrompt` (~170 lines)
- **File:** `src/TC.CodeGraphApi.Services/BatchAnalysisService.cs:357-528`
- **Extract to:**
  - `GroupEdgesByRole(edges)` — returns the three edge dictionaries (incorporates 2.1)
  - `FormatClassNode(node, edges, nodeById)` — formats one class/interface section
  - `BuildAnalysisInstructions(repoName, projectName)` — instruction header
- **Verify:** Diff prompt output before/after for a known repo — must be byte-identical.

### 3.2 Break up `IndexingPipeline.IndexProjectAsync` (~150 lines)
- **File:** `src/TC.CodeGraphApi.Services/IndexingPipeline.cs:39-188`
- **Extract to:**
  - `ExtractPhaseAsync(...)` — discovery, structural nodes, analyzers, per-file extraction, NuGet
  - `ResolvePhase(buffer)` — imports, calls, type references, stub nodes
  - `FlushPhaseAsync(project, buffer)` — batch upsert nodes, resolve edges, insert edges, file hashes
- **Verify:** All existing tests pass unchanged.

### 3.3 Break up `BatchAnalysisService.ReadCompressedSource` (~90 lines)
- **File:** `src/TC.CodeGraphApi.Services/BatchAnalysisService.cs:636-727`
- **Extract to:**
  - `ResolveSourceFilePath(repoPath, filePath)` — path validation
  - `CompressMethodBodies(lines)` — brace-tracking compression
- **Verify:** Add unit test for `CompressMethodBodies` with a known C# class.

### 3.4 Break up `BatchAnalysisService.ProcessCompletedBatchesAsync` (~85 lines)
- **File:** `src/TC.CodeGraphApi.Services/BatchAnalysisService.cs:141-228`
- **Extract to:**
  - `CheckBatchStatusAsync(http, pending)` — polls API
  - `StreamBatchResultsAsync(http, pending)` — yields deserialized results
  - `PostProcessBatchAsync(repo, batchId)` — synthesis + CODEGRAPH.md
- **Verify:** Process a real batch — compare DB state before/after.

---

## Phase 4: Exception Handling & Validation

### 4.1 Narrow exception handlers
- **Files and changes:**
  - `BatchAnalysisService.cs:314-317` — catch `DbUpdateException`/`MySqlException` instead of `Exception`
  - `ClaudeCodeAnalyzer.cs:84-95` — catch `HttpRequestException`, `JsonException` specifically; let OOM propagate
  - `GraphAssistant.cs:133-136` — catch `HttpRequestException`, `JsonException`, `InvalidOperationException`
  - `IndexingPipeline.cs:100-105` — add comment explaining why broad catch is intentional (Roslyn), separate `OperationCanceledException`
  - `AskController.cs:35-42` — add `when (ex is not OutOfMemoryException)` guard
- **Verify:** Existing tests pass. Manual verification of error paths.

### 4.2 Add input validation at API boundaries
- **Files:**
  - `AdminController.cs` — validate URL format for entries with `::http`, add max repo count guard
  - `AskController.cs` — return `BadRequest` if `request.Question` is null/empty (before setting response headers)
- **Verify:** Manual test with empty/null inputs.

### 4.3 Fix sync I/O in async contexts
- **Files:**
  - `BatchAnalysisService.cs:665` — `File.ReadAllLines()` -> `await File.ReadAllLinesAsync()`
  - `CodeGraphMcpServer.cs:391` — `File.ReadLines().FirstOrDefault()` -> `await File.ReadAllLinesAsync()` + `.FirstOrDefault()`
  - `CodeGraphMcpServer.cs:425` — `File.ReadAllText()` -> `await File.ReadAllTextAsync()`
- **Verify:** Build succeeds. No functional change.

---

## Phase 5: Design Cleanup

### 5.1 Forward CancellationToken in CrossRepoLinker
- **File:** `src/TC.CodeGraphApi.Services/CrossRepoLinker.cs`
- **Change:** Pass `ct` through to all store calls that accept it. Add `// TODO` for IGraphStore methods that don't yet accept CancellationToken.

### 5.2 Remove dead code
- `CrossRepoLinker.cs:274-278` — remove wrapper methods (already replaced by batch in 1.2)
- `IndexingPipeline.cs:450-453` — remove no-op `ResolveTypeReferences` and its call site

### 5.3 Centralize JsonSerializerOptions
- **File:** New `src/TC.CodeGraphApi.Models/CodeGraphJsonDefaults.cs`
- **Change:** Shared static `CamelCase` and `SnakeCase` options. Replace per-class definitions in `BatchAnalysisService`, `ClaudeCodeAnalyzer`, `MySqlGraphStore`.

### 5.4 Introduce `PromptBuildContext` record
- **File:** `src/TC.CodeGraphApi.Services/BatchAnalysisService.cs`
- **Change:** Replace 7-parameter `BuildProjectPrompt` with a context record. Improves readability and enables future parameter additions without method signature changes.

---

## Phase 6: Test Coverage

### 6.1 BatchAnalysisService tests (HIGHEST PRIORITY — 940 lines, 0 tests)
- **File:** New `src/TC.CodeGraphApi.Tests/Services/BatchAnalysisServiceTests.cs`
- **Test cases:**
  - `BuildProjectPrompt` output includes all class/interface nodes with edges
  - `CompressMethodBodies` preserves signatures, collapses bodies (after 3.3 extraction)
  - Edge grouping produces correct dictionaries
  - Empty project produces valid minimal prompt
  - JSON fence stripping handles edge cases

### 6.2 IndexingPipeline tests (518 lines, 0 tests)
- **File:** New `src/TC.CodeGraphApi.Tests/Services/IndexingPipelineTests.cs`
- **Test cases:**
  - Structural nodes created correctly (Repository, Folder, File)
  - NuGet references extracted from csproj
  - Incremental indexing skips unchanged files
  - Stub nodes created for unresolved references

### 6.3 Controller integration tests
- **File:** New `src/TC.CodeGraphApi.Tests/Controllers/ControllerTests.cs`
- **Test cases:**
  - `NodesController.Detail` returns neighbors via batch (not N+1)
  - `ProjectsController.Detail` returns node counts without full scan
  - `AskController` rejects empty questions
  - Pagination works correctly

### 6.4 Job tests
- **File:** New `src/TC.CodeGraphApi.Tests/Jobs/ProcessRepositoriesJobTests.cs`
- **Test cases:**
  - Parses repo entries with `::` delimiter correctly
  - Handles empty/null repos argument
  - Publishes messages for each valid repo

---

## Verification Strategy

After each phase:
1. `dotnet build src/TC.CodeGraphApi.sln` — must succeed
2. `dotnet test` — all existing tests must pass
3. For performance phases (0-2): manually hit affected API endpoints and confirm response correctness
4. For Phase 1 specifically: use MySQL `SHOW PROCESSLIST` or query logging to confirm N+1 patterns are eliminated

---

## Execution Priority

| Order | Phase | Focus | Risk |
|-------|-------|-------|------|
| 1 | Phase 0 | Data layer primitives | Very Low |
| 2 | Phase 1 | Critical N+1 and full-scan fixes | Low |
| 3 | Phase 2 | Secondary perf (single-pass, caching) | Very Low |
| 4 | Phase 6.1-6.2 | Tests for largest untested classes | Very Low |
| 5 | Phase 3 | Method extraction (SRP) | Low |
| 6 | Phase 4 | Exception handling, validation | Low |
| 7 | Phase 5 | Design cleanup | Low |
| 8 | Phase 6.3-6.4 | Controller + job tests | Very Low |

> **Note:** Phase 6.1-6.2 (tests) is ordered *before* Phase 3 (method extraction) so that extracted methods have a safety net.
