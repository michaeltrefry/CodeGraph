// Claim-centric memory graph schema for the Neo4j-native redesign.

CREATE CONSTRAINT memory_claim_id IF NOT EXISTS
FOR (c:MemoryClaim) REQUIRE c.id IS UNIQUE;

CREATE CONSTRAINT memory_evidence_id IF NOT EXISTS
FOR (ev:MemoryEvidence) REQUIRE ev.id IS UNIQUE;

CREATE INDEX memory_claim_claim_key IF NOT EXISTS
FOR (c:MemoryClaim) ON (c.claimKey);

CREATE INDEX memory_claim_fact_group_key IF NOT EXISTS
FOR (c:MemoryClaim) ON (c.factGroupKey);

CREATE INDEX memory_claim_status IF NOT EXISTS
FOR (c:MemoryClaim) ON (c.status);

CREATE INDEX memory_claim_predicate IF NOT EXISTS
FOR (c:MemoryClaim) ON (c.predicate);

CREATE INDEX memory_claim_recorded_at IF NOT EXISTS
FOR (c:MemoryClaim) ON (c.recordedAt);

CREATE INDEX memory_claim_effective_at IF NOT EXISTS
FOR (c:MemoryClaim) ON (c.effectiveAt);

CREATE INDEX memory_evidence_claim_id IF NOT EXISTS
FOR (ev:MemoryEvidence) ON (ev.claimId);

CREATE INDEX memory_evidence_observation_id IF NOT EXISTS
FOR (ev:MemoryEvidence) ON (ev.observationId);

CREATE INDEX memory_entity_external_id IF NOT EXISTS
FOR (e:MemoryEntity) ON (e.externalId);

CREATE INDEX memory_entity_canonical_name IF NOT EXISTS
FOR (e:MemoryEntity) ON (e.canonicalName);

CREATE FULLTEXT INDEX memory_claim_fulltext IF NOT EXISTS
FOR (c:MemoryClaim) ON EACH [c.normalizedText, c.predicate, c.valueText, c.claimKey, c.factGroupKey];

CREATE FULLTEXT INDEX memory_entity_identity_fulltext IF NOT EXISTS
FOR (e:MemoryEntity) ON EACH [e.label, e.canonicalName, e.summary, e.id];

CREATE VECTOR INDEX memory_claim_embedding IF NOT EXISTS
FOR (c:MemoryClaim) ON (c.embedding)
OPTIONS {indexConfig: {`vector.dimensions`: 384, `vector.similarity_function`: 'cosine'}};

CREATE INDEX memory_active_relates_to_best_claim IF NOT EXISTS
FOR ()-[r:ACTIVE_RELATES_TO]-() ON (r.bestActiveClaimId);
