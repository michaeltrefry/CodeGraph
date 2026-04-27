CREATE INDEX ix_memory_entities_v2_username_type_updated_at
    ON memory_entities_v2 (username, type, updated_at);

CREATE INDEX ix_memory_claims_username_fact_group_key_recorded_at
    ON memory_claims (username, fact_group_key, recorded_at);

CREATE INDEX ix_memory_claims_username_subject_entity_predicate
    ON memory_claims (username, subject_entity_id, predicate);

CREATE INDEX ix_memory_claims_username_object_entity_predicate
    ON memory_claims (username, object_entity_id, predicate);

CREATE INDEX ix_memory_claims_username_status_recorded_at
    ON memory_claims (username, status, recorded_at);

CREATE INDEX ix_memory_claim_edges_username_from_claim_edge_type
    ON memory_claim_edges (username, from_claim_id, edge_type);

CREATE INDEX ix_memory_claim_edges_username_to_claim_edge_type
    ON memory_claim_edges (username, to_claim_id, edge_type);

CREATE INDEX ix_memory_entity_edges_username_from_entity_edge_type
    ON memory_entity_edges (username, from_entity_id, edge_type);

CREATE INDEX ix_memory_entity_edges_username_to_entity_edge_type
    ON memory_entity_edges (username, to_entity_id, edge_type);

CREATE INDEX ix_memory_observations_v2_username_resolution_status_created_at
    ON memory_observations_v2 (username, resolution_status, created_at);

CREATE INDEX ix_memory_observations_v2_username_claim_id
    ON memory_observations_v2 (username, claim_id);

CREATE INDEX ix_memory_observations_v2_username_entity_id
    ON memory_observations_v2 (username, entity_id);

CREATE INDEX ix_memory_evidence_username_claim_id
    ON memory_evidence (username, claim_id);

CREATE INDEX ix_memory_evidence_username_observation_id
    ON memory_evidence (username, observation_id);
