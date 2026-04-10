# Memory Claim Graph Neo4j Implementation Plan

## Status Snapshot

Last updated: 2026-04-10

Scope:

- complete redesign of the personal memory system
- Neo4j only
- no SQL persistence layer
- claim-centric memory replaces the current entity-summary-first model

Current problem statement:

- the existing memory system stores truth too coarsely in `MemoryEntity.summary`
- relationships are append-only `RELATES_TO` edges with weak native supersession semantics
- conflict handling depends too much on caller-supplied `conflicts`
- retrieval is effectively seed-entity recall plus prose rendering, which is shallow for agent use

Primary implementation files today:

- store interface: [IMemoryGraphStore.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/IMemoryGraphStore.cs)
- Neo4j adapter: [Neo4jMemoryGraphStore.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data.Neo4j/Neo4jMemoryGraphStore.cs)
- write path: [MemoryNormalizationService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Memory/MemoryNormalizationService.cs)
- retrieval path: [MemoryRetrievalService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Memory/MemoryRetrievalService.cs)
- service façade: [MemoryService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Memory/MemoryService.cs)
- REST API: [MemoryController.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/Controllers/MemoryController.cs)
- MCP tools: [MemoryMcpServer.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Assistant/MemoryMcpServer.cs)
- schema baseline: [001_schema.cypher](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/Migrations/001_schema.cypher)
- target design: [memory-claim-graph-spec.md](/Users/michael/Repos/CodeGraph/plans/memory-claim-graph-spec.md)

## Goal

Replace the current memory graph with a claim-centric Neo4j model that:

- stores atomic facts as first-class memory records
- preserves supersession, contradiction, and evidence explicitly
- supports structured retrieval as the primary agent contract
- supports iterative memory chasing instead of one-shot prose recall
- keeps entities as stable anchors, not as the primary truth surface

## Non-Goals

- no SQL schema or SQL query work
- no backward-compatible preservation of the old write model as the long-term architecture
- no prose-first MCP contract
- no attempt to reconstruct precise atomic claims from already-merged entity summaries

## Target Neo4j Model

### Node Labels

- `MemoryEntity`
  - stable named anchors such as people, projects, concepts, tools, decisions
  - `summary` remains optional descriptive metadata only
- `MemoryClaim`
  - source of truth for atomic facts
  - stores normalized claim payload, fact-group identity, status, timestamps, and confidence
- `MemoryObservation`
  - unresolved contradiction, ambiguity, or dispute records
- `MemoryEvidence`
  - provenance records for claims and observations

### Core Relationships

- `(c:MemoryClaim)-[:SUBJECT]->(e:MemoryEntity)`
- `(c:MemoryClaim)-[:OBJECT]->(e:MemoryEntity)`
- `(c:MemoryClaim)-[:SUPERSEDES]->(prior:MemoryClaim)`
- `(c:MemoryClaim)-[:CONFLICTS_WITH]->(other:MemoryClaim)`
- `(c:MemoryClaim)-[:SUPPORTS]->(other:MemoryClaim)`
- `(c:MemoryClaim)-[:DERIVED_FROM]->(other:MemoryClaim)`
- `(o:MemoryObservation)-[:ABOUT]->(c:MemoryClaim|e:MemoryEntity)`
- `(ev:MemoryEvidence)-[:EVIDENCE_FOR]->(c:MemoryClaim|o:MemoryObservation)`

### Derived Optimization Relationships

- `(a:MemoryEntity)-[:ACTIVE_RELATES_TO]->(b:MemoryEntity)`

Rules:

- derived only from active claims
- never the source of truth
- safe to rebuild from claim state

### Core Claim Properties

- `id`
- `claimKey`
- `factGroupKey`
- `predicate`
- `normalizedText`
- `status`
- `confidence`
- `valueText`
- `valueJson`
- `effectiveAt`
- `recordedAt`
- `source`
- `embedding`

### Claim Statuses

- `active`
- `superseded`
- `conflicted`
- `deprecated`

## Architectural Direction

This redesign should be implemented as a new memory vertical slice inside the existing service boundaries, then cut over endpoint by endpoint.

High-level rules:

- keep `MemoryEntity` for stable anchors
- stop storing truth primarily in entity summaries
- make `MemoryClaim` the main unit for write and read behavior
- make structured retrieval the default contract
- keep markdown rendering as a convenience layer on top of structured retrieval

## Phase 1: Lock The V2 Domain Model

### Deliverables

- new claim-centric contracts in `CodeGraph.Models.Memory`
- legacy contracts retained only as temporary compatibility wrappers
- clear separation between write contracts, retrieval contracts, and rendered summary output

### New Model Types

- `MemoryClaim`
- `MemoryEvidence`
- `MemorySeed`
- `MemorySearchResult`
- `MemorySubgraphResult`
- `MemoryEntityBundle`
- `MemoryClaimBundle`
- `MemoryFrontierExpansionResult`
- `MemoryPathExplanation`

### Notes

