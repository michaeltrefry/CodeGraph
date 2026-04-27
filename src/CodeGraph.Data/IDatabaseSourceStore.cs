namespace CodeGraph.Data;

public interface IDatabaseSourceStore
{
    Task<IReadOnlyList<DatabaseSourceEntity>> ListAsync();
    Task<DatabaseSourceEntity?> GetAsync(long id);
    Task<DatabaseSourceEntity> CreateAsync(DatabaseSourceEntity entity);
    Task<DatabaseSourceEntity?> UpdateAsync(long id, string? serverName, string? databaseName, string? connectionString, bool? enabled);
    Task<bool> DeleteAsync(long id);
    Task UpdateLastSyncedAsync(long id);
}
