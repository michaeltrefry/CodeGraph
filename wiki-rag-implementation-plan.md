# Wiki RAG: Nomic Embedder Swap & Markdown-Aware Ingestion

**Epic**: sc-932  
**Created**: 2026-05-12  
**Rationale**: Migrate wiki embedding and search from MiniLM-L6 (384-dim) to nomic-embed-text-v1.5 (768-dim) with markdown-aware chunking, persistent ingestion pipeline, and reactive re-embedding on edits.

## Context & Rationale

### Problem

The current wiki RAG uses MiniLM-L6 embeddings (384-dim), which produces decent results but has known limitations for domain-specific search quality. Nomic produces 768-dim vectors with better semantic understanding and is Matryoshka-truncatable (can be searched at 256/512/768 depending on use case).

### Technical Migration Challenge

**Current state**: Embeddings stored as `LONGTEXT` JSON in `embeddings` table, no schema provenance tracking (no `model_name` or `dimensions` column).

**The breakage**: Swapping embedders invalidates existing 384-dim vectors — they can't be cross-queried with 768-dim vectors, and the schema has no way to distinguish which model produced each row. Mixed-model state silently corrupts search.

**Why this doesn't require schema migration**: The JSON column accepts 768-dim vectors without modification. But there's no way to tell old rows from new rows if both are present.

### Chosen Path: Hard Cutover with Provenance (Path #2)

1. Add `model_name VARCHAR(100)` and `dimensions INT` columns to `embeddings` table
2. Migrate code to use nomic ONNX model
3. Truncate/reset embeddings table
4. Re-embed entire wiki corpus on next startup
5. Write provenance columns on every new embedding

**Why this path**:
- Eliminates invalid mixed-model state
- Cost is acceptable (full re-embed on 2x RTX 6000: ~1 hour for full corpus, much less wiki-only)
- Adds structural safety for future embedder swaps — no more "which model produced this?" ambiguity
- Enables future Matryoshka truncation without re-embedding

### Nomic-Specific Notes

- `nomic-embed-text-v1.5` requires ONNX export compatible with existing `OnnxEmbeddingService`
- May require special prefix tokens (`search_document:` / `search_query:`) — verify against existing tokenizer
- Matryoshka-truncatable: future iterations can store 768-dim but search at 256/512 without re-embedding

## Implementation Slices

### Slice 1: Swap Embedder to Nomic with Provenance

**Card**: sc-934

**Goal**: Replace MiniLM-L6 ONNX model with nomic-embed-text-v1.5, add schema provenance tracking, reset embeddings to clean state.

**Tasks**:
1. Verify nomic-embed-text-v1.5 ONNX export availability and tokenizer compatibility with `OnnxEmbeddingService`
2. Create SQL migration:
   - Add `model_name VARCHAR(100) NOT NULL DEFAULT 'nomic-embed-text-v1.5'` to `embeddings` table
   - Add `dimensions INT NOT NULL DEFAULT 768` to `embeddings` table
   - Truncate existing embeddings (or backup to archive table if recovery is desired)
3. Update `CodeGraphStorageOptions.cs` to define `EmbeddingDimensions = 768` and `EmbeddingModelName = "nomic-embed-text-v1.5"`
4. Replace ONNX model file in `OnnxEmbeddingService` or dependency injection layer
5. Update any `OnnxEmbeddingService` tokenizer configuration if nomic requires special prefixes
6. Update schema documentation

**Acceptance Criteria**:
- [ ] Migration runs successfully on fresh and existing databases
- [ ] `OnnxEmbeddingService.Embed()` returns 768-dim vectors
- [ ] Provenance columns are written on every new embedding
- [ ] Unit tests for embedding service pass with 768-dim assertions
- [ ] Embeddings table is either truncated or archived per team decision

**Open Questions**:
- Is nomic ONNX tokenizer compatible with existing setup, or does it need special prefixes?
- Should truncated MiniLM vectors be archived for audit/recovery, or discarded?

---

### Slice 2: Markdown-Aware Wiki Chunker & Ingester

**Card**: sc-935

**Goal**: Build a chunking/ingesting service that understands wiki structure (headings, code blocks, lists) and preserves chunk context.

