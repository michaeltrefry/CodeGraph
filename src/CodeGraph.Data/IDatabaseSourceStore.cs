namespace CodeGraph.Data;

/// <summary>MCP Hub exposure modes for a database source — see Shortcut sc-1058.</summary>
public static class McpSourceExposureModes
{
    public const string SchemaOnly = "SchemaOnly";
    public const string NamedToolsOnly = "NamedToolsOnly";
    public const string AggregateOnly = "AggregateOnly";
    public const string ReadOnlySql = "ReadOnlySql";

    public static readonly IReadOnlyList<string> All =
        [SchemaOnly, NamedToolsOnly, AggregateOnly, ReadOnlySql];

    public static bool IsValid(string? mode) =>
        mode is not null && All.Contains(mode, StringComparer.OrdinalIgnoreCase);
}

public interface IDatabaseSourceStore
{
    Task<IReadOnlyList<DatabaseSourceEntity>> ListAsync();
    Task<DatabaseSourceEntity?> GetAsync(long id);
    Task<DatabaseSourceEntity> CreateAsync(DatabaseSourceEntity entity);
    Task<DatabaseSourceEntity?> UpdateAsync(long id, string? serverName, string? databaseName, string? connectionString, bool? enabled);

    /// <summary>Updates only the MCP Hub exposure controls of a source. Null arguments are left unchanged.</summary>
    Task<DatabaseSourceEntity?> UpdateMcpExposureAsync(
        long id,
        bool? mcpHubEnabled,
        string? mcpExposureMode,
        string? mcpDisplayName,
        string? mcpEnvironment);
    Task<bool> DeleteAsync(long id);
    Task UpdateLastSyncedAsync(long id);
}
