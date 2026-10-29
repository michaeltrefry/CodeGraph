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
/// 092_rename_project_to_repository.sql (sc-27867): the columns that held a repository name are
/// renamed with their rows, foreign keys and cascade intact, the work Projects of 073-091 keep their
/// repository links, and the renamed MCP tools keep the admin-owned catalog state and the token
/// grants of the tools they replace.
/// </summary>
public sealed class MariaDbRepositoryRenameMigrationTests
{
    private const string RenameMigration = "092_rename_project_to_repository.sql";

    // The database already ran every earlier migration (the work tracking chain 073-089 and the 091
    // column fix), as a production database has; the rename is numbered last so a fresh database
    // runs it last too.
    [Fact]
    public async Task RenameMigration_KeepsRowsCascadeHubStateAndGrantsAcrossRestartsAndSeeding()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"cg_mig_092_{Guid.NewGuid():N}"
        };
        var sourceMigrations = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations"));
        var migrationsPath = Path.Combine(Path.GetTempPath(), $"codegraph-migrations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(migrationsPath);

        try
        {
            foreach (var file in Directory.EnumerateFiles(sourceMigrations, "*.sql")
                         .Where(file => string.CompareOrdinal(Path.GetFileName(file), RenameMigration) < 0))
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
                await conn.ExecuteAsync(LegacyRepositoryRowsSql);
                await conn.ExecuteAsync(WorkProjectRowsSql);
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
            (await check.ExecuteScalarAsync<string>(
                "SELECT CONCAT_WS('|', (SELECT COUNT(*) FROM nodes WHERE repository = 'orders'), " +
                "(SELECT COUNT(*) FROM edges WHERE repository = 'orders'), " +
                "(SELECT COUNT(*) FROM file_hashes WHERE repository = 'orders'), " +
                "(SELECT COUNT(*) FROM sync_state WHERE repository = 'orders'), " +
                "(SELECT COUNT(*) FROM file_metrics WHERE repository = 'orders'), " +
                "(SELECT COUNT(*) FROM cross_repo_edges WHERE source_repository = 'orders' AND target_repository = 'billing'), " +
                "(SELECT COUNT(*) FROM repo_clusters WHERE repository_name = 'orders'))"))
                .ShouldBe("2|1|1|1|1|1|1");
            // The work Project and its repository links (073-089) are untouched, and its knowledge
            // still joins the renamed graph columns.
            (await check.ExecuteScalarAsync<string>(
                "SELECT GROUP_CONCAT(CONCAT(pr.repository_name, ':', pr.role) ORDER BY pr.repository_name) " +
                "FROM work_projects p JOIN work_project_repositories pr ON pr.project_id = p.id WHERE p.project_key = 'SHOP'"))
                .ShouldBe("billing:dependency,orders:owned");
            (await check.ExecuteScalarAsync<string>("SELECT repository_name FROM work_items WHERE number = 1")).ShouldBe("orders");
            (await check.ExecuteScalarAsync<string>(
                "SELECT h.content_hash FROM knowledge_entry_files kf JOIN file_hashes h " +
                "ON h.repository = kf.repository_name AND h.rel_path = kf.path"))
                .ShouldBe("hash-a");
            (await check.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM knowledge_entry_nodes kn JOIN nodes n ON n.repository = kn.repository_name AND n.qualified_name = kn.qualified_name"))
                .ShouldBe(1);
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

    // Rows in every renamed graph table, written under the old column names.
    private const string LegacyRepositoryRowsSql = """
        INSERT INTO repositories (name) VALUES ('billing');
        INSERT INTO nodes (project, label, name, qualified_name) VALUES
            ('orders', 'Class', 'OrderService', 'Orders.OrderService'),
            ('orders', 'Method', 'Place', 'Orders.OrderService.Place'),
            ('billing', 'Class', 'Invoice', 'Billing.Invoice');
        INSERT INTO edges (project, source_id, target_id, type)
            SELECT 'orders', s.id, t.id, 'CALLS' FROM nodes s JOIN nodes t
            WHERE s.qualified_name = 'Orders.OrderService' AND t.qualified_name = 'Orders.OrderService.Place';
        INSERT INTO cross_repo_edges (source_project, target_project, source_node_id, target_node_id, type)
            SELECT 'orders', 'billing', s.id, t.id, 'HTTP_CALLS' FROM nodes s JOIN nodes t
            WHERE s.qualified_name = 'Orders.OrderService.Place' AND t.qualified_name = 'Billing.Invoice';
        INSERT INTO file_hashes (project, rel_path, content_hash) VALUES ('orders', 'src/a.cs', 'hash-a');
        INSERT INTO sync_state (project) VALUES ('orders');
        INSERT INTO file_metrics (project, file_path, computed_at) VALUES ('orders', 'src/a.cs', UTC_TIMESTAMP());
        INSERT INTO repo_clusters (project_name, cluster_id, computed_at) VALUES ('orders', 1, UTC_TIMESTAMP());
        """;

    // A work Project (073) owning one repository and depending on another, with an item and a
    // knowledge entry (078) scoped to a file and a node of the owned repository.
    private const string WorkProjectRowsSql = """
        INSERT INTO work_projects (id, project_key, name, goals_json, created_by, created_at, updated_at)
            VALUES ('00000000-0000-0000-0000-000000000001', 'SHOP', 'Shop', '[]', 'michael', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
        INSERT INTO work_project_repositories (project_id, repository_name, role, position) VALUES
            ('00000000-0000-0000-0000-000000000001', 'orders', 'owned', 0),
            ('00000000-0000-0000-0000-000000000001', 'billing', 'dependency', 1);
        INSERT INTO work_items (id, project_id, number, item_type, title, status, priority, repository_name, created_by, created_at, updated_at)
            VALUES ('00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000001', 1, 'task', 'Place orders',
                    'todo', 'medium', 'orders', 'michael', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
        INSERT INTO knowledge_entries (id, project_id, kind, summary, summary_hash, confidence, verified_at, author, created_at, updated_at)
            VALUES ('00000000-0000-0000-0000-000000000003', '00000000-0000-0000-0000-000000000001', 'fact', 'Orders call billing',
                    SHA2('Orders call billing', 256), 'high', UTC_TIMESTAMP(6), 'michael', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
        INSERT INTO knowledge_entry_files (entry_id, repository_name, path) VALUES ('00000000-0000-0000-0000-000000000003', 'orders', 'src/a.cs');
        INSERT INTO knowledge_entry_nodes (entry_id, node_id, repository_name, qualified_name)
            SELECT '00000000-0000-0000-0000-000000000003', id, 'orders', qualified_name FROM nodes WHERE qualified_name = 'Orders.OrderService';
        """;

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
