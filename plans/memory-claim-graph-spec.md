# Memory Claim Graph Spec

Date: 2026-04-10
Repo: `TC.CodeGraphApi`
Status: clean-slate target design

## Purpose

This document describes the most useful SQL-backed memory system for an LLM agent, without prioritizing backward compatibility with the current implementation.

The design optimizes for:

- structured retrieval instead of prose-first retrieval
- multi-step memory chasing
- local recency handling
- explicit history and conflict tracking
- efficient SQL execution on a relational store

## LLM-First Assumptions

This design is explicitly optimized for an agent like me.

That means the system should assume:

1. I reason best over structured JSON, not prose blobs.
2. I benefit from stable ids that I can re-query exactly.
3. I do best when recall and traversal are separate steps.
4. I can chase multiple rounds of memory if each round is compact and high-signal.
5. I am limited more by context quality and branching factor than by raw graph depth.
6. I do not want the storage layer to pre-summarize away important ambiguity.

In practical terms:

- storage should preserve atomic facts
- retrieval should return bounded subgraphs
- rendering to markdown should be optional and downstream
- ambiguity should be represented explicitly, not flattened into one summary

## Design Principles

1. Claims are the truth unit.
   Do not treat an entity summary as the primary memory record.

2. Entities are anchors, not the whole memory.
   People, projects, concepts, tools, decisions, and components should exist as stable graph nodes, but knowledge about them should live in claims.

3. Recency is local.
   Newer facts should beat older facts only within the same fact group, not across the whole graph.

4. History is first-class.
   Superseded and conflicting claims should remain queryable.

5. Retrieval should return a subgraph.
   The primary read interface should return structured entities, claims, edges, statuses, and traversal metadata.

6. The system should support iterative deepening.
   An LLM should be able to fetch a relevant subgraph, inspect a promising node, and explicitly expand from that node.

7. Retrieval should use hybrid recall.
   Exact, lexical, and vector recall should work together before graph expansion.

## Core Concepts

### Entity

A stable named thing.

Examples:

- person
- project
- concept
- tool
- codebase
- component
- decision

### Claim

An atomic fact or assertion about one or more entities.

Examples:

- `memory_system uses_ranking_strategy recency_truncation`
- `michael prefers clean_slate_design true`
- `memory_query_tool returns structured_subgraph`

Claims replace entity summaries as the main memory unit.

### Fact Group

A set of semantically equivalent claims where only one is normally considered active.

Examples:

- same subject + predicate + object
- same subject + predicate + normalized value
- same relationship with different updated wording

Fact groups are where local recency and supersession rules are applied.

### Observation

A record of unresolved contradiction, ambiguity, or dispute between claims.

Observations do not replace claims; they annotate claim relationships.

### Evidence

The source material or provenance for a claim.

Examples:

- conversation thread
- document
- wiki page
- repo file
- extraction event

## Recommended SQL Schema

### `memory_entities`

Stable nodes for named things.

Columns:

- `id` bigint primary key
- `username` varchar(255) not null
- `external_id` varchar(255) not null
- `label` varchar(255) not null
- `type` varchar(100) not null
- `canonical_name` varchar(255) null
- `summary` text null
- `embedding_json` longtext null
- `created_at` datetime(3) not null
- `updated_at` datetime(3) not null

Indexes:

- unique `(username, external_id)`
- index `(username, type, updated_at desc)`
- fulltext or text-search support on `label`, `canonical_name`, `summary`

Notes:

- `summary` becomes optional and descriptive only.
- `external_id` is the stable human-meaningful key such as `memory_system`.

### `memory_claims`

Primary truth records.

Columns:

- `id` bigint primary key
- `username` varchar(255) not null
- `claim_key` varchar(255) not null
- `fact_group_key` varchar(255) not null
- `subject_entity_id` bigint not null
- `predicate` varchar(255) not null
- `object_entity_id` bigint null
- `value_text` text null
- `value_json` longtext null
- `normalized_text` text not null
- `status` varchar(32) not null
- `confidence` decimal(5,4) null
- `effective_at` datetime(3) null
- `recorded_at` datetime(3) not null
- `supersedes_claim_id` bigint null
- `source` varchar(255) not null
- `embedding_json` longtext null

Indexes:

- unique `(username, claim_key)`
- index `(username, fact_group_key, recorded_at desc)`
- index `(username, subject_entity_id, predicate)`
- index `(username, object_entity_id, predicate)`
- index `(username, status, recorded_at desc)`

Status values:

