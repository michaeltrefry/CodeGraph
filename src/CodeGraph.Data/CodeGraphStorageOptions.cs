namespace CodeGraph.Data;

public class CodeGraphStorageOptions
{
    public int BatchSize { get; set; } = 500;

    // Neo4j
    public string Neo4jUri { get; set; } = "bolt://localhost:7687";
    public string Neo4jUsername { get; set; } = "neo4j";
    public string Neo4jPassword { get; set; } = "";
    public string? Neo4jDatabase { get; set; }
    public string Neo4jMigrationsPath { get; set; } = "Migrations";

    // Embeddings
    public string? EmbeddingModelPath { get; set; }
    public int EmbeddingDimensions { get; set; } = 384;
    public string EmbeddingModelName { get; set; } = "all-MiniLM-L6-v2";
}
