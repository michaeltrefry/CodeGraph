# Neo4j Native Schema Refactor

**Status:** Implemented
**Created:** 2026-04-07
**Context:** CodeGraph was originally persisting the main code graph with generic `CodeNode` nodes plus `EdgeRecord` nodes. For a fresh database with no migration baggage, we replaced that shape with a more native Neo4j graph model aimed at better traversal performance, clearer semantics, and better LLM consumption.

---

## Goals

- Use Neo4j as an actual graph for the main code graph, not a graph-shaped record store
- Preserve the existing conceptual model (`GraphNode`, `GraphEdge`, `NodeLabel`, `EdgeType`)
- Improve traversal performance for hot query paths like call tracing and consumer/publisher lookups
- Make code element semantics more explicit for Cypher, MCP tools, and LLM prompting
- Keep a compatibility bridge where helpful so upstream services do not need a full rewrite

---

## Key Decisions

### 1. Main graph edges are native Neo4j relationships

The main graph now stores binary facts like `CALLS`, `IMPLEMENTS`, `INJECTS`, `PUBLISHES`, and `CONSUMES` as native relationships between `:CodeNode` nodes instead of persisting `EdgeRecord` nodes.

**Why:**
- Better fit for Neo4j traversal
- Less indirection in reads and path expansion
- Cleaner Cypher for analysis and query logic

### 2. Code nodes use multi-label storage

Nodes still share the `:CodeNode` base label, but now also get semantic labels derived from `NodeLabel`, for example:

- `:CodeNode:Type:Class`
- `:CodeNode:Member:Method`
- `:CodeNode:Messaging:Event`

**Why:**
- Type-specific queries become more direct
- Better indexing and filtering options later
- More explicit semantics for humans and machines

### 3. Important semantic properties are promoted

Selected values that were previously only buried in the JSON `properties` bag are now promoted onto the Neo4j node or relationship directly when present.

Examples:
- Node-side: `signature`, `return_type`, `is_async`, `is_entry_point`, `http_method`, `route_template`, `queue_name`, `exchange_name`, `lifetime`
- Edge-side: `confidence`, `confidence_band`, `extractor`, `inferred`, `source_repo`, `target_repo`, `url_pattern`, `http_method`

**Why:**
- Better filtering and future indexing options
- Better support for compact, high-signal retrieval and prompting

### 4. Compatibility was preserved where inexpensive

The persisted node `label` property and JSON `properties` bag were retained even though the graph now also uses richer labels and promoted properties.

**Why:**
- Lower blast radius across the query, analysis, and assistant layers
- Easier incremental cleanup later

---

## Files Changed

### Schema

- `cypher/migrations/001_schema.cypher`

### Neo4j store implementation

- `src/CodeGraph.Data.Neo4j/Neo4jGraphStore.cs`
- `src/CodeGraph.Data.Neo4j/Neo4jGraphStore.Nodes.cs`
- `src/CodeGraph.Data.Neo4j/Neo4jGraphStore.Edges.cs`
- `src/CodeGraph.Data.Neo4j/Neo4jGraphStore.Analysis.cs`

### Analysis prompt and test compatibility follow-through

- `src/CodeGraph.Services/Analyzers/BatchAnalysisService.cs`
- `src/CodeGraph.Services/Analyzers/BatchAnalysisService.Prompts.cs`
- `src/CodeGraph.Tests/Extractors/InMemoryGraphStore.cs`

### Test compatibility fix encountered during rollout

- `src/CodeGraph.Jobs.Tests/Jobs/JobTestDoubles.cs`

---

## What Changed

### Schema migration

- Removed `EdgeRecord` schema/index assumptions from the main graph model
- Kept `CrossRepoEdge` as a separate stored artifact
- Expanded the fulltext index to include `filePath`

### Node persistence

- `UpsertNodeAsync` and `UpsertNodeBatchAsync` now assign semantic Neo4j labels derived from `NodeLabel`
- Common semantic properties are promoted to top-level Neo4j properties
- The existing `label` property remains populated as a compatibility shim

### Edge persistence

- `InsertEdgeAsync` and `InsertEdgeBatchAsync` now create native relationships grouped by `EdgeType`
- Relationship reads (`FindEdgesBySourceAsync`, `FindEdgesByTargetAsync`, `FindAllEdgesByTypeAsync`, traversal) now read native relationships directly

### Analysis graph reads

- Batch-analysis support methods now gather graph context from native relationships instead of `EdgeRecord` nodes
- Child-node reads now traverse `[:DEFINES|:DEFINES_METHOD]`

### Analysis prompt building and test doubles

- Structural-edge filtering now treats `DEFINES_METHOD` consistently with `DEFINES`
- Project-analysis prompt generation now includes method children reached through `DEFINES_METHOD`
- The in-memory graph store used by tests mirrors that same child-edge behavior

---

## Benefits Expected

- Faster traversal-heavy graph queries
- Less awkward Cypher in graph reads
- Better semantic clarity for both people and LLMs
- Better future options for label-specific indexes and query tuning
- A cleaner foundation for later semantic projection work, if needed

---

## Follow-Up Optimization Ideas

These are refinements, not signs that the base direction is wrong.

### 1. Evaluate edge-type simplification

Potential future cleanup:
- consolidate `CONTAINS_*` variants into a smaller containment model
- revisit whether `DEFINES` and `DEFINES_METHOD` should stay distinct long-term

### 2. Revisit cross-repo edge representation

`CrossRepoEdge` is still stored as a node-like artifact. That may remain fine, but if cross-repo traversals become hot, consider whether a second native-relationship projection would help.

### 3. Promote more properties only if query patterns justify it

Avoid over-promoting everything. Add top-level properties when:
- they become common query filters
- they materially improve prompt construction
- they would benefit from indexing

### 4. Add label-specific indexes after real usage data

Possible candidates later:
- route template lookups
- entry-point methods
- messaging topology fields
- repo-scoped type/member name lookups by label family

---

## Open Questions

- Do we want to keep both `DEFINES` and `DEFINES_METHOD`, or normalize them later?
- Should `CrossRepoEdge` stay as a separate artifact model, or eventually get a native traversal projection?
- Which promoted properties actually become hot query filters once the graph is in regular use?
- Should we eventually expose more of the new node-label semantics through MCP schema/tool responses?

---

## Outcome

This refactor should be treated as the new base schema direction for CodeGraph: native relationships for the main graph, semantic multi-label nodes, and selective promotion of important metadata. Further changes should mostly be tuning and refinement on top of this foundation rather than another structural rewrite.
