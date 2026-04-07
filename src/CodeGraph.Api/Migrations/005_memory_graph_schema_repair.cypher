// Repair memory graph schema that may have been skipped by older migration parsing
// or by edits to 001_schema.cypher after it had already been marked applied.

CREATE CONSTRAINT memory_entity_id IF NOT EXISTS
FOR (e:MemoryEntity) REQUIRE e.id IS UNIQUE;

CREATE CONSTRAINT memory_observation_id IF NOT EXISTS
FOR (o:MemoryObservation) REQUIRE o.id IS UNIQUE;

CREATE INDEX memory_entity_updatedAt IF NOT EXISTS
FOR (e:MemoryEntity) ON (e.updatedAt);

CREATE FULLTEXT INDEX memory_entity_fulltext IF NOT EXISTS
FOR (e:MemoryEntity) ON EACH [e.label, e.summary, e.id];

CREATE VECTOR INDEX memory_entity_embedding IF NOT EXISTS
FOR (e:MemoryEntity) ON (e.embedding)
OPTIONS {indexConfig: {`vector.dimensions`: 384, `vector.similarity_function`: 'cosine'}};
