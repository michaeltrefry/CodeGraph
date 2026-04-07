namespace CodeGraph.Data;

/// <summary>
/// Applies pending schema migration scripts to the backing store.
/// </summary>
public interface IMigrationRunner
{
    Task ApplyMigrationsAsync(string migrationsPath);
}
