using CodeGraph.Data.MariaDb;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

/// <summary>
/// Swift cross-file re-binding against the MariaDB store: the edge-property lookups, the
/// retraction of re-bound edges and the removal of orphaned stubs in the file-slice write; and
/// Swift call overload selection against argument labels read back from the store.
/// </summary>
public class MariaDbSwiftTypeRebindingTests
{
    private const string Project = "SwiftRepo";

    [Fact]
    public async Task IncrementalRuns_RebindStubsKindsAndAmbiguity_WhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"codegraph_swift_rebind_{Guid.NewGuid():N}"
        };
        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        var runner = new MariaDbMigrationRunner(storageOptions, NullLogger<MariaDbMigrationRunner>.Instance);
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-swift-mariadb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Sources"));

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            async Task IndexAsync(params string[] changed)
            {
                var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
                    .UseMySql(builder.ConnectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                    .Options;
                await using var context = new CodeGraphDbContext(dbOptions);
                var store = new MySqlGraphStore(
                    context, storageOptions, NullLogger<MySqlGraphStore>.Instance,
                    new MySqlAnalysisStore(context), new MySqlMetricsStore(context),
                    new MySqlReviewStore(context), runner);
                var pipeline = new IndexingPipeline(
                    store,
                    [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                    Options.Create(new IndexingOptions { MaxParallelFiles = 2 }),
                    new LocalFileSystem(),
                    NullLogger<IndexingPipeline>.Instance);
                await pipeline.IndexProjectAsync(Project, root,
                    changedFilesOnly: changed.Length == 0 ? null : changed, ct: CancellationToken.None);
            }

            void Write(string path, string source) => File.WriteAllText(Path.Combine(root, path), source);

            const string member = $"{Project}:Sources/Ext.swift#extension:Square#method:doubled()";
            const string sub = $"{Project}:Sources/Sub.swift#type:Sub";
            Write("Sources/Ext.swift", "extension Square {\n    func doubled() {}\n}\n");
            Write("Sources/Sub.swift", "class Sub: Base {}\n");
            await IndexAsync();
            (await RelationsAsync(builder.ConnectionString)).ShouldBe(
            [
                ("swift-external:Square", member, "DEFINES_METHOD"),
                (sub, "swift-external:Base", "INHERITS")
            ], ignoreOrder: true);

            // Square and Base appear: the stub-bound edges re-bind and the stubs are removed.
            Write("Sources/Square.swift", "struct Square {}\n");
            Write("Sources/Base.swift", "protocol Base {}\n");
            await IndexAsync();
            (await RelationsAsync(builder.ConnectionString)).ShouldBe(
            [
                ($"{Project}:Sources/Square.swift#type:Square", member, "DEFINES_METHOD"),
                (sub, $"{Project}:Sources/Base.swift#type:Base", "IMPLEMENTS")
            ], ignoreOrder: true);
            (await StubsAsync(builder.ConnectionString)).ShouldBeEmpty();

            // A second Square: the member falls back to its file.
            Write("Sources/Other.swift", "struct Square {}\n");
            await IndexAsync();
            (await RelationsAsync(builder.ConnectionString)).ShouldBe(
            [
                ($"{Project}:Sources/Ext.swift", member, "DEFINES_METHOD"),
                (sub, $"{Project}:Sources/Base.swift#type:Base", "IMPLEMENTS")
            ], ignoreOrder: true);

            // Both Squares and Base go away: back to stubs, with the guessed superclass kind.
            File.Delete(Path.Combine(root, "Sources/Other.swift"));
            File.Delete(Path.Combine(root, "Sources/Square.swift"));
            File.Delete(Path.Combine(root, "Sources/Base.swift"));
            await IndexAsync("Sources/Other.swift", "Sources/Square.swift", "Sources/Base.swift");
            (await RelationsAsync(builder.ConnectionString)).ShouldBe(
            [
                ("swift-external:Square", member, "DEFINES_METHOD"),
                (sub, "swift-external:Base", "INHERITS")
            ], ignoreOrder: true);
            (await StubsAsync(builder.ConnectionString)).ShouldBe(
                ["swift-external:Base", "swift-external:Square"], ignoreOrder: true);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            await using var conn = new MySqlConnection(new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ConnectionString);
            await conn.OpenAsync();
            // Release this database's pooled connections so the suite stays under max_connections.
            await MySqlConnection.ClearPoolAsync(new MySqlConnection(builder.ConnectionString));
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{builder.Database}`");
        }
    }

    [Fact]
    public async Task IncrementalRun_PicksSwiftOverloadsByPersistedArgumentLabels_WhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"codegraph_swift_calls_{Guid.NewGuid():N}"
        };
        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        var runner = new MariaDbMigrationRunner(storageOptions, NullLogger<MariaDbMigrationRunner>.Instance);
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-swift-calls-mariadb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Sources"));

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
                await new IndexingPipeline(
                        store,
                        [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                        Options.Create(new IndexingOptions { MaxParallelFiles = 2 }),
                        new LocalFileSystem(),
                        NullLogger<IndexingPipeline>.Instance)
                    .IndexProjectAsync(Project, root, ct: CancellationToken.None);
            }

            void Write(string path, string source) => File.WriteAllText(Path.Combine(root, path), source);

            const string circle = $"{Project}:Sources/Circle.swift#type:Circle";
            const string caller = $"{Project}:Sources/Use.swift#function:use()";
            Write("Sources/Circle.swift", "struct Circle {\n    init() {}\n    init(radius: Double) {}\n}\n");
            Write("Sources/Use.swift", "func use() { _ = Circle(radius: 1) }\n");
            await IndexAsync();
            (await CallsAsync(builder.ConnectionString)).ShouldBe([(caller, $"{circle}#method:init(radius:Double)")]);

            // Only the caller changes: the overloads come from the store, labels included.
            Write("Sources/Use.swift", "func use() { _ = Circle() }\n");
            await IndexAsync();
            (await CallsAsync(builder.ConnectionString)).ShouldBe([(caller, $"{circle}#method:init()")]);

            // And back: init()'s persisted empty signature still rules it out for a labeled call.
            Write("Sources/Use.swift", "func use() { _ = Circle(radius: 1) }\n");
            await IndexAsync();
            (await CallsAsync(builder.ConnectionString)).ShouldBe([(caller, $"{circle}#method:init(radius:Double)")]);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            await using var conn = new MySqlConnection(new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ConnectionString);
            await conn.OpenAsync();
            await MySqlConnection.ClearPoolAsync(new MySqlConnection(builder.ConnectionString));
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{builder.Database}`");
        }
    }

    private static async Task<List<(string, string)>> CallsAsync(string connectionString)
    {
        await using var conn = new MySqlConnection(connectionString);
        var rows = await conn.QueryAsync<(string Source, string Target)>("""
            SELECT s.qualified_name AS Source, t.qualified_name AS Target
            FROM edges e
            JOIN nodes s ON s.id = e.source_id
            JOIN nodes t ON t.id = e.target_id
            WHERE e.project = @Project AND e.type = 'CALLS'
            """, new { Project });
        return rows.Select(row => (row.Source, row.Target)).ToList();
    }

    private static async Task<List<(string, string, string)>> RelationsAsync(string connectionString)
    {
        await using var conn = new MySqlConnection(connectionString);
        var rows = await conn.QueryAsync<(string Source, string Target, string Type)>("""
            SELECT s.qualified_name AS Source, t.qualified_name AS Target, e.type AS Type
            FROM edges e
            JOIN nodes s ON s.id = e.source_id
            JOIN nodes t ON t.id = e.target_id
            WHERE e.project = @Project
              AND (e.type IN ('INHERITS', 'IMPLEMENTS')
                   OR (e.type IN ('DEFINES', 'DEFINES_METHOD')
                       AND (t.qualified_name LIKE '%#extension:%' OR s.qualified_name LIKE 'swift-external:%')))
            """, new { Project });
        return rows.Select(row => (row.Source, row.Target, row.Type)).ToList();
    }

    private static async Task<List<string>> StubsAsync(string connectionString)
    {
        await using var conn = new MySqlConnection(connectionString);
        return (await conn.QueryAsync<string>(
            "SELECT qualified_name FROM nodes WHERE project = @Project AND qualified_name LIKE 'swift-external:%'",
            new { Project })).ToList();
    }
}
