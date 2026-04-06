using Neo4j.Driver;
using TC.CodeGraphApi.Data;

namespace TC.CodeGraphApi.Data.Neo4j;

/// <summary>
/// Manages the Neo4j driver lifecycle. Register as singleton — the driver
/// handles its own connection pool internally.
/// </summary>
public sealed class Neo4jSessionFactory : IAsyncDisposable
{
    private readonly IDriver _driver;
    private readonly string? _database;

    public Neo4jSessionFactory(CodeGraphStorageOptions options)
    {
        _driver = GraphDatabase.Driver(
            options.Neo4jUri ?? "bolt://localhost:7687",
            AuthTokens.Basic(
                options.Neo4jUsername ?? "neo4j",
                options.Neo4jPassword ?? ""));
        _database = options.Neo4jDatabase;
    }

    public IAsyncSession GetSession(AccessMode mode = AccessMode.Write)
    {
        return _driver.AsyncSession(builder =>
        {
            builder.WithDefaultAccessMode(mode);
            if (!string.IsNullOrEmpty(_database))
                builder.WithDatabase(_database);
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _driver.DisposeAsync();
    }
}
