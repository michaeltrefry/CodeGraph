namespace CodeGraph.Data;

public class CodeGraphStorageOptions
{
    public string Provider { get; set; } = "mysql";
    public string ConnectionString { get; set; } = "";
    public string MigrationsPath { get; set; } = "sql/migrations";
    public int BatchSize { get; set; } = 500;

    // Neo4j
    public string? Neo4jUri { get; set; }
    public string? Neo4jUsername { get; set; }
    public string? Neo4jPassword { get; set; }
    public string? Neo4jDatabase { get; set; }
    public string Neo4jMigrationsPath { get; set; } = "cypher/migrations";

    // Embeddings
    public string? EmbeddingModelPath { get; set; }
    public int EmbeddingDimensions { get; set; } = 384;
    public string EmbeddingModelName { get; set; } = "all-MiniLM-L6-v2";

    public bool IsNeo4j => Provider.Equals("neo4j", StringComparison.OrdinalIgnoreCase);
}
