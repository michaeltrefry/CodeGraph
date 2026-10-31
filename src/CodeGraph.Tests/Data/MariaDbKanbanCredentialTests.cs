using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Mcp.Hub;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Data;

/// <summary>
/// sc-28194: the KanbanBoard work import reads kanban/apiToken from MCP Hub credentials. A credential
/// row needs its provider row (foreign key), so the catalog seeder registers a kanban provider.
/// </summary>
public sealed class MariaDbKanbanCredentialTests
{
    [Fact]
    public async Task The_seeded_catalog_lets_the_kanban_import_token_be_saved_and_read()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString) { Database = $"cg_kanban_cred_{Guid.NewGuid():N}" };
        var migrations = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations"));
        try
        {
            await new MariaDbMigrationRunner(
                Options.Create(new MariaDbStorageOptions { ConnectionString = builder.ConnectionString, MigrationsPath = migrations, MigrationLockTimeoutSeconds = 30 }),
                NullLogger<MariaDbMigrationRunner>.Instance).ApplyConfiguredMigrationsAsync();

            // Without the provider row the save is refused by the database.
            await using (var bare = Context(builder.ConnectionString))
                await Should.ThrowAsync<DbUpdateException>(() =>
                    new MySqlMcpHubStore(bare, new PassthroughEncryptor()).SetCredentialValueAsync("kanban", "apiToken", "kb_token", "owner"));

            await using var context = Context(builder.ConnectionString);
            var store = new MySqlMcpHubStore(context, new PassthroughEncryptor());
            await new McpHubCatalogSeeder(store, new InMemoryMcpSensitiveColumnStore()).EnsureCatalogAsync();

            await store.SetCredentialValueAsync("kanban", "apiToken", "kb_token", "owner");
            (await store.GetCredentialValueAsync("kanban", "apiToken")).ShouldBe("kb_token");
            (await store.ListCredentialsAsync()).ShouldContain(credential => credential.ProviderKey == "kanban" && credential.CredentialKey == "apiToken");
            (await store.ListToolsAsync()).ShouldNotContain(tool => tool.ProviderKey == "kanban");
        }
        finally
        {
            var admin = new MySqlConnectionStringBuilder(connectionString) { Database = "" };
            await using var connection = new MySqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await MySqlConnection.ClearAllPoolsAsync();
            await connection.ExecuteAsync($"DROP DATABASE IF EXISTS `{builder.Database}`");
        }
    }

    private static CodeGraphDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(connectionString, ServerVersion.Create(new Version(11, 8, 0), Pomelo.EntityFrameworkCore.MySql.Infrastructure.ServerType.MariaDb))
            .Options);

    private sealed class PassthroughEncryptor : IAesEncryptor
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }
}
