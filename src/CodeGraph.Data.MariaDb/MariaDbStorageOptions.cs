namespace CodeGraph.Data.MariaDb;

public class MariaDbStorageOptions
{
    public string ConnectionString { get; set; } = "";
    public string MigrationsPath { get; set; } = "sql/migrations";
    public int MigrationLockTimeoutSeconds { get; set; } = 120;
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// Command timeout, in seconds, for each statement of a graph snapshot write (file-slice and
    /// full-repository replacement). MySqlConnector's 30 s default is far too short for a large repository:
    /// replacing a slice of ~130k nodes cascades the delete to ~900k edges. 0 means no limit.
    /// </summary>
    public int GraphWriteCommandTimeoutSeconds { get; set; } = 3600;

    /// <summary>
    /// Node ids deleted per statement during a snapshot write, bounding each cascaded edge delete.
    /// Larger batches let InnoDB delete many edge rows per index/data page it reads: replacing the
    /// ~93k-node / ~730k-edge inference slice took 72 s at 2,000 ids per statement and 23 s at
    /// 25,000 with the default 128 MB buffer pool (6.6 s vs 5.4 s once the tables fit in a 2 GB
    /// pool). Each statement stays a few seconds, so cancellation still takes effect between them.
    /// </summary>
    public int GraphDeleteBatchSize { get; set; } = 25_000;

    // Encryption key for database source connection strings and LLM tokens (AES-256, base64-encoded).
    public string? EncryptionKey { get; set; }
}
