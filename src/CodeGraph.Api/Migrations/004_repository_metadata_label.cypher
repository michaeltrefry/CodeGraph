MATCH (r:Repository)
WHERE NOT r:CodeNode
SET r:RepositoryRecord
REMOVE r:Repository;

CREATE CONSTRAINT repository_name IF NOT EXISTS
FOR (r:Repository) REQUIRE r.name IS UNIQUE;

CREATE CONSTRAINT repository_record_name IF NOT EXISTS
FOR (r:RepositoryRecord) REQUIRE r.name IS UNIQUE;
