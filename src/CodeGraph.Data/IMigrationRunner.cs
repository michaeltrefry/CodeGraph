namespace CodeGraph.Data;

/// <summary>
/// Applies SQL migration scripts to the database.
/// </summary>
public interface IMigrationRunner
{
    Task ApplyMigrationsAsync(string migrationsPath);
}
