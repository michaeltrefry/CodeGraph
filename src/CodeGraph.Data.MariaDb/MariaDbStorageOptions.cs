namespace CodeGraph.Data.MariaDb;

public class MariaDbStorageOptions
{
    public string ConnectionString { get; set; } = "";
    public string MigrationsPath { get; set; } = "sql/migrations";
    public int MigrationLockTimeoutSeconds { get; set; } = 120;
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// Command timeout, in seconds, for each statement of a graph snapshot write (file-slice and
    /// full-project replacement). MySqlConnector's 30 s default is far too short for a large repository:
    /// replacing a slice of ~130k nodes cascades the delete to ~900k edges. 0 means no limit.
    /// </summary>
    public int GraphWriteCommandTimeoutSeconds { get; set; } = 3600;

    /// <summary>Node ids deleted per statement during a snapshot write, bounding each cascaded edge delete.</summary>
    public int GraphDeleteBatchSize { get; set; } = 2000;

    // Encryption key for database source connection strings and LLM tokens (AES-256, base64-encoded).
    public string? EncryptionKey { get; set; }
}
