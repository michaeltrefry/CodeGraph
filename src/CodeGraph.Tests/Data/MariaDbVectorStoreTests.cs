using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class MariaDbVectorStoreTests
{
    [Fact]
    public void MySqlVectorStore_ImplementsStandaloneVectorContract()
    {
        typeof(IVectorStore).IsAssignableFrom(typeof(MySqlVectorStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task MySqlVectorStore_RoundTripsEmbeddingsWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_vector_store_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });

        var runner = new MariaDbMigrationRunner(
            storageOptions,
            NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            var store = new MySqlVectorStore(storageOptions);
            await store.StoreBatchEmbeddingsAsync(
            [
                ("node", "a", [1f, 0f, 0f]),
                ("node", "b", [0f, 1f, 0f]),
                ("claim", "c", [1f, 0f, 0f])
            ]);

            var allResults = await store.SearchSimilarAsync([1f, 0f, 0f], topK: 10, minScore: 0);
            allResults.ShouldContain(result => result.EntityKey == "a" && result.Score > 0.99);
            allResults.Count.ShouldBe(3);

            var nodeResults = await store.SearchSimilarAsync([1f, 0f, 0f], entityType: "node", topK: 10, minScore: 0.5);
            nodeResults.Single().EntityKey.ShouldBe("a");

            await store.DeleteEmbeddingsAsync("node", "a");
            (await store.SearchSimilarAsync([1f, 0f, 0f], entityType: "node", topK: 10, minScore: 0.5))
                .ShouldBeEmpty();
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = ""
        };

        await using var conn = new MySqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
    }
}
