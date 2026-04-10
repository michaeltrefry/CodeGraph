# Memory Agent UX Hardening Plan

## Status Snapshot

Last updated: 2026-04-10

Scope:

- evaluate whether the current claim-centric memory implementation is actually good for agent use
- document what is already materially better than the legacy memory graph
- identify the remaining friction points that still make agent behavior clunkier than it should be
- propose the next hardening pass as an ergonomics and operations effort, not another storage-model redesign

Primary implementation files today:

- MCP tools: [MemoryMcpServer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Assistant/MemoryMcpServer.cs)
- retrieval layer: [MemoryRetrievalService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Memory/MemoryRetrievalService.cs)
- Neo4j adapter: [Neo4jMemoryGraphStore.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data.Neo4j/Neo4jMemoryGraphStore.cs)
- REST API: [MemoryController.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/Controllers/MemoryController.cs)
- current repo guidance: [AGENTS.md](/Users/michael/Repos/CodeGraph/AGENTS.md)
- target design baseline: [memory-claim-graph-spec.md](/Users/michael/Repos/CodeGraph/plans/memory-claim-graph-spec.md)
- implementation history: [memory-claim-neo4j-implementation-plan.md](/Users/michael/Repos/CodeGraph/plans/memory-claim-neo4j-implementation-plan.md)

## Executive Summary

The redesign is successful on architecture.

The system is now much better for agents than the legacy entity-summary and `RELATES_TO` model because:

- truth is claim-centric
- retrieval is structured-first
- search combines exact, lexical, and vector recall before graph expansion
- agents can iteratively deepen through bundles and frontier expansion instead of relying on one-shot prose

The remaining gaps are mostly product and contract ergonomics:

- write ergonomics are still awkward for agents
- one important read tool still defaults to prose
- cleanup and maintenance operations are underpowered
- there is not yet a polished "agent contract" for the recommended retrieval workflow
- operational visibility for memory writes, migrations, and index health is still thin

Conclusion:

- no further major schema redesign is needed
- the next phase should optimize for agent usability, safety, and operability

## What Is Already Good For Agents

### 1. The Truth Model Is Now Correct

The current implementation matches the intended memory shape in the spec:

- `MemoryClaim` is the primary truth surface
- `MemoryEntity` is a stable anchor, not the primary fact store
- `MemoryObservation` and `MemoryEvidence` exist as explicit first-class records
- `ACTIVE_RELATES_TO` is now a derived convenience edge, not the source of truth

This is a major improvement over the old model because agents reason more reliably over atomic fact records than over accumulated summaries.

### 2. Retrieval Follows An Agent-Friendly Pattern

The current tool set supports a sane iterative retrieval loop:

1. `search_memory`
2. `get_memory_subgraph`
3. `get_entity_bundle` or `get_claim_bundle`
4. `expand_memory_frontier`
5. `render_memory_summary` only when human-readable output is needed

This is much closer to how a good agent actually works than the earlier "query once and read prose" approach.

### 3. Retrieval Quality Is Meaningfully Better

The retrieval layer now does:

- exact recall
- lexical recall
- vector recall
- bounded graph expansion

This is the right combination for agent use. It gives agents a better chance of finding relevant memory from either IDs, natural language, or semantically related phrasing.

### 4. Live Behavior Is Now Good Enough To Trust

The recent live smoke checks validated that:

- claim-centric writes work end-to-end
- claim bundles return precise fact and evidence data
- entity bundle scoping is now correct
- embeddings are loading again and vector results are active

That moves this from "theoretical redesign" into "actually usable by agents in production."

## Current Friction Points

### P1. `store_memory_v2` Is Still Awkward For Agents

Current issue:

- the MCP tool takes a single raw JSON string rather than structured MCP arguments

Why this matters:

- agents are more likely to make quoting and serialization mistakes
- validation errors are less actionable than they could be
- the call shape is inconsistent with the otherwise structured retrieval tools

Current implementation:

- [MemoryMcpServer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Assistant/MemoryMcpServer.cs)

Desired direction:

- expose typed MCP arguments for `entities`, `claims`, and `evidence`
- keep a string-based compatibility path only if needed for external callers

### P1. `query_memory` Still Returns Prose As Its Main Contract

Current issue:

- `query_memory` returns `FormattedText`

Why this matters:

- agents should prefer structured results by default
- prose output is harder to inspect, score, chain, and filter programmatically
- it encourages the old retrieval habit the redesign was intended to replace

Current implementation:

- [MemoryMcpServer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Assistant/MemoryMcpServer.cs)
- [MemoryRetrievalService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Memory/MemoryRetrievalService.cs)

Desired direction:

- either de-emphasize `query_memory` in agent guidance
- or make it return a structured result plus optional rendered summary

### P1. There Is No First-Class Memory Cleanup Surface

Current issue:

- there is no MCP or REST delete/update/admin tool for memory records
- smoke cleanup had to be done directly in Neo4j

Why this matters:

- agents need a safe, bounded way to retract obvious junk, test data, or bad imports
- operators need admin-grade cleanup without shelling into Neo4j

