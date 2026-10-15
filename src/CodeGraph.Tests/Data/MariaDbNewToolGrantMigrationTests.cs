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

/// <summary>
/// 066_grant_new_native_tools_to_selected_tokens.sql: existing selected-mode tokens gain the four
/// sc-25698 tools once, and a later deliberate removal survives a host restart.
/// </summary>
public sealed class MariaDbNewToolGrantMigrationTests
{
    private const string GrantMigration = "066_grant_new_native_tools_to_selected_tokens.sql";
    private static readonly string[] NewTools = ["request_overlay", "get_overlay_status", "analyze_impact_file", "file_hotspot"];

    [Fact]
    public async Task GrantMigration_AddsNewToolsToExistingSelectedTokensOnce()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"cg_mig_066_{Guid.NewGuid():N}"
        };
        var sourceMigrations = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations"));
        var migrationsPath = Path.Combine(Path.GetTempPath(), $"codegraph-migrations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(migrationsPath);

        try
        {
            // The database as it stood before the grant: every migration ordered before 066.
            foreach (var file in Directory.EnumerateFiles(sourceMigrations, "*.sql")
                         .Where(file => string.CompareOrdinal(Path.GetFileName(file), GrantMigration) < 0))
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

            var selected = await CreateTokenAsync(tokenStore, "selected", "selected");
            await hubStore.ReplaceTokenEntitlementsAsync(selected, ["search_graph"]);
            var selectedWithOne = await CreateTokenAsync(tokenStore, "selected-with-one", "selected");
            await hubStore.ReplaceTokenEntitlementsAsync(selectedWithOne, ["search_graph", "file_hotspot"]);
            var all = await CreateTokenAsync(tokenStore, "all", "all");
            var revoked = await CreateTokenAsync(tokenStore, "revoked", "selected", revokedAt: DateTime.UtcNow);
            await hubStore.ReplaceTokenEntitlementsAsync(revoked, ["search_graph"]);

            foreach (var tool in NewTools)
                (await hubStore.IsTokenEntitledAsync(selected, tool)).ShouldBeFalse();

            File.Copy(Path.Combine(sourceMigrations, GrantMigration), Path.Combine(migrationsPath, GrantMigration));
            await runner.ApplyConfiguredMigrationsAsync();

            foreach (var tool in NewTools)
            {
                (await hubStore.IsTokenEntitledAsync(selected, tool)).ShouldBeTrue(tool);
                (await hubStore.IsTokenEntitledAsync(selectedWithOne, tool)).ShouldBeTrue(tool);
                (await hubStore.IsTokenEntitledAsync(all, tool)).ShouldBeTrue(tool);
            }

            // The pre-existing selection is untouched, the all-mode token gets no allow-list rows
            // (that would silently narrow it), and a revoked token is not granted anything.
            (await hubStore.GetTokenEntitlementsAsync(selected)).Order().ShouldBe(NewTools.Append("search_graph").Order());
            (await hubStore.GetTokenEntitlementsAsync(selectedWithOne)).Order().ShouldBe(NewTools.Append("search_graph").Order());
            (await hubStore.GetTokenEntitlementsAsync(all)).ShouldBeEmpty();
            (await hubStore.GetTokenEntitlementsAsync(revoked)).ShouldBe(["search_graph"]);

            // The owner later deselects one tool in the UI; a host restart must not re-grant it.
            await hubStore.ReplaceTokenEntitlementsAsync(selected, ["search_graph", "get_overlay_status", "analyze_impact_file", "file_hotspot"]);
            await CreateRunner(builder.ConnectionString, migrationsPath).ApplyConfiguredMigrationsAsync();

            (await hubStore.IsTokenEntitledAsync(selected, "request_overlay")).ShouldBeFalse();
            (await hubStore.IsTokenEntitledAsync(selected, "get_overlay_status")).ShouldBeTrue();

            // A token created after the grant follows its own selection only.
            var created = await CreateTokenAsync(tokenStore, "created-later", "selected");
            await hubStore.ReplaceTokenEntitlementsAsync(created, ["search_graph"]);
            await CreateRunner(builder.ConnectionString, migrationsPath).ApplyConfiguredMigrationsAsync();
            (await hubStore.IsTokenEntitledAsync(created, "request_overlay")).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(migrationsPath, recursive: true);
            var adminBuilder = new MySqlConnectionStringBuilder(connectionString) { Database = "" };
            await using var adminConn = new MySqlConnection(adminBuilder.ConnectionString);
            await adminConn.OpenAsync();
            // Release pooled sessions (idle ones linger ~180s per database) before dropping.
            await MySqlConnection.ClearAllPoolsAsync();
            await adminConn.ExecuteAsync($"DROP DATABASE IF EXISTS `{builder.Database}`");
        }
    }

    private static async Task<long> CreateTokenAsync(
        MySqlMcpPersonalAccessTokenStore store,
        string name,
        string mode,
        DateTime? revokedAt = null)
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
            RevokedAt = revokedAt,
            EntitlementMode = mode
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
