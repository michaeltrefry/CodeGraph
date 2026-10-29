using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Mcp.Hub;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

/// <summary>
/// 076_rename_project_to_repository.sql (sc-27867): the columns that held a repository name are
/// renamed with their rows, foreign keys and cascade intact, and the renamed MCP tools keep the
/// admin-owned catalog state and the token grants of the tools they replace.
/// </summary>
public sealed class MariaDbRepositoryRenameMigrationTests
{
    private const string RenameMigration = "076_rename_project_to_repository.sql";

    // laterMigrationsAlreadyApplied: the database already ran every migration numbered after 076
    // (the runner orders by file name and tracks applied scripts by name, so 076 still applies).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameMigration_KeepsRowsCascadeHubStateAndGrantsAcrossRestartsAndSeeding(bool laterMigrationsAlreadyApplied)
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"cg_mig_076_{Guid.NewGuid():N}"
        };
        var sourceMigrations = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations"));
        var migrationsPath = Path.Combine(Path.GetTempPath(), $"codegraph-migrations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(migrationsPath);

        try
        {
            foreach (var file in Directory.EnumerateFiles(sourceMigrations, "*.sql")
                         .Where(file => laterMigrationsAlreadyApplied
                             ? Path.GetFileName(file) != RenameMigration
                             : string.CompareOrdinal(Path.GetFileName(file), RenameMigration) < 0))
            {
                File.Copy(file, Path.Combine(migrationsPath, Path.GetFileName(file)));
            }

            var runner = CreateRunner(builder.ConnectionString, migrationsPath);
            await runner.ApplyConfiguredMigrationsAsync();

            await using var context = new CodeGraphDbContext(new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(builder.ConnectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options);
            var tokenStore = new MySqlMcpPersonalAccessTokenStore(context);
            var hubStore = new MySqlMcpHubStore(context, new PassthroughEncryptor());

            // The catalog as the previous release left it: an admin disabled search_projects and
            // changed its access class; get_project_health was left enabled and default-selected.
            await new McpHubCatalogSeederForOldNames(hubStore).SeedAsync();
            await using (var conn = new MySqlConnection(builder.ConnectionString))
            {
                await conn.ExecuteAsync(
                    "UPDATE mcp_hub_tools SET enabled = 0, default_selected = 0, access_class = 'admin' WHERE tool_name = 'search_projects'");
                await conn.ExecuteAsync(
                    "UPDATE mcp_hub_tools SET enabled = 1, default_selected = 1, access_class = 'read' WHERE tool_name = 'get_project_health'");
                await conn.ExecuteAsync("INSERT INTO repositories (name) VALUES ('orders')");
                await conn.ExecuteAsync(
                    "INSERT INTO branch_overlays (project, requested_ref, created_at) VALUES ('orders', 'main', UTC_TIMESTAMP(6))");
                await conn.ExecuteAsync(
                    "INSERT INTO branch_overlay_files (overlay_id, file_path, change_kind) SELECT id, 'a.cs', 'modified' FROM branch_overlays");
            }

            var selected = await CreateTokenAsync(tokenStore, "selected");
            await hubStore.ReplaceTokenEntitlementsAsync(selected, ["search_graph", "search_projects", "get_project_health"]);

            File.Copy(Path.Combine(sourceMigrations, RenameMigration), Path.Combine(migrationsPath, RenameMigration));
            await runner.ApplyConfiguredMigrationsAsync();
            // A second host start, then the catalog seeder the hosts run on every start.
            await CreateRunner(builder.ConnectionString, migrationsPath).ApplyConfiguredMigrationsAsync();
            await new McpHubCatalogSeeder(hubStore, new InMemoryMcpSensitiveColumnStore()).EnsureCatalogAsync();

            // Grants moved to the new names; unrelated grants are kept.
            (await hubStore.GetTokenEntitlementsAsync(selected)).Order()
                .ShouldBe(new[] { "get_repository_health", "search_graph", "search_repositories" });

            // Admin-owned hub state followed the rename; the seeder did not re-enable the tool.
            var tools = (await hubStore.ListToolsAsync()).ToDictionary(tool => tool.ToolName);
            tools.ShouldNotContainKey("search_projects");
            tools.ShouldNotContainKey("get_project_health");
            tools["search_repositories"].Enabled.ShouldBeFalse();
            tools["search_repositories"].DefaultSelected.ShouldBeFalse();
            tools["search_repositories"].AccessClass.ShouldBe("admin");
            tools["get_repository_health"].Enabled.ShouldBeTrue();
            tools["get_repository_health"].DefaultSelected.ShouldBeTrue();

            await using var check = new MySqlConnection(builder.ConnectionString);
            (await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM branch_overlays WHERE repository = 'orders'")).ShouldBe(1);
            // The renamed foreign key still cascades from repositories.
            await check.ExecuteAsync("DELETE FROM repositories WHERE name = 'orders'");
            (await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM branch_overlays")).ShouldBe(0);
            (await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM branch_overlay_files")).ShouldBe(0);
        }
        finally
        {
            Directory.Delete(migrationsPath, recursive: true);
            var adminBuilder = new MySqlConnectionStringBuilder(connectionString) { Database = "" };
            await using var adminConn = new MySqlConnection(adminBuilder.ConnectionString);
            await adminConn.OpenAsync();
            await MySqlConnection.ClearAllPoolsAsync();
            await adminConn.ExecuteAsync($"DROP DATABASE IF EXISTS `{builder.Database}`");
        }
    }

    // What the previous release's seeder wrote for the four tools that were renamed.
    private sealed class McpHubCatalogSeederForOldNames(IMcpHubStore store)
    {
        public async Task SeedAsync()
        {
            var now = DateTime.UtcNow;
            await store.UpsertProviderAsync(new McpHubProviderEntity
            {
                ProviderKey = "codegraph", DisplayName = "CodeGraph", Description = "", Enabled = true,
                SourceVisible = true, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            foreach (var name in new[] { "list_projects", "search_projects", "project_report", "get_project_health" })
            {
                await store.UpsertToolAsync(new McpHubToolEntity
                {
                    ToolName = name, ProviderKey = "codegraph", ProviderType = "native", DisplayName = name,
                    Description = "old", ReadOnly = true, Enabled = true, IsAvailable = true,
                    DefaultSelected = true, AccessClass = "read", CreatedAtUtc = now, UpdatedAtUtc = now
                });
            }
        }
    }

    private static async Task<long> CreateTokenAsync(MySqlMcpPersonalAccessTokenStore store, string name)
    {
        var token = await store.CreateMcpPersonalAccessTokenAsync(new McpPersonalAccessTokenEntity
        {
            Username = "michael",
            TokenName = name,
            TokenPrefixValue = "cgmcp_" + name,
            TokenHash = "hash-" + name,
            LastFour = "abcd",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            EntitlementMode = "selected"
        });
        return token.Id;
    }

    private static MariaDbMigrationRunner CreateRunner(string connectionString, string migrationsPath) =>
        new(
            Options.Create(new MariaDbStorageOptions
            {
                ConnectionString = connectionString,
                MigrationsPath = migrationsPath,
                MigrationLockTimeoutSeconds = 30
            }),
            NullLogger<MariaDbMigrationRunner>.Instance);

    private sealed class PassthroughEncryptor : IAesEncryptor
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }
}
