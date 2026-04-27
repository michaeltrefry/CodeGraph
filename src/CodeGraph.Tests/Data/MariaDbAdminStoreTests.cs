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

public class MariaDbAdminStoreTests
{
    [Fact]
    public void MySqlAdminStore_ImplementsStandaloneAdminContract()
    {
        typeof(IAdminStore).IsAssignableFrom(typeof(MySqlAdminStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task MySqlAdminStore_RoundTripsUsersSettingsAndPromptOverridesWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_admin_store_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        var runner = new MariaDbMigrationRunner(
            Options.Create(new MariaDbStorageOptions
            {
                ConnectionString = builder.ConnectionString,
                MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
            }),
            NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            await using var context = new CodeGraphDbContext(CreateOptions(builder.ConnectionString));
            var store = new MySqlAdminStore(context);
            var now = DateTime.UtcNow;

            var admin = await store.AddAdminUserAsync(new AdminUserEntity
            {
                Username = "codex",
                CreatedAt = now
            });

            admin.Id.ShouldBeGreaterThan(0);
            (await store.IsAdminAsync("codex")).ShouldBeTrue();
            (await store.ListAdminUsersAsync()).ShouldContain(user => user.Username == "codex");

            await store.UpsertSettingsOverrideAsync(new SettingsOverrideEntity
            {
                SettingsJson = """{"provider":"local"}""",
                UpdatedBy = "codex",
                UpdatedAt = now
            });
            await store.UpsertSettingsOverrideAsync(new SettingsOverrideEntity
            {
                SettingsJson = """{"provider":"openai"}""",
                UpdatedBy = "codex",
                UpdatedAt = now.AddMinutes(1)
            });

            (await store.GetLatestSettingsOverrideAsync())!.SettingsJson.ShouldContain("openai");

            await store.UpsertPromptOverrideAsync(new AgentPromptOverrideEntity
            {
                PromptKey = "review",
                PromptText = "First",
                UpdatedBy = "codex",
                UpdatedAt = now
            });
            await store.UpsertPromptOverrideAsync(new AgentPromptOverrideEntity
            {
                PromptKey = "review",
                PromptText = "Second",
                UpdatedBy = "codex",
                UpdatedAt = now.AddMinutes(1)
            });

            (await store.GetPromptOverrideAsync("review"))!.PromptText.ShouldBe("Second");
            (await store.ListPromptOverridesAsync()).Single(p => p.PromptKey == "review").PromptText.ShouldBe("Second");
            (await store.DeletePromptOverrideAsync("review")).ShouldBeTrue();
            (await store.GetPromptOverrideAsync("review")).ShouldBeNull();

            (await store.RemoveAdminUserAsync("codex")).ShouldBeTrue();
            (await store.IsAdminAsync("codex")).ShouldBeFalse();
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
