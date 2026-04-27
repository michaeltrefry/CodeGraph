using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class MariaDbDatabaseSourceStoreTests
{
    [Fact]
    public void MySqlDatabaseSourceStore_ImplementsStandaloneDatabaseSourceContract()
    {
        typeof(IDatabaseSourceStore).IsAssignableFrom(typeof(MySqlDatabaseSourceStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task MySqlDatabaseSourceStore_RoundTripsEncryptedSourcesWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_db_source_store_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        var options = new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations"),
            EncryptionKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray())
        };

        var runner = new MariaDbMigrationRunner(
            Options.Create(options),
            NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            await using var context = new CodeGraphDbContext(CreateOptions(builder.ConnectionString));
            var store = new MySqlDatabaseSourceStore(context, new ConnectionStringEncryptor(Options.Create(options)));
            const string plainConnectionString = "Server=db;Database=app;User ID=app;Password=secret";

            var created = await store.CreateAsync(new DatabaseSourceEntity
            {
                ServerName = "db",
                DatabaseName = "app",
                ConnectionString = plainConnectionString,
                Enabled = true
            });

            created.Id.ShouldBeGreaterThan(0);
            created.ConnectionString.ShouldBe(plainConnectionString);

            var storedCipherText = (await context.DatabaseSources.AsNoTracking().SingleAsync()).ConnectionString;
            storedCipherText.ShouldNotBe(plainConnectionString);

            (await store.GetAsync(created.Id))!.ConnectionString.ShouldBe(plainConnectionString);
            (await store.ListAsync()).Single().ConnectionString.ShouldBe(plainConnectionString);

            var updated = await store.UpdateAsync(
                created.Id,
                serverName: "db2",
                databaseName: null,
                connectionString: "Server=db2;Database=app;User ID=app;Password=secret2",
                enabled: false);

            updated.ShouldNotBeNull();
            updated.ServerName.ShouldBe("db2");
            updated.Enabled.ShouldBeFalse();
            updated.ConnectionString.ShouldContain("db2");

            await store.UpdateLastSyncedAsync(created.Id);
            (await store.GetAsync(created.Id))!.LastSyncedAt.ShouldNotBeNull();

            (await store.DeleteAsync(created.Id)).ShouldBeTrue();
            (await store.GetAsync(created.Id)).ShouldBeNull();
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    private static DbContextOptions<CodeGraphDbContext> CreateOptions(string connectionString)
        => new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(
                connectionString,
                ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
            .Options;

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
