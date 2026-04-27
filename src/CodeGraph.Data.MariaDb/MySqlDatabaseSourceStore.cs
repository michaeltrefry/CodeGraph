using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public class MySqlDatabaseSourceStore(
    CodeGraphDbContext db,
    ConnectionStringEncryptor encryptor) : IDatabaseSourceStore
{
    public async Task<IReadOnlyList<DatabaseSourceEntity>> ListAsync()
    {
        var sources = await db.DatabaseSources.AsNoTracking()
            .OrderBy(d => d.ServerName)
            .ThenBy(d => d.DatabaseName)
            .ToListAsync();
        return sources.Select(CloneDecrypted).ToList();
    }

    public async Task<DatabaseSourceEntity?> GetAsync(long id)
    {
        var source = await db.DatabaseSources.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
        return source is null ? null : CloneDecrypted(source);
    }

    public async Task<DatabaseSourceEntity> CreateAsync(DatabaseSourceEntity entity)
    {
        var now = DateTime.UtcNow;
        var plainConnectionString = entity.ConnectionString;
        entity.ConnectionString = encryptor.Encrypt(plainConnectionString);
        entity.CreatedAt = entity.CreatedAt == default ? now : entity.CreatedAt;
        entity.UpdatedAt = entity.UpdatedAt == default ? now : entity.UpdatedAt;

        db.DatabaseSources.Add(entity);
        await db.SaveChangesAsync();

        return CloneWithConnectionString(entity, plainConnectionString);
    }

    public async Task<DatabaseSourceEntity?> UpdateAsync(
        long id,
        string? serverName,
        string? databaseName,
        string? connectionString,
        bool? enabled)
    {
        var source = await db.DatabaseSources.FirstOrDefaultAsync(d => d.Id == id);
        if (source is null)
        {
            return null;
        }

        if (serverName is not null)
        {
            source.ServerName = serverName;
        }

        if (databaseName is not null)
        {
            source.DatabaseName = databaseName;
        }

        if (connectionString is not null)
        {
            source.ConnectionString = encryptor.Encrypt(connectionString);
        }

        if (enabled is not null)
        {
            source.Enabled = enabled.Value;
        }

        source.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return CloneDecrypted(source);
    }

    public async Task<bool> DeleteAsync(long id)
    {
        var source = await db.DatabaseSources.FirstOrDefaultAsync(d => d.Id == id);
        if (source is null)
        {
            return false;
        }

        db.DatabaseSources.Remove(source);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task UpdateLastSyncedAsync(long id)
    {
        var source = await db.DatabaseSources.FirstOrDefaultAsync(d => d.Id == id);
        if (source is null)
        {
            return;
        }

        source.LastSyncedAt = DateTime.UtcNow;
        source.UpdatedAt = source.LastSyncedAt.Value;
        await db.SaveChangesAsync();
    }

    private DatabaseSourceEntity CloneDecrypted(DatabaseSourceEntity source)
    {
        return CloneWithConnectionString(source, encryptor.Decrypt(source.ConnectionString));
    }

    private static DatabaseSourceEntity CloneWithConnectionString(DatabaseSourceEntity source, string connectionString)
        => new()
        {
            Id = source.Id,
            ServerName = source.ServerName,
            DatabaseName = source.DatabaseName,
            ConnectionString = connectionString,
            Enabled = source.Enabled,
            LastSyncedAt = source.LastSyncedAt,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
}
