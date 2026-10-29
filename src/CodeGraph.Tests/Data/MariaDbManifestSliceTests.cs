using CodeGraph.Data.MariaDb;
using CodeGraph.Tests.Services;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

/// <summary>
/// sc-26145 (LensWorks re-run): against MariaDB, the Package and Module rows of unchanged
/// manifests keep their row ids across incremental runs (they used to be deleted and re-inserted
/// every run), while a changed manifest's group still gets its rows replaced.
/// </summary>
public class MariaDbManifestSliceTests
{
    private const string Repository = ManifestSliceTrackingTests.Repository;

    [Fact]
    public async Task IncrementalRuns_UnchangedManifests_KeepPackageRowIds_WhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"codegraph_manifest_slices_{Guid.NewGuid():N}"
        };
        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        var runner = new MariaDbMigrationRunner(storageOptions, NullLogger<MariaDbMigrationRunner>.Instance);
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-manifest-slices-mariadb-{Guid.NewGuid():N}");

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            async Task IndexAsync()
            {
                var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
                    .UseMySql(builder.ConnectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                    .Options;
                await using var context = new CodeGraphDbContext(dbOptions);
                var store = new MySqlGraphStore(
                    context, storageOptions, NullLogger<MySqlGraphStore>.Instance,
                    new MySqlAnalysisStore(context), new MySqlMetricsStore(context),
                    new MySqlReviewStore(context), runner);
                await ManifestSliceTrackingTests.Pipeline(store)
                    .IndexRepositoryAsync(Repository, root, ct: CancellationToken.None);
            }

            ManifestSliceTrackingTests.WriteRepository(root);
            await IndexAsync();
            var first = await ManifestRowsAsync(builder.ConnectionString);
            first.Keys.ShouldContain(qn => qn.StartsWith("package:cargo:", StringComparison.Ordinal));
            first.Keys.ShouldContain($"{Repository}:app/App.xcodeproj/project.pbxproj#target:App");
            (await ManifestHashPathsAsync(builder.ConnectionString))
                .ShouldBe(ManifestSliceTrackingTests.TrackedManifests, ignoreOrder: true);
            var maxNodeId = await MaxNodeIdAsync(builder.ConnectionString);

            // No change: every manifest row keeps its id and no node row is inserted.
            await IndexAsync();
            await IndexAsync();
            (await ManifestRowsAsync(builder.ConnectionString)).ShouldBe(first);
            (await MaxNodeIdAsync(builder.ConnectionString)).ShouldBe(maxNodeId);

            // A re-pinned Package.resolved replaces the Swift group's rows; Cargo rows stay.
            ManifestSliceTrackingTests.WriteRepository(root, sparkleVersion: "2.11.0");
            await IndexAsync();
            var afterPin = await ManifestRowsAsync(builder.ConnectionString);
            afterPin.Keys.ShouldContain(qn => qn.Contains("sparkle@2.11.0", StringComparison.Ordinal));
            foreach (var (qualifiedName, id) in first.Where(row => row.Key.StartsWith("package:cargo:", StringComparison.Ordinal)))
                afterPin[qualifiedName].ShouldBe(id);
            afterPin[$"{Repository}:app/App.xcodeproj/project.pbxproj#target:App"]
                .ShouldNotBe(first[$"{Repository}:app/App.xcodeproj/project.pbxproj#target:App"]);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            await using var conn = new MySqlConnection(new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ConnectionString);
            await conn.OpenAsync();
            await MySqlConnection.ClearAllPoolsAsync();
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{builder.Database}`");
        }
    }

    private static async Task<Dictionary<string, long>> ManifestRowsAsync(string connectionString)
    {
        await using var conn = new MySqlConnection(connectionString);
        var rows = await conn.QueryAsync<(string QualifiedName, long Id)>(
            "SELECT qualified_name AS QualifiedName, id AS Id FROM nodes WHERE repository = @Repository AND file_path IN @Paths",
            new { Repository, Paths = ManifestSliceTrackingTests.TrackedManifests });
        return rows.ToDictionary(row => row.QualifiedName, row => row.Id);
    }

    private static async Task<List<string>> ManifestHashPathsAsync(string connectionString)
    {
        await using var conn = new MySqlConnection(connectionString);
        return (await conn.QueryAsync<string>(
            "SELECT rel_path FROM file_hashes WHERE repository = @Repository AND rel_path IN @Paths",
            new { Repository, Paths = ManifestSliceTrackingTests.TrackedManifests })).ToList();
    }

    private static async Task<long> MaxNodeIdAsync(string connectionString)
    {
        await using var conn = new MySqlConnection(connectionString);
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COALESCE(MAX(id), 0) FROM nodes WHERE repository = @Repository", new { Repository });
    }
}