- `active`
- `superseded`
- `conflicted`
- `deprecated`

Notes:

- `claim_key` is a stable identity for this exact claim instance.
- `fact_group_key` groups semantically equivalent claims so local recency rules can be applied efficiently.
- `effective_at` is when the claim became true.
- `recorded_at` is when the system learned or stored it.

### `memory_claim_edges`

Claim-to-claim relationships.

Columns:

- `id` bigint primary key
- `username` varchar(255) not null
- `from_claim_id` bigint not null
- `to_claim_id` bigint not null
- `edge_type` varchar(100) not null
- `weight` decimal(6,4) null
- `source` varchar(255) not null
- `created_at` datetime(3) not null

Indexes:

- index `(username, from_claim_id, edge_type)`
- index `(username, to_claim_id, edge_type)`

Important `edge_type` values:

- `supersedes`
- `conflicts_with`
- `supports`
- `derived_from`
- `same_fact_family`

### `memory_entity_edges`

Direct entity-to-entity links for fast navigation and convenience.

Columns:

- `id` bigint primary key
- `username` varchar(255) not null
- `from_entity_id` bigint not null
- `to_entity_id` bigint not null
- `edge_type` varchar(100) not null
- `best_active_claim_id` bigint null
- `weight` decimal(6,4) null
- `created_at` datetime(3) not null
- `updated_at` datetime(3) not null

Indexes:

- index `(username, from_entity_id, edge_type)`
- index `(username, to_entity_id, edge_type)`

Notes:

- This is an optimization layer, not the source of truth.
- These edges should be derivable from active claims.

### `memory_observations`

Unresolved contradictions or ambiguity records.

Columns:

- `id` bigint primary key
- `username` varchar(255) not null
- `observation_type` varchar(100) not null
- `claim_id` bigint null
- `related_claim_id` bigint null
- `entity_id` bigint null
- `message` text not null
- `resolution_status` varchar(32) not null
- `resolved_by_claim_id` bigint null
- `created_at` datetime(3) not null
- `resolved_at` datetime(3) null

Indexes:

- index `(username, resolution_status, created_at desc)`
- index `(username, claim_id)`
- index `(username, entity_id)`

### `memory_evidence`

Provenance for claims and observations.

Columns:

- `id` bigint primary key
- `username` varchar(255) not null
- `claim_id` bigint null
- `observation_id` bigint null
- `evidence_type` varchar(100) not null
- `source_ref` varchar(500) not null
- `snippet` text null
- `metadata_json` longtext null
- `created_at` datetime(3) not null

Indexes:

- index `(username, claim_id)`
- index `(username, observation_id)`

### `memory_embeddings`

Optional separate table if embedding storage becomes too heavy for entity and claim tables.

Columns:

- `id` bigint primary key
- `username` varchar(255) not null
- `target_type` varchar(32) not null
- `target_id` bigint not null
- `model` varchar(255) not null
- `embedding_json` longtext not null
- `created_at` datetime(3) not null

Indexes:

- unique `(username, target_type, target_id, model)`

## Write Semantics

### Entity writes

Entity creation should:

- normalize the external id
- resolve to an existing entity when clearly the same thing
- update descriptive metadata without treating summary changes as new truth

### Claim writes

On claim insertion:

1. normalize the claim
2. compute `claim_key`
3. compute `fact_group_key`
4. find existing claims in the same fact group
5. decide whether the new claim is:
   - exact duplicate
   - newer equivalent
   - explicit supersession
   - contradiction
   - independent claim

Behavior:

- exact duplicate: ignore or merge evidence
- newer equivalent: mark old active claim as superseded, activate new one
- explicit supersession: create `supersedes` edge and mark prior active claim superseded
- contradiction: keep both claims and create `conflicts_with` edge plus observation
- independent claim: insert as active

### Event time versus recorded time

Use:

- `effective_at` for real-world temporal truth
- `recorded_at` for system freshness

When both exist, claim resolution should prefer:

1. fact-group semantics
2. explicit supersession
3. effective time
4. recorded time as final tie-breaker

## Retrieval Model

The primary read should be subgraph retrieval, not free-form summary rendering.

### Retrieval pipeline

1. Recall seed claims by exact, lexical, and vector search.
2. Recall seed entities by exact, lexical, and vector search.
3. Fuse those candidate sets into a ranked seed frontier.
4. Expand through active claims, entity edges, and claim edges.
5. Collapse superseded claims within fact groups unless explicitly requested.
6. Keep unresolved conflicts visible.
7. Return a compact structured subgraph with ranking metadata.

