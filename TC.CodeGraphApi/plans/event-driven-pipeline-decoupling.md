# Event-Driven Pipeline Decoupling

**Status:** Implemented
**Created:** 2026-03-20
**Context:** The indexing pipeline currently runs several heavyweight steps synchronously within `ProcessRepositoryConsumer`. Introducing new messages and consumers at natural boundaries would improve resilience, enable independent retry, and allow parallel scaling.

The codebase already has the pattern: `ProcessRepository` message → `ProcessRepositoryConsumer`. These proposals extend that proven approach.

---

## 1. Incremental Cross-Repo Linking After Indexing

**Current state:** Cross-repo linking is manual — triggered via `AdminController.Link()`. Repos can sit unlinked indefinitely.

**Change:** After a repo finishes indexing in `ProjectService.ProcessRepositoryCore()`, publish a message that triggers incremental cross-repo linking for just that repo's edges.

**Message:** `RepositoryIndexingCompleted { RepositoryName, LocalPath, IndexedAt, NodeCount }`
**Consumer:** Calls `CrossRepoLinker` scoped to the newly-indexed repo's outbound edges (HTTP, messaging, NuGet).

**Why it matters:** This is the core "self-maintaining" promise — the graph should wire itself up automatically, not wait for a human to click a button.

**Files:**
- `ProjectService.cs` — publish after indexing completes (~line 118)
- `CrossRepoLinker.cs` — add an incremental method that links only edges from one repo
- New: `Models/Messages/RepositoryIndexingCompleted.cs`
- New: `Consumers/RepositoryIndexingCompletedConsumer.cs`

---

## 2. Decouple Vitals Computation from Indexing

**Current state:** `ProjectService.ProcessRepositoryCore()` runs vitals computation synchronously after indexing (lines 122-136). If vitals fails (git operations, Claude call), the entire ProcessRepository consumer fails.

**Change:** The `RepositoryIndexingCompleted` message from item 1 can also trigger vitals computation via a second consumer, completely independent of cross-repo linking.

**Consumer:** `ComputeVitalsConsumer` — calls `VitalsAnalyzer.AnalyzeAsync()` for the indexed repo.

**Why it matters:** Vitals runs 4 parallel git operations + a Claude call. That's too much to couple to the indexing consumer's success/failure. Independent retry means a transient Claude API failure doesn't re-trigger the entire indexing pipeline.

**Files:**
- `ProjectService.cs` — remove direct vitals call, rely on the event from item 1
- New: `Consumers/ComputeVitalsConsumer.cs`

---

## 3. Decouple Analysis Submission from Indexing

**Current state:** `ProjectService` submits analysis to the Anthropic Batch API synchronously within the ProcessRepository consumer (lines 139-158). A slow or failed submission blocks the consumer.

**Change:** Publish an event after indexing; a separate consumer handles batch submission to Anthropic.

**Message:** Reuse `RepositoryIndexingCompleted` or a dedicated `AnalysisRequested { RepositoryName, Priority }`
**Consumer:** Calls `BatchAnalysisService.SubmitBatchAsync()` with circuit breaker protection.

**Why it matters:** Anthropic API availability shouldn't determine whether indexing "succeeds." Submission can retry independently with backoff. Also enables priority-based submission (urgent repos first).

**Files:**
- `ProjectService.cs` — remove direct analysis submission
- New: `Consumers/AnalysisRequestedConsumer.cs` (or fold into `RepositoryIndexingCompletedConsumer`)

---

## 4. Analysis Results → Synthesis Decoupling

**Current state:** `BatchAnalysisService.ProcessCompletedBatchesAsync()` processes per-project results and immediately calls `SynthesizeRepoSummaryAsync()` (line ~200). If synthesis fails, the entire batch result processing is affected.

**Change:** After per-project results are stored, publish an event. A consumer handles synthesis independently.

**Message:** `ProjectAnalysisResultsProcessed { RepositoryName, AnthropicBatchId, ProcessedProjectCount }`
**Consumer:** Calls `SynthesizeRepoSummaryAsync()` with independent retry.

**Why it matters:** Per-project analysis results have immediate value for queries and the UI. Synthesis (a separate Claude call) shouldn't gate their availability. If synthesis fails, project-level insights are still accessible.

**Files:**
- `BatchAnalysisService.cs` — publish event after storing results (~line 192)
- New: `Models/Messages/ProjectAnalysisResultsProcessed.cs`
- New: `Consumers/ProjectAnalysisResultsProcessedConsumer.cs`

---

## 5. Synthesis Complete → CODEGRAPH.md Generation Decoupling

**Current state:** After synthesis completes in `BatchAnalysisService.Synthesis.cs`, CODEGRAPH.md is written synchronously via Git I/O (lines ~155-156). Filesystem/git failures block the pipeline.

