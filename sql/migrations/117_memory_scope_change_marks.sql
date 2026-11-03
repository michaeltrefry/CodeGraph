-- CG-172: a change mark on the memory rows whose repository names the scope reconciliation matches,
-- so it finds rows rewritten in place as well as new ones. A claim upsert keyed on its external id can
-- repoint an existing claim at other entities, and an entity upsert can replace its label: the row
-- keeps its id, so an id watermark would never see the change. scope_seq is taken from a sequence by
-- the memory store when a row is inserted, and again when an upsert changes a claim's subject or
-- object or an entity's label; the reconciliation's watermark covers scope_seq instead of id.
--
-- Rows written before this migration keep 0: they are below every watermark, and migration 116's
-- one-time pending rows run the full backfill over them. Evidence rows are only ever inserted (each
-- with a fresh id), so memory_evidence keeps its id as the mark.
CREATE SEQUENCE IF NOT EXISTS memory_entity_scope_seq;

CREATE SEQUENCE IF NOT EXISTS memory_claim_scope_seq;

ALTER TABLE memory_entities_v2 ADD COLUMN IF NOT EXISTS scope_seq BIGINT NOT NULL DEFAULT 0;

ALTER TABLE memory_claims ADD COLUMN IF NOT EXISTS scope_seq BIGINT NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS ix_memory_entities_v2_scope_seq ON memory_entities_v2 (scope_seq);

CREATE INDEX IF NOT EXISTS ix_memory_claims_scope_seq ON memory_claims (scope_seq);
