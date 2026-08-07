CREATE CONSTRAINT project_index_lock_unique IF NOT EXISTS
FOR (l:ProjectIndexLock) REQUIRE l.project IS UNIQUE;
