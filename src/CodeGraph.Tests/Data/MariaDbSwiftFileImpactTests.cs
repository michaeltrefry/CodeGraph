using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Overlays;
using CodeGraph.Services.Pipeline;
using CodeGraph.Services.Query;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

/// <summary>
/// sc-26136: analyze_impact_file on a tree-sitter indexed Swift file over the MariaDB store, whose
/// traversal (unlike the in-memory test double's) follows the CALLS edges into the file.
/// </summary>
public class MariaDbSwiftFileImpactTests
{
    private const string Repository = "SwiftImpact";
    private const string GreeterPath = "Sources/App/Greeter.swift";

    [Fact]
    public async Task AnalyzeImpactFile_OnASwiftFile_ReportsItsCallers_WhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"codegraph_swift_impact_{Guid.NewGuid():N}"
        };
        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        var runner = new MariaDbMigrationRunner(storageOptions, NullLogger<MariaDbMigrationRunner>.Instance);
        var root = Path.Combine(Path.GetTempPath(), $"codegraph-swift-impact-mariadb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Sources", "App"));

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();
            File.WriteAllText(Path.Combine(root, GreeterPath),
                "struct Greeter {\n    func greet(name: String) -> String {\n        return name\n    }\n}\n");
            File.WriteAllText(Path.Combine(root, "Sources/App/Main.swift"),
                "func run() {\n    let greeter = Greeter()\n    _ = greeter.greet(name: \"a\")\n}\n");

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
                    Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                    new LocalFileSystem(),
                    NullLogger<IndexingPipeline>.Instance)
                .IndexRepositoryAsync(Repository, root, ct: CancellationToken.None);
            var sha = new string('c', 40);
            await store.UpsertRepositoryAsync(new RepositoryEntity { Name = Repository, LocalPath = root, LastCommitSha = sha });
            await store.UpsertSyncStateAsync(new SyncStateEntity { Repository = Repository, LastCommitSha = sha });

            // The stored CALLS edge run -> greet.
            var run = (await store.FindNodesByNameAsync(Repository, "run")).ShouldHaveSingleItem();
            var greet = (await store.FindNodesByNameAsync(Repository, "greet")).ShouldHaveSingleItem();
            greet.FilePath.ShouldBe(GreeterPath);
            (await store.FindEdgesBySourceAsync(run.Id, EdgeType.CALLS)).Select(edge => edge.TargetId)
                .ShouldBe([greet.Id]);

            var impact = new ImpactAnalysisService(store, NullLogger<ImpactAnalysisService>.Instance);
            var report = (await impact.AnalyzeFileImpactAsync(Repository, GreeterPath)).ShouldNotBeNull();
            report.AffectedNodes.Select(node => node.Name).ShouldContain("run");

            var resolver = new CommitScopeResolver(
                store,
                Options.Create(new RepositorySourceOptions()),
                new MySqlBranchOverlayStore(context),
                new BranchOverlayGit(),
                Options.Create(new BranchOverlayOptions()));
            var mcp = new CodeGraphMcpServer(
                new GraphQueryEngine(store, NullLogger<GraphQueryEngine>.Instance),
                null!,
                store,
                null!,
                Options.Create(new RepositorySourceOptions()),
                null!,
                impact,
                commitScopes: resolver);
            var answer = (await mcp.AnalyzeImpactFile(Repository, GreeterPath)).StructuredContent!.Value;
            answer.GetProperty("found").GetBoolean().ShouldBeTrue();
            answer.GetProperty("totalAffected").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
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
}
