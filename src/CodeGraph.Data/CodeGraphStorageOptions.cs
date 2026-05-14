namespace CodeGraph.Data;

public class CodeGraphStorageOptions
{
    public int BatchSize { get; set; } = 500;
    public string Provider { get; set; } = "MariaDb";

    // Neo4j
    public string Neo4jUri { get; set; } = "bolt://localhost:7687";
    public string Neo4jUsername { get; set; } = "neo4j";
    public string Neo4jPassword { get; set; } = "";
    public string? Neo4jDatabase { get; set; }
    public string Neo4jMigrationsPath { get; set; } = "Migrations";

    // MariaDB/MySQL
    public string MariaDbConnectionString { get; set; } = "";
    public string MariaDbMigrationsPath { get; set; } = "sql/migrations";
    public string? MariaDbEncryptionKey { get; set; }

    // Embeddings
    public string? EmbeddingModelPath { get; set; }
    public int EmbeddingDimensions { get; set; } = 768;
    public string EmbeddingModelName { get; set; } = "nomic-embed-text-v1.5";
    public int EmbeddingMaxTokens { get; set; } = 8192;
}
