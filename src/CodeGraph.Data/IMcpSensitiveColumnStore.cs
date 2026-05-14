namespace CodeGraph.Data;

public interface IMcpSensitiveColumnStore
{
    Task<IReadOnlyList<McpSensitiveColumnEntity>> ListAsync(CancellationToken ct = default);
    Task UpsertAsync(McpSensitiveColumnEntity entity, CancellationToken ct = default);
    Task<bool> DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// An opaque token that changes whenever sensitive-column metadata changes. Used as a
    /// cache key component so policy snapshots are reused while metadata is stable and
    /// discarded once it changes.
    /// </summary>
    Task<string> GetRevisionAsync(CancellationToken ct = default);
}
