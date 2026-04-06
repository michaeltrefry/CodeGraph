// Full-text index for node search (name + qualifiedName)
CREATE FULLTEXT INDEX code_node_search IF NOT EXISTS
FOR (n:CodeNode) ON EACH [n.name, n.qualifiedName]
