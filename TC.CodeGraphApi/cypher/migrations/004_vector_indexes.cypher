// Vector embedding support

CREATE CONSTRAINT embedding_unique IF NOT EXISTS
FOR (e:Embedding) REQUIRE (e.entityType, e.entityKey) IS UNIQUE;

CREATE VECTOR INDEX embedding_vector IF NOT EXISTS
FOR (e:Embedding) ON (e.vector)
OPTIONS {indexConfig: {
  `vector.dimensions`: 384,
  `vector.similarity_function`: 'cosine'
}}