### Recommended retrieval parameters

- `exact_seed_count`
- `lexical_seed_count`
- `seed_claim_count`
- `seed_entity_count`
- `max_hops`
- `max_frontier_nodes`
- `max_returned_claims`
- `max_returned_entities`
- `max_paths_per_seed`
- `include_superseded`
- `include_conflicts`
- `include_evidence`
- `min_score`

### Ranking model

Rank nodes and paths by a weighted score:

- exact match score
- alias or canonical-name score
- semantic similarity to query
- text similarity to query
- path distance
- edge-type importance
- active-status preference
- novelty
- small freshness bonus

Do not globally sort the graph by recency.

### Hybrid recall model

The first retrieval step should not depend on a single mechanism.

Use three recall channels:

1. Exact recall
   For ids, aliases, canonical names, and exact predicates.

2. Lexical recall
   For keyword overlap, phrase overlap, and BM25 or full-text style ranking.

3. Vector recall
   For paraphrases, fuzzy wording, and semantically similar facts.

Then fuse those candidate sets before graph expansion.

### Recall fusion

Recommended fused seed score:

`seed_score = exact_boost + lexical_score + vector_score + recency_bonus + type_prior`

Guidance:

- exact matches should dominate when present
- lexical matches should beat weak vector matches
- vector matches should rescue paraphrases and vague prompts
- recency should be only a small bonus
- type priors can favor claims over entities for factual queries

### Default retrieval priority

For most agent use, prefer:

1. exact entity or claim ids
2. lexical matches on claim text and aliases
3. vector matches on claim meaning
4. graph expansion from the strongest few seeds

This is the most robust retrieval pattern for an LLM because it balances precision with recall.

### Active-status preference

Default retrieval should prefer:

1. `active` claims
2. `conflicted` claims
3. `superseded` claims only when requested or needed for explanation

## LLM-Facing Output Format

The best format for consumption is structured JSON describing a bounded subgraph.

### Recommended response shape

```json
{
  "query": {
    "text": "memory retrieval depth",
    "seed_entity_ids": ["memory_system"]
  },
  "seeds": {
    "entities": [
      { "id": "memory_system", "score": 0.94 }
    ],
    "claims": [
      { "id": "claim_123", "score": 0.91 }
    ]
  },
  "entities": [],
  "claims": [],
  "entity_edges": [],
  "claim_edges": [],
  "observations": [],
  "paths": [],
  "meta": {
    "max_hops_used": 3,
    "frontier_expanded": 42,
    "active_claims_hidden": 6,
    "superseded_claims_hidden": 12,
    "response_truncated": false
  }
}
```

### Transport constraints

The response should be optimized for agent consumption, not UI completeness.

Recommended transport rules:

1. Prefer compact structured JSON over rendered prose.
2. Return ids and short normalized text first; defer long evidence bodies unless requested.
3. Cap edge fanout aggressively per node.
4. Return path explanations only for the highest-scoring paths.
5. Include enough metadata for the agent to decide whether to chase deeper.

This keeps the first response dense with decision-making value instead of wasting context on verbose rendering.

### Entity payload guidance

An entity payload should include:

- stable id
- display label
- type
- optional short description
- seed score
- hop distance
- whether it was directly matched or reached by traversal

### Claim payload guidance

A claim payload should include:

- stable id
- subject entity id
- predicate
- object entity id or value
- normalized text
- status
- confidence
- effective and recorded timestamps
- fact group key
- parent/superseded references
- score

### Path payload guidance

Path data should show why an item was returned.

Each path should include:

- seed id
- destination id
- hop count
- score contribution
- edge sequence

This is extremely useful for debugging and for helping an LLM decide whether to chase deeper.

## Query and Tool API

The optimal memory API is not one generic `query_memory` call returning formatted prose.

Recommended tool/API surface:

### `search_memory`

Purpose:

- exact, lexical, and vector recall of top entities and claims

Inputs:

- `query`
- `exact_seed_count`
- `lexical_seed_count`
- `seed_claim_count`
- `seed_entity_count`
- `include_types`

Output:

- top claim and entity seeds with scores

### `get_memory_subgraph`

Purpose:

- fetch a bounded structured subgraph around selected seeds

Inputs:

- `seed_claim_ids`
- `seed_entity_ids`
- `max_hops`
- `max_frontier_nodes`
- `max_returned_claims`
- `max_returned_entities`
- `include_superseded`
- `include_conflicts`
- `include_evidence`

