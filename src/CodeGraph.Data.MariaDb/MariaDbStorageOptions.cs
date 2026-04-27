namespace CodeGraph.Data.MariaDb;

public class MariaDbStorageOptions
{
    public string ConnectionString { get; set; } = "";
    public string MigrationsPath { get; set; } = "sql/migrations";
    public int BatchSize { get; set; } = 500;

    // Encryption key for database source connection strings (AES-256, base64-encoded).
    public string? EncryptionKey { get; set; }
}
