// Memory entity vector index (separate file — schema ops can't mix with data writes in some Neo4j versions)
CREATE VECTOR INDEX memory_entity_embedding IF NOT EXISTS
FOR (e:MemoryEntity) ON (e.embedding)
OPTIONS {indexConfig: {`vector.dimensions`: 384, `vector.similarity_function`: 'cosine'}};