**Change:** Publish an event after synthesis succeeds. A consumer handles the Git commit independently.

**Message:** `AnalysisSynthesisCompleted { RepositoryName, SummaryText }`
**Consumer:** Writes CODEGRAPH.md to the repo and commits. Can retry independently on git lock/filesystem errors.

**Why it matters:** Git operations (clone, write, commit, push) are the most failure-prone part of the pipeline. Decoupling means synthesis results are stored even if the commit fails. The consumer can retry with backoff on git lock contention.

**Files:**
- `BatchAnalysisService.Synthesis.cs` — publish event after synthesis stored
- New: `Models/Messages/AnalysisSynthesisCompleted.cs`
- New: `Consumers/CodeGraphDocGenerationConsumer.cs`

---

## 6. Convention Updated → Doc Regeneration

**Current state:** Convention wiki pages are updated via `ConventionService` with direct DB writes. No downstream notification. CODEGRAPH.md files that reference conventions become stale silently.

**Change:** Publish an event when a convention page is created or updated.

**Message:** `ConventionUpdated { ConventionSlug, Title, Revision }`
**Consumer:** Could trigger selective re-analysis of repos whose CODEGRAPH.md references the changed convention.

**Why it matters:** Conventions define how code should be written. When a convention changes, the documentation describing whether repos follow it should be refreshed. This is a lower-priority but natural extension of the self-maintaining principle.

**Files:**
- `ConventionService.cs` — publish after create/update (~lines 91-117)
- New: `Models/Messages/ConventionUpdated.cs`
- New: `Consumers/ConventionUpdatedConsumer.cs`

---

## 7. Repository Removed → Cascading Cleanup

**Current state:** No automated cleanup when a repo disappears from GitLab. Orphaned nodes, edges, analysis records, and cross-repo links persist indefinitely.

**Change:** When the sync worker detects a repo no longer exists in GitLab, publish a removal event.

**Message:** `RepositoryRemoved { RepositoryName, RemovedAt }`
**Consumer:** Deletes nodes, edges, analysis records, cross-repo edges, and optionally removes the CODEGRAPH.md commit.

**Why it matters:** Self-maintaining means self-cleaning. Without this, the graph accumulates phantom repos over time, degrading query accuracy.

**Files:**
- `RepositorySyncWorker` — publish when repo not found in GitLab
- New: `Models/Messages/RepositoryRemoved.cs`
- New: `Consumers/RepositoryRemovedConsumer.cs`

---

## Resulting Pipeline Flow

**Before (synchronous chain):**
```
ProcessRepository → Index → Vitals → Submit Analysis → [wait] → Process Results → Synthesize → Write CODEGRAPH.md
```

**After (event-driven):**
```
ProcessRepository → Index → publish RepositoryIndexingCompleted
                                ├─→ ComputeVitalsConsumer
                                ├─→ CrossRepoLinkingConsumer
                                └─→ AnalysisRequestedConsumer → Submit Batch
                                                                    │
                                        [Anthropic completes batch] │
                                                                    ▼
                                              ProcessBatchResults → publish ProjectAnalysisResultsProcessed
                                                                        └─→ SynthesisConsumer → publish AnalysisSynthesisCompleted
                                                                                                    └─→ CodeGraphDocGenerationConsumer
```

Each step retries independently. Failure at any node doesn't cascade backward.

---

## Implementation Order

| Priority | Item | Reason |
|----------|------|--------|
| 1 | Item 1 (cross-repo linking) | Most impactful — closes the "self-maintaining" gap |
| 2 | Items 2+3 (vitals + analysis decoupling) | Natural since they share the same trigger event |
| 3 | Item 4 (results → synthesis) | Straightforward split of existing sequential code |
| 4 | Item 5 (synthesis → CODEGRAPH.md) | Isolates the most failure-prone I/O |
| 5 | Item 7 (repo removal cleanup) | Important for long-term graph health |
| 6 | Item 6 (convention updates) | Nice-to-have, lower urgency |

Items 1-3 can ship together since they all key off `RepositoryIndexingCompleted`.

---

## Shared Infrastructure Notes

- All messages go in `TC.CodeGraphApi.Models/Messages/` (Models has zero dependencies — ideal for message contracts)
- All consumers go in `TC.CodeGraphApi/Consumers/` or `TC.CodeGraphJobs/Consumers/` depending on whether they should run in the API host or the jobs worker
- Register consumers in `Startup.cs` via MassTransit's `AddConsumer<T>()`
- Use `AnthropicCircuitBreaker` (already exists) for consumers that call Claude
- Consider idempotency: consumers should handle duplicate messages gracefully (use `IndexedAt`/`CompletedAt` timestamps as dedup keys)