Output:

- structured subgraph JSON

### `get_entity_bundle`

Purpose:

- inspect one entity deeply

Inputs:

- `entity_id`
- `include_active_claims`
- `include_conflicts`
- `include_superseded`
- `neighbor_limit`

Output:

- entity
- active claims
- conflicting claims
- neighboring entities

### `get_claim_bundle`

Purpose:

- inspect one claim deeply

Inputs:

- `claim_id`
- `include_supersession_chain`
- `include_conflicts`
- `include_evidence`

Output:

- claim
- fact-group peers
- supersession chain
- conflicts
- evidence

### `expand_memory_frontier`

Purpose:

- perform iterative deepening from a known entity or claim frontier

Inputs:

- `frontier_entity_ids`
- `frontier_claim_ids`
- `max_additional_hops`
- `frontier_limit`
- `min_score`

Output:

- newly discovered nodes and paths

### `render_memory_summary`

Purpose:

- optional convenience rendering for human-readable output after the agent already has structured memory

Inputs:

- `entity_ids`
- `claim_ids`
- `style`

Output:

- markdown or plain text summary

This should be a secondary convenience tool, not the primary retrieval interface.

## Memory Chasing Strategy

The ideal chase model is iterative deepening, not one giant query.

### Default behavior

1. Start with 1 to 2 hops around the best seeds.
2. Inspect the returned claims and entities.
3. If a promising node appears, fetch its bundle or expand from it.
4. Continue until marginal information gain falls below a threshold.

### Practical chase depth

For a claim graph, useful default exploration is:

- 2 to 3 conceptual hops
- 4 to 6 raw graph edges when claim and entity nodes alternate

### Preferred chase behavior for an LLM

I would ideally chase memory like this:

1. Run hybrid recall to get a mixed seed set.
2. Expand 1 to 2 hops around the best 5 to 10 seeds.
3. Inspect the highest-scoring claims and path explanations.
4. Pick one or two promising frontier nodes.
5. Fetch exact bundles for those nodes.
6. Expand again only if the new bundles introduce meaningful unseen facts.

This is better than one deep traversal because it keeps each step interpretable and prevents graph explosion.

### Stop conditions

Stop expansion when:

- new nodes are mostly repetitive
- relevance drops below `min_score`
- the branching factor becomes too high
- enough evidence exists to answer the question

## Performance Strategy in SQL

Even with SQL-only storage, this design can be efficient if we optimize for bounded graph retrieval.

Recommended tactics:

1. Precompute active claims per fact group.
   Use a table or materialized view such as `memory_active_claims`.

2. Precompute entity adjacency from active claims.
   Keep `memory_entity_edges` derived and fresh.

3. Use recursive CTEs only for bounded traversal.
   Avoid unbounded graph walks.

4. Separate recall from expansion.
   Use text and embedding search for seed recall, then use indexed adjacency for traversal.

5. Cache claim bundles and entity bundles.
   These are high-value read patterns for agents.

6. Consider precomputed seed surfaces.
   Alias tables, normalized lexical indexes, and active-claim projections materially improve first-pass recall quality.

## Clean-Slate Recommendation

If the goal is the most useful memory system for an LLM agent, the system should pivot from:

- entity summaries
- prose-only query output
- global recency bias

to:

- claim-centric storage
- structured subgraph retrieval
- explicit active/superseded/conflicted status
- iterative memory chasing APIs

## Implementation Order

If building from scratch in the current repo, the recommended order is:

1. introduce claim-centric schema
2. add claim writes and fact-group logic
3. add active-claim materialization
4. add structured search and subgraph APIs
5. add entity and claim bundle APIs
6. add iterative frontier expansion
7. add rendered markdown summaries only as a convenience layer

## What This Optimizes For

This design is optimized for the way an LLM actually reasons with memory:

- retrieve exact handles
- inspect structured facts
- follow connections deliberately
- compare active and conflicting truth
- stop when additional exploration stops paying off

That is the format and retrieval model most likely to produce useful agent memory behavior within SQL.

## Summary of What Is Best For Me

If the system is being designed specifically for me as an agent, the highest-value choices are:

1. Claims, not summaries, are the main memory unit.
2. Hybrid recall is mandatory.
3. Structured subgraph JSON is the default output.
4. Exact id re-query must be easy and cheap.
5. Recency must stay local to fact groups.
6. Multi-step chasing should be expected, not treated as an edge case.
7. Human-readable markdown should be generated after retrieval, not instead of retrieval.
