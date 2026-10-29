using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Mcp.Hub;
using CodeGraph.Services.Work;
using CodeGraph.Tests.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The work tools reach a default token: they are in the hub catalog, pre-selected for new tokens,
/// and 074_grant_work_tools_to_selected_tokens.sql adds them once to existing selected tokens.
/// </summary>
public sealed class WorkToolGrantMigrationTests
{
    private const string GrantMigration = "074_grant_work_tools_to_selected_tokens.sql";

    [Fact]
    public async Task The_catalog_lists_every_work_tool_enabled_and_selected_by_default()
    {
        var store = new InMemoryMcpHubStore();
        await new McpHubCatalogSeeder(store, new InMemoryMcpSensitiveColumnStore()).EnsureCatalogAsync();
        var tools = (await store.ListToolsAsync()).ToDictionary(tool => tool.ToolName);

        foreach (var definition in WorkTools.All)
        {
            var tool = tools[definition.Name];
            (tool.Enabled, tool.DefaultSelected, tool.IsAvailable, tool.ProviderKey).ShouldBe((true, true, true, "codegraph"), definition.Name);
            tool.ReadOnly.ShouldBe(definition.ReadOnly, definition.Name);
            McpHubExplicitEntitlementPolicy.RequiresExplicitSelection(definition.Name).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task The_grant_adds_the_work_tools_to_existing_selected_tokens_once()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString) { Database = $"cg_mig_074_{Guid.NewGuid():N}" };
        var sourceMigrations = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations"));
        var migrationsPath = Path.Combine(Path.GetTempPath(), $"codegraph-migrations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(migrationsPath);
        try
        {
            foreach (var file in Directory.EnumerateFiles(sourceMigrations, "*.sql")
                         .Where(file => string.CompareOrdinal(Path.GetFileName(file), GrantMigration) < 0))
                File.Copy(file, Path.Combine(migrationsPath, Path.GetFileName(file)));

            var runner = CreateRunner(builder.ConnectionString, migrationsPath);
            await runner.ApplyConfiguredMigrationsAsync();
            await using var context = new CodeGraphDbContext(new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(builder.ConnectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options);
            var tokens = new MySqlMcpPersonalAccessTokenStore(context);
            var hub = new MySqlMcpHubStore(context, new PassthroughEncryptor());
            var selected = await CreateTokenAsync(tokens, "selected", "selected");
            await hub.ReplaceTokenEntitlementsAsync(selected, ["search_graph"]);
            var all = await CreateTokenAsync(tokens, "all", "all");
            var revoked = await CreateTokenAsync(tokens, "revoked", "selected", DateTime.UtcNow);
            await hub.ReplaceTokenEntitlementsAsync(revoked, ["search_graph"]);

            File.Copy(Path.Combine(sourceMigrations, GrantMigration), Path.Combine(migrationsPath, GrantMigration));
            await runner.ApplyConfiguredMigrationsAsync();

            var workTools = WorkTools.All.Select(tool => tool.Name).ToList();
            foreach (var tool in workTools)
            {
                (await hub.IsTokenEntitledAsync(selected, tool)).ShouldBeTrue(tool);
                (await hub.IsTokenEntitledAsync(all, tool)).ShouldBeTrue(tool);
            }

            (await hub.GetTokenEntitlementsAsync(selected)).Order().ShouldBe(workTools.Append("search_graph").Order());
            (await hub.GetTokenEntitlementsAsync(all)).ShouldBeEmpty();
            (await hub.GetTokenEntitlementsAsync(revoked)).ShouldBe(["search_graph"]);

            // A later deliberate deselection survives a restart.
            await hub.ReplaceTokenEntitlementsAsync(selected, ["search_graph"]);
            await CreateRunner(builder.ConnectionString, migrationsPath).ApplyConfiguredMigrationsAsync();
            (await hub.IsTokenEntitledAsync(selected, "next_work")).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(migrationsPath, recursive: true);
            await MySqlConnection.ClearAllPoolsAsync();
            await using var admin = new MySqlConnection(new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ConnectionString);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS `{builder.Database}`");
        }
    }

    private static async Task<long> CreateTokenAsync(MySqlMcpPersonalAccessTokenStore store, string name, string mode, DateTime? revokedAt = null) =>
        (await store.CreateMcpPersonalAccessTokenAsync(new McpPersonalAccessTokenEntity
        {
            Username = "michael",
            TokenName = name,
            TokenPrefixValue = "cgmcp_" + name,
            TokenHash = "hash-" + name,
            LastFour = "abcd",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            RevokedAt = revokedAt,
            EntitlementMode = mode
        })).Id;

    private static MariaDbMigrationRunner CreateRunner(string connectionString, string migrationsPath) =>
        new(Options.Create(new MariaDbStorageOptions { ConnectionString = connectionString, MigrationsPath = migrationsPath, MigrationLockTimeoutSeconds = 30 }),
            NullLogger<MariaDbMigrationRunner>.Instance);

    private sealed class PassthroughEncryptor : IAesEncryptor
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }
}