- `MemoryQueryResult` should stop being the primary retrieval abstraction
- `FormattedText` should move to a renderer-oriented result or helper

## Phase 2: Add Neo4j Schema For Claim Memory

### Deliverables

- new migration after [001_schema.cypher](/Users/michael/Repos/CodeGraph/src/CodeGraph.Api/Migrations/001_schema.cypher)
- constraints, indexes, and vector indexes for claim-centric retrieval

### Schema Work

Add constraints for:

- `MemoryEntity.id`
- `MemoryClaim.id`
- `MemoryObservation.id`
- `MemoryEvidence.id`

Add indexes for:

- `MemoryClaim.claimKey`
- `MemoryClaim.factGroupKey`
- `MemoryClaim.status`
- `MemoryClaim.predicate`
- `MemoryClaim.recordedAt`
- `MemoryClaim.effectiveAt`

Add fulltext indexes for:

- claim normalized text and value text
- entity label, canonical name, aliases, and optional summary

Add vector indexes for:

- `MemoryEntity.embedding`
- `MemoryClaim.embedding`

### Important Constraint

- avoid APOC dependencies
- use bounded Cypher traversals and explicit relationship types only

## Phase 3: Replace The Write Path

### Goal

Replace the current entity-summary and append-edge write behavior with claim-centric ingestion.

### Code Areas

- [MemoryNormalizationService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Memory/MemoryNormalizationService.cs)
- [Neo4jMemoryGraphStore.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data.Neo4j/Neo4jMemoryGraphStore.cs)
- [MemoryExtractionResult.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Models/Memory/MemoryExtractionResult.cs)

### Planned Refactor

Split the current normalization service into focused components:

1. `MemoryEntityResolver`
2. `MemoryClaimNormalizer`
3. `MemoryClaimWritePlanner`
4. `MemoryObservationService`
5. `MemoryEvidenceService`

### Write Behavior

For each incoming atomic fact:

1. resolve or create referenced entities
2. normalize the claim payload
3. compute `claimKey`
4. compute `factGroupKey`
5. load existing claims in the fact group
6. classify the incoming claim as:
   - duplicate
   - newer equivalent
   - explicit supersession
   - contradiction
   - independent claim
7. write claim nodes and claim edges
8. update derived active entity adjacency
9. attach evidence and observations as needed

### Required Changes To Inputs

The current extraction DTO is too edge-oriented.

Introduce a v2 ingestion payload that supports:

- explicit claims
- explicit evidence
- optional entity declarations
- optional timestamps and confidence
- optional explicit supersedes references

The old `nodes` and `edges` payload can remain temporarily as a compatibility transform into v2 claims.

### Entity Identity Rules

Use this order:

1. explicit external id match
2. canonical-name or alias exact match
3. strong lexical normalization match
4. fuzzy match only as a fallback with tighter safeguards

Avoid using fuzzy id matching as the primary identity mechanism for claim writes.

### Legacy Behavior To Remove

- summary concatenation as truth accumulation
- blind edge creation as the main memory write primitive
- caller-owned contradiction detection as the main conflict mechanism

## Phase 4: Introduce Derived Active Adjacency

### Goal

Keep retrieval efficient without making entity edges the source of truth.

### Deliverables

- write-time maintenance of `ACTIVE_RELATES_TO`
- a repair or rebuild routine that can regenerate active adjacency from claims

### Rules

- only active claims contribute to derived adjacency
- superseded claims must stop contributing
- conflicted claims may contribute only when explicitly useful for retrieval and explanation

## Phase 5: Build Structured Retrieval

### Goal

Replace prose-first memory queries with structured search and bounded subgraph retrieval.

### Code Areas

- [MemoryRetrievalService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Memory/MemoryRetrievalService.cs)
- [MemoryService.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Services/Memory/MemoryService.cs)
- [IMemoryGraphStore.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data/IMemoryGraphStore.cs)
- [Neo4jMemoryGraphStore.cs](/Users/michael/Repos/CodeGraph/src/CodeGraph.Data.Neo4j/Neo4jMemoryGraphStore.cs)

### Retrieval APIs

Add store and service methods for:

- `SearchMemoryAsync`
- `GetMemorySubgraphAsync`
- `GetEntityBundleAsync`
- `GetClaimBundleAsync`
- `ExpandMemoryFrontierAsync`
- `RenderMemorySummaryAsync`

### Retrieval Pipeline

1. exact recall over ids, aliases, canonical names, and claim keys
2. lexical recall over entity and claim fulltext indexes
3. vector recall over entity and claim embeddings
4. seed fusion in C#
5. bounded graph expansion in Neo4j
6. default suppression of superseded claims
7. visibility of unresolved conflicts
8. compact structured response with ranking and path metadata

### Default Retrieval Rules

- prefer active claims
- include conflicted claims when relevant
- hide superseded claims unless explicitly requested
- apply freshness as a small bonus, not a global sort order
- cap edge fanout aggressively