Desired direction:

- add explicit admin-only tools such as:
  - `delete_memory_claims`
  - `delete_memory_entities`
  - `delete_memory_by_source`
  - `delete_memory_test_data`

Guardrails:

- require admin auth
- require bounded filters
- return dry-run counts before destructive execution

### P1. Write Acknowledgement Is Too Thin

Current issue:

- writes are asynchronous and return "processing" only
- there is no first-class write receipt or completion polling contract

Why this matters:

- agents cannot easily know when a memory write is durable
- smoke tests and chained workflows need a stable completion check

Desired direction:

- return a write receipt id
- expose a lightweight status endpoint or MCP tool
- include counts for normalized entities, claims, deduped claims, superseded claims, conflicts, and evidence rows

### P2. The Recommended Retrieval Workflow Is Not Encoded Strongly Enough

Current issue:

- the right workflow exists, but agents still have to infer which tool to use first
- `query_memory` remains tempting because it is short and human-friendly

Why this matters:

- agent outcomes improve when the intended tool choreography is obvious

Desired direction:

- explicitly document:
  - use `search_memory` for recall
  - use `get_memory_subgraph` for bounded structure
  - use bundles for local inspection
  - use `expand_memory_frontier` for iterative chasing
  - use `render_memory_summary` only for final human output

### P2. Search Result Metadata Could Be Stronger

Current issue:

- current seeds expose `score` and `matchKind`, which is good
- but they do not expose enough explanation for ranking decisions

Desired direction:

- add optional retrieval diagnostics such as:
  - `scoreBreakdown`
  - `matchedFields`
  - `matchedEntityIds`
  - `matchedClaimIds`
  - `retrievalStage`

This would help agents choose whether to trust a result or expand further.

### P2. Memory Maintenance Visibility Is Still Thin

Current issue:

- migrations exist, but there is no strong operator surface for:
  - migration status
  - index health
  - embedding availability
  - orphaned evidence or observations

Desired direction:

- add admin diagnostics for:
  - memory index status
  - embedding model availability
  - migration progress
  - counts by claim status
  - orphan checks for evidence and observations

### P3. Some Contracts Still Favor Human Readability Over Agent Stability

Examples:

- bundle endpoints return rich data, which is good
- but some errors still come back as human strings in MCP instead of structured error payloads

Desired direction:

- normalize MCP errors into structured result envelopes where practical
- keep human-readable messages as secondary text

## Recommended Next Phase

### Phase A. Make The Write Surface Agent-Native

Deliverables:

- typed `store_memory_v2` MCP contract
- write receipt id
- write status endpoint/tool
- richer validation errors

Acceptance criteria:

- agents can submit memory writes without hand-building JSON strings
- agents can reliably tell when a write finished
- validation failures clearly identify the failing entity, claim, or evidence item

### Phase B. Make Retrieval Contracts Explicitly Structured-First

Deliverables:

- structured `query_memory_v2` or equivalent replacement
- updated agent guidance that de-emphasizes prose-first usage
- optional diagnostics in search and subgraph results

Acceptance criteria:

- the recommended retrieval path for agents no longer starts with prose
- agents can reason from returned structure without reparsing text

### Phase C. Add Admin And Cleanup Tools

Deliverables:

- admin delete/retract tools
- delete-by-source support
- dry-run mode
- memory diagnostics tool

Acceptance criteria:

- test data can be cleaned up without direct Neo4j access
- bad imports can be surgically retracted
- operators can inspect memory health and migration state through supported surfaces

### Phase D. Improve Retrieval Explainability

Deliverables:

- result provenance metadata
- scoring breakdowns where practical
- frontier expansion explanation improvements

Acceptance criteria:

- agents can distinguish "high-confidence direct hit" from "semantic neighbor"
- retrieval decisions are easier to debug when a result looks surprising

## Proposed MCP Contract Direction

### Recommended Agent Read Workflow

1. `search_memory`
2. `get_memory_subgraph`
3. `get_claim_bundle` and `get_entity_bundle`
4. `expand_memory_frontier`
5. `render_memory_summary` only for final human-facing output

### Recommended Agent Write Workflow

1. `store_memory_v2`
2. `get_memory_write_status`
3. `search_memory` or `get_claim_bundle` for verification

### Recommended Admin Workflow

1. `get_memory_diagnostics`
2. `delete_memory_by_source` with dry-run
3. destructive execution only after explicit confirmation

## Suggested Priority Order

1. typed `store_memory_v2` contract plus write receipt/status
2. structured-first replacement or enhancement for `query_memory`
3. admin cleanup and delete-by-source tools
4. retrieval diagnostics and score explanations
5. broader memory operator dashboard and health endpoints

## Bottom Line

The current implementation is already a real improvement for agents and should be treated as the new baseline.

The path forward is not another graph-model rewrite.

The path forward is to harden the product surface around the graph so agents can:

- write memory more safely
- retrieve memory more predictably
- inspect ranking and provenance more clearly
- clean up mistakes without dropping into Neo4j
- rely on a stable, documented workflow that matches how good agents actually operate
