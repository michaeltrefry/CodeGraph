using CodeGraph.Data.MariaDb;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
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
/// Swift import resolution and manifest removal against the MariaDB store: persisted import edges
/// read back by property and type, re-bound and retracted in the file-slice write, orphaned
/// external module stubs removed, and the package slice of a deleted Package.resolved emptied.
/// </summary>
public class MariaDbSwiftImportTests
{
    private const string Project = "SwiftImports";
    private const string Shape = $"{Project}:Sources/App/Shape.swift";

    private static string Manifest(bool withCollections) => $$"""
        let package = Package(
            name: "App",
            dependencies: [
                .package(url: "https://github.com/apple/swift-log.git", from: "1.5.0"),
                {{(withCollections ? ".package(url: \"https://github.com/apple/swift-collections\", from: \"1.0.0\")," : "")}}
            ],
            targets: [
                .target(name: "App", dependencies: [
                    .product(name: "Logging", package: "swift-log"),
                    {{(withCollections ? ".product(name: \"Collections\", package: \"swift-collections\")," : "")}}
                ]),
            ]
        )
        """;

    private static string Resolved(string version) => $$"""
        { "pins" : [ { "identity" : "sparkle", "kind" : "remoteSourceControl",
            "location" : "https://github.com/sparkle-project/Sparkle",
            "state" : { "revision" : "eef1a539a373c1f1a320624b1130fc5de7b2e100", "version" : "{{version}}" } } ],
          "version" : 2 }
        """;

    [Fact]
    public async Task IncrementalRuns_RebindImportsAndRemoveDeletedManifestSlices_WhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"codegraph_swift_imports_{Guid.NewGuid():N}"
        };
        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        var runner = new MariaDbMigrationRunner(storageOptions, NullLogger<MariaDbMigrationRunner>.Instance);
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-swift-imports-mariadb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Sources", "App"));

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
                        NullLogger<IndexingPipeline>.Instance,
                        swiftPackageManifestExtractor: new SwiftPackageManifestExtractor())
                    .IndexProjectAsync(Project, root, ct: CancellationToken.None);
            }

            void Write(string path, string source) => File.WriteAllText(Path.Combine(root, path), source);

            Write("Package.swift", Manifest(withCollections: false));
            Write("Package.resolved", Resolved("2.10.0"));
            Write("Sources/App/Shape.swift", "import Foundation\nimport Logging\nimport Sparkle\nimport Collections\n");
            await IndexAsync();

            const string log = "package:swiftpm:scm:github.com/apple/swift-log@upToNextMajor:1.5.0";
            (await ShapeImportsAsync(builder.ConnectionString)).ShouldBe(
            [
                ("swift-external-module:Foundation", "external"),
                (log, "product"),
                ("package:swiftpm:scm:github.com/sparkle-project/sparkle@2.10.0", "identity"),
                ("swift-external-module:Collections", "external")
            ], ignoreOrder: true);

            // Manifests change; Shape.swift does not.
            Write("Package.swift", Manifest(withCollections: true));
            Write("Package.resolved", Resolved("2.11.0"));
            await IndexAsync();

            (await ShapeImportsAsync(builder.ConnectionString)).ShouldBe(
            [
                ("swift-external-module:Foundation", "external"),
                (log, "product"),
                ("package:swiftpm:scm:github.com/sparkle-project/sparkle@2.11.0", "identity"),
                ("package:swiftpm:scm:github.com/apple/swift-collections@upToNextMajor:1.0.0", "product")
            ], ignoreOrder: true);
            (await NamesAsync(builder.ConnectionString, "qualified_name LIKE 'swift-external-module:%'"))
                .ShouldBe(["swift-external-module:Foundation"],
                    ignoreOrder: true);

            // The pin file is deleted: its package slice goes, and Sparkle becomes external.
            File.Delete(Path.Combine(root, "Package.resolved"));
            await IndexAsync();

            (await NamesAsync(builder.ConnectionString, "file_path = 'Package.resolved'")).ShouldBeEmpty();
            (await ShapeImportsAsync(builder.ConnectionString)).ShouldContain(("swift-external-module:Sparkle", "external"));
            (await ShapeImportsAsync(builder.ConnectionString)).Count.ShouldBe(4);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            await using var conn = new MySqlConnection(new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ConnectionString);
            await conn.OpenAsync();
            // Release this database's pooled connections so the suite stays under max_connections.
            await MySqlConnection.ClearAllPoolsAsync();
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{builder.Database}`");
        }
    }

    private static async Task<List<(string Target, string MatchedBy)>> ShapeImportsAsync(string connectionString)
    {
        await using var conn = new MySqlConnection(connectionString);
        var rows = await conn.QueryAsync<(string Target, string MatchedBy)>("""
            SELECT t.qualified_name AS Target,
                   JSON_UNQUOTE(JSON_EXTRACT(e.properties, '$.matched_by')) AS MatchedBy
            FROM edges e
            JOIN nodes s ON s.id = e.source_id
            JOIN nodes t ON t.id = e.target_id
            WHERE e.project = @Project AND e.type = 'IMPORTS' AND s.qualified_name = @Shape
            """, new { Project, Shape });
        return rows.ToList();
    }

    private static async Task<List<string>> NamesAsync(string connectionString, string predicate)
    {
        await using var conn = new MySqlConnection(connectionString);
        return (await conn.QueryAsync<string>(
            $"SELECT qualified_name FROM nodes WHERE project = @Project AND {predicate}", new { Project })).ToList();
    }
}