**Requirements**:
- Input: raw markdown wiki page content
- Output: chunks (text + metadata) optimized for embedding and search
- Per-chunk metadata: `slug`, `section_path` (e.g., "## Overview > ### Architecture"), `revision_id`, `chunk_index`
- Respect markdown semantics: don't split mid-sentence across code blocks, preserve heading hierarchy
- Chunk size: target 256–512 tokens (adjust based on nomic tokenizer + pragmatic search UX)

**Architecture Decision Required**: Where does per-chunk metadata live?
- Option A: Extend `embeddings` table with `chunk_metadata LONGTEXT JSON` (simplest, keeps chunks + vectors atomic)
- Option B: Sibling table `chunk_metadata(id, chunk_id, ...)` (normalized, slightly more complex querying)
- Option C: Pack into `entity_key` (lossy, avoid)
- **Recommendation**: Option A (single table, atomic chunks + vectors)

**Tasks**:
1. Create `MarkdownChunker` class:
   - Parse markdown into AST or token stream
   - Identify sections by heading level + hierarchy
   - Emit chunks with metadata: `{ text, slug, section_path, revision_id, chunk_index }`
   - Configurable chunk size in tokens (use nomic tokenizer)
2. Create `WikiIngester` service:
   - Accept `WikiPage` entity (slug, title, content, revision_id)
   - Call `MarkdownChunker.Chunk()`
   - Call `OnnxEmbeddingService.Embed()` for each chunk
   - Persist chunks + embeddings (single table or sibling pattern per decision)
   - Return success/failure + telemetry
3. Update `embeddings` table schema or create `chunk_metadata` table
4. Unit tests:
   - Chunker respects code-block boundaries
   - Chunker preserves section context in `section_path`
   - Ingester correctly embeds and persists all chunks
   - Ingester handles edge cases: empty pages, single-line pages, deeply nested headings

**Acceptance Criteria**:
- [ ] `MarkdownChunker` produces well-formed chunks with hierarchy metadata
- [ ] `WikiIngester` end-to-end test: ingest a real wiki page, verify chunks in DB with correct embeddings
- [ ] Chunk metadata storage decision is documented and implemented
- [ ] Unit tests pass with coverage > 80%
- [ ] Performance: ingesting a typical 50-chunk page < 2s on local hardware

---

### Slice 3: One-Time Job to Ingest All Existing Wiki Pages

**Card**: sc-936

**Goal**: Backfill all existing wiki pages through the new chunker + ingester pipeline.

**Dependencies**: Slice 1 (schema + embedder) and Slice 2 (chunker + ingester) must be complete.

**Tasks**:
1. Create `WikiBackfillJob` service:
   - Fetch all `WikiPage` rows from database
   - For each page in order of `id` (stable, resumable):
     - Call `WikiIngester.Ingest(page)`
     - Log success, emit error with page slug if failure
     - Optionally: skip if already ingested (check if chunks exist for `(slug, revision_id)`)
2. Integrate into startup or ad-hoc execution:
   - Option A: Automatic on startup if embeddings table is empty
   - Option B: Manual trigger endpoint `POST /admin/jobs/wiki-backfill`
   - **Recommendation**: Option A (automatic, self-healing)
3. Add resumption logic: if job is interrupted, restart from last completed page
4. Telemetry:
   - Track pages ingested, skipped, failed
   - Log total time + chunks/sec throughput
   - Emit to application logs + dashboard

**Acceptance Criteria**:
- [ ] All existing wiki pages (or test subset) successfully ingested
- [ ] No duplicate chunks for same (slug, revision_id)
- [ ] Backfill job logs progress and completion
- [ ] Job handles errors gracefully (missing pages, encoding issues, etc.)
- [ ] Job is idempotent (can re-run without corruption)
- [ ] Performance: full wiki corpus ingested in < 2 hours on target hardware

---

### Slice 4: Event-Driven Re-Embedding on Wiki Edits

**Card**: sc-937

**Goal**: Keep embeddings fresh by automatically re-embedding when a wiki page is published, edited, or deleted.

**Dependencies**: Slice 1 (schema + embedder) and Slice 2 (chunker + ingester) must be complete.

**Tasks**:
1. Create event types:
   - `WikiPagePublished { slug, title, content, revision_id }`
   - `WikiPageEdited { slug, title, content, revision_id, previous_revision_id }`
   - `WikiPageDeleted { slug, revision_id }`
