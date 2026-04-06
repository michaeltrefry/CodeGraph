// Memory graph schema — per-user knowledge graph for LLM memory

// Constraints
CREATE CONSTRAINT memory_entity_username_id IF NOT EXISTS
FOR (e:MemoryEntity) REQUIRE (e.username, e.id) IS UNIQUE;

CREATE CONSTRAINT memory_observation_id IF NOT EXISTS
FOR (o:MemoryObservation) REQUIRE o.id IS UNIQUE;

// Indexes
CREATE INDEX memory_entity_username IF NOT EXISTS
FOR (e:MemoryEntity) ON (e.username);

CREATE INDEX memory_observation_username IF NOT EXISTS
FOR (o:MemoryObservation) ON (o.username);

CREATE INDEX memory_entity_username_updatedAt IF NOT EXISTS
FOR (e:MemoryEntity) ON (e.username, e.updatedAt);

// Fulltext index for entity search
CREATE FULLTEXT INDEX memory_entity_fulltext IF NOT EXISTS
FOR (e:MemoryEntity) ON EACH [e.label, e.summary, e.id];
