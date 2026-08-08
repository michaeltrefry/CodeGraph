-- Code symbols are case-sensitive in supported languages such as Rust, C#, and TypeScript.
-- Use a binary collation for exact lookup and a full-value digest for uniqueness instead of
-- the historical case-insensitive 700-character prefix index.
ALTER TABLE nodes
    MODIFY COLUMN qualified_name VARCHAR(1000)
        CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL;

ALTER TABLE nodes
    ADD COLUMN IF NOT EXISTS qualified_name_hash BINARY(32)
        GENERATED ALWAYS AS (UNHEX(SHA2(qualified_name, 256))) STORED
        AFTER qualified_name;

ALTER TABLE nodes
    DROP INDEX IF EXISTS uq_node;

ALTER TABLE nodes
    ADD UNIQUE KEY IF NOT EXISTS uq_node (project, qualified_name_hash);