## Phase 6: Add Bundle And Frontier APIs

### Goal

Support iterative deepening instead of one giant traversal.

### New Service Surfaces

- `GetEntityBundleAsync(entityId, options)`
- `GetClaimBundleAsync(claimId, options)`
- `ExpandMemoryFrontierAsync(frontier, options)`

### Response Requirements

Structured responses should include:

- seeds and scores
- entities and claims
- claim edges and entity edges
- observations
- optional evidence summaries
- path explanations
- truncation and frontier metadata

## Phase 7: Cut Over REST And MCP Contracts

### REST

Add v2 endpoints:

- `POST /api/memory/claims/store`
- `GET /api/memory/search`
- `POST /api/memory/subgraph`
- `GET /api/memory/entities/{id}/bundle`
- `GET /api/memory/claims/{id}`
- `POST /api/memory/frontier/expand`
- `POST /api/memory/render-summary`

Temporary compatibility:

- keep `/api/memory/query` as a wrapper over v2 retrieval plus markdown rendering during migration

### MCP

Add tools:

- `store_memory_v2`
- `search_memory`
- `get_memory_subgraph`
- `get_entity_bundle`
- `get_claim_bundle`
- `expand_memory_frontier`
- `render_memory_summary`

Temporary compatibility:

- keep `query_memory` as a wrapper only while clients migrate

## Phase 8: Legacy Data Migration Inside Neo4j

### Goal

Migrate current Neo4j memory data into the new label and relationship model without relying on SQL or a second database.

### Migration Inputs

- existing `MemoryEntity` nodes
- existing `RELATES_TO` edges
- existing `MemoryObservation` nodes

### Migration Strategy

1. preserve current entities
2. convert each legacy `RELATES_TO` edge into a `MemoryClaim`
3. attach `SUBJECT` and `OBJECT` links to referenced entities
4. preserve old observations by linking them to migrated claims or entities where possible
5. mark migrated claims with a migration source marker
6. build derived `ACTIVE_RELATES_TO` edges from migrated active claims
7. validate read parity on representative queries
8. switch reads to v2 only after validation

### Important Limitation

Do not attempt to reconstruct atomic claims from merged `summary` text. That information is lossy and should remain descriptive metadata only.

## Phase 9: Testing Strategy

Current test coverage for memory is too shallow for this redesign.

### Add Unit Tests For

- claim normalization
- claim key and fact-group key generation
- duplicate detection
- supersession classification
- contradiction classification
- entity resolution ranking
- retrieval seed fusion
- summary rendering from structured results

### Add Integration Tests For

- Neo4j claim writes
- derived active adjacency maintenance
- exact, lexical, and vector recall
- bounded subgraph expansion
- entity bundle retrieval
- claim bundle retrieval
- frontier expansion
- legacy migration from `RELATES_TO` to `MemoryClaim`
- compatibility wrappers for old endpoints and MCP tools

### Minimum Required Scenarios

- same fact restated later becomes superseded correctly
- contradictory facts remain queryable with an observation
- active claims dominate default retrieval
- superseded claims appear only when explicitly requested
- path metadata explains why an item was returned

## Phase 10: Rollout And Cleanup

### Rollout Sequence

1. ship schema and v2 models
2. ship v2 write path behind internal cutover flag or isolated entry points
3. ship v2 structured retrieval
4. add REST and MCP compatibility wrappers
5. migrate existing Neo4j memory data
6. switch default reads to v2
7. switch default writes to v2
8. remove old `RELATES_TO` retrieval logic
9. remove old summary-first query path

### Cleanup Targets

- old `FormattedText`-first query flow
- direct dependence on legacy `nodes` plus `edges` payloads as the main ingestion shape
- legacy retrieval code that treats entity summaries as memory truth

## Concrete Work Breakdown

### Slice 1

- add new memory model classes
- add new migration for claim graph labels, properties, constraints, and indexes
- add baseline Neo4j store methods for claim CRUD and search

### Slice 2

- implement claim normalization and write planning
- add observation and evidence persistence
- add derived active adjacency maintenance

### Slice 3

- implement structured search and subgraph retrieval
- implement entity bundle and claim bundle retrieval
- implement summary renderer on top of structured results

### Slice 4

- expose REST v2 endpoints
- expose MCP v2 tools
- keep temporary wrappers for old contracts

### Slice 5

- implement legacy migration command
- validate migrated data
- cut reads and writes over to v2
- delete obsolete code paths

## Success Criteria

The redesign is successful when:

- a memory fact is stored as a claim, not inferred from entity prose
- supersession is explicit and queryable
- contradictions are explicit and queryable
- retrieval returns structured subgraphs by default
- the agent can re-query exact entity and claim ids cheaply
- iterative memory chasing works without graph explosion
- the old memory flow can be removed without losing required functionality

## Immediate Next Step

Start with the v2 domain model and the Neo4j schema migration. Those two decisions define the rest of the implementation and keep the redesign from collapsing back into the current entity-edge abstraction.