2. Add publish/edit triggers to `ConventionsController`:
   - On `POST /api/conventions` or `PUT /api/conventions/{slug}`, emit `WikiPagePublished` or `WikiPageEdited`
   - On `DELETE /api/conventions/{slug}`, emit `WikiPageDeleted`
3. Create consumer `WikiPageEventConsumer`:
   - `Consume(WikiPagePublished)`: Call `WikiIngester.Ingest()` with new page
   - `Consume(WikiPageEdited)`: Delete old chunks for `(slug, previous_revision_id)`, ingest new chunks for `(slug, revision_id)`
   - `Consume(WikiPageDeleted)`: Delete all chunks for slug
4. Register consumer in startup DI
5. Unit & integration tests:
   - Publish event, verify chunks appear in DB with correct embeddings
   - Edit event, verify old chunks deleted and new chunks created
   - Delete event, verify all chunks removed
   - Verify events are durable (don't lose embeds if service crashes mid-ingest)

**Acceptance Criteria**:
- [ ] Events are published on page create/edit/delete
- [ ] Consumer correctly handles all three event types
- [ ] Embeddings are updated within 5 seconds of page publish/edit
- [ ] Old embeddings are cleaned up on edit/delete (no orphans)
- [ ] Unit + integration tests pass
- [ ] Manual test: edit a wiki page in UI, verify updated embeddings in DB

---

### Slice 5: MCP `search_conventions` Tool

**Card**: sc-939

**Goal**: Expose wiki search via MCP with hybrid BM25 + dense vector retrieval and reciprocal rank fusion (RRF) reranking.

**Dependencies**: Slices 1–4 must be complete (full embeddings + chunks available).

**Implementation**:
1. Create hybrid search service `ConventionSearchService`:
   - BM25 search against chunk text (using MySQL `MATCH` + `AGAINST` or similar)
   - Dense vector search: embed query with nomic, find k-nearest chunks by cosine similarity
   - Combine results via RRF: score_rrf = 1/(k1 + bm25_rank) + 1/(k2 + dense_rank)
2. Add MCP tool `search_conventions`:
   - Input: query (string), limit (int, default 5)
   - Output: list of chunks with `{ text, slug, section_path, score }`
3. Integrate into `MCP.Tools.cs` or existing MCP endpoint
4. Unit tests:
   - BM25 retrieves expected chunks for keyword queries
   - Dense search retrieves semantically similar chunks for natural-language queries
   - RRF reranking merges both signals correctly
   - Edge cases: empty query, no results, tie-breaking

**Open Decisions**:
- BM25 weight (k1) vs. dense weight (k2) in RRF formula — recommend tuning empirically after backfill is live
- Cross-encoder reranking (e.g., `cross-encoder/ms-marco-minilm-l-6-v2`) — explicitly deferred; revisit if search quality is poor

**Acceptance Criteria**:
- [ ] MCP tool registered and callable from Claude
- [ ] Hybrid search returns high-quality results for both keyword and semantic queries
- [ ] Tool is callable and returns results within 500ms for typical corpus
- [ ] Manual test: ask Claude "How do we handle X?" and verify correct wiki sections are retrieved

---

## Open Questions & Future Work

### Before Starting Slice 1
- [ ] Verify nomic-embed-text-v1.5 ONNX export and tokenizer compatibility
- [ ] Decide: archive or discard existing MiniLM embeddings?

### Before Starting Slice 2
- [ ] Metadata storage: extend `embeddings` table (Option A) or sibling table (Option B)?

### After All Slices (Deferred)
- [ ] Cross-encoder reranking for search quality (if needed)
- [ ] Matryoshka truncation to 256-dim for storage optimization (if needed)
- [ ] Update CODEGRAPH.md to document wiki RAG design

## Execution Order

```
Slice 1: Swap embedder + schema   [must complete first]
    ↓
Slice 2: Markdown chunker/ingester [depends on Slice 1]
    ↓
    ├─→ Slice 3: Backfill job      [parallel, depends on 1 + 2]
    ├─→ Slice 4: Event consumer    [parallel, depends on 1 + 2]
    ↓
Slice 5: MCP search tool          [depends on 1–4]
```

## Notes

- **Hardware assumption**: 2x RTX 6000 available for re-embedding. Adjust timeline if different.
- **Self-maintaining principle**: Job 3 (backfill) should run automatically on startup if `embeddings` is empty — no manual intervention needed.
- **Git discipline**: Each slice is a discrete commit. Search PRs reference the epic + card numbers.
