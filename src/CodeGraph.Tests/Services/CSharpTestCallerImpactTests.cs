using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Extractors.CSharp;
using CodeGraph.Models;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Data;
using CodeGraph.Tests.Extractors;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// Regression for dark-factory-sandbox: callers of a static helper that live in a separate
/// test repository must produce CALLS edges and count toward impact analysis, both with the
/// default syntax-only C# extraction and with trusted MSBuild solution analysis.
/// </summary>
public sealed class CSharpTestCallerImpactTests
{
    private const string Repository = "StaticCallers";
    private const string LibFile = "src/Lib/TextTools.cs";
    private const string TestFile = "tests/Lib.Tests/TextToolsTests.cs";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexRepositoryAsync_TestProjectCallersOfStaticMethod_ProduceCallsEdges(bool trusted)
    {
        var root = CopyFixture();
        try
        {
            var store = new InMemoryGraphStore();
            await CreatePipeline(store, root, trusted).IndexRepositoryAsync(
                Repository, root, repositoryToolingIdentity: Identity(root), ct: CancellationToken.None);

            var calls = CallsByName(store);
            calls.ShouldContain(("WordCount_counts_words", TestFile, "WordCount", LibFile));
            calls.ShouldContain(("WordCount_namespace_qualified", TestFile, "WordCount", LibFile));
            calls.ShouldContain(("DoubleWordCount", LibFile, "WordCount", LibFile));
            calls.ShouldNotContain(("Local_value_named_like_the_type", TestFile, "WordCount", LibFile),
                "a local named like the type is a value receiver, not the static type");
            if (trusted)
            {
                // The test project itself was loaded by MSBuildWorkspace (semantic symbols carry
                // their signature), not merely rescued by the per-file syntax fallback.
                store.Nodes
                    .Where(node => node.FilePath == TestFile && node.Label == NodeLabel.Method)
                    .ShouldAllBe(node => node.QualifiedName.EndsWith(")") && node.DotnetProject == "Lib.Tests");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnalyzeImpact_OverMariaDb_CountsTestProjectCallers_WhenConnectionIsConfigured()
    {
        var builder = new MySqlConnectionStringBuilder(MariaDbTestEnvironment.RequireConnectionString());
        var databaseName = $"cg_test_callers_{Guid.NewGuid():N}";
        builder.Database = databaseName;
        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        var runner = new MariaDbMigrationRunner(storageOptions, NullLogger<MariaDbMigrationRunner>.Instance);
        var root = CopyFixture();

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();
            var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(builder.ConnectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options;
            await using var context = new CodeGraphDbContext(dbOptions);
            var store = new MySqlGraphStore(
                context,
                storageOptions,
                NullLogger<MySqlGraphStore>.Instance,
                new MySqlAnalysisStore(context),
                new MySqlMetricsStore(context),
                new MySqlReviewStore(context),
                runner);

            // Default (untrusted) policy — what the hosted indexer runs for this repository.
            await CreatePipeline(store, root, trusted: false).IndexRepositoryAsync(
                Repository, root, repositoryToolingIdentity: Identity(root), ct: CancellationToken.None);

            var impact = new ImpactAnalysisService(store, NullLogger<ImpactAnalysisService>.Instance);
            var byName = await impact.AnalyzeImpactAsync("WordCount", Repository);
            byName.ShouldNotBeNull();
            var callers = byName.AffectedNodes.Where(node => node.EdgeType == nameof(EdgeType.CALLS)).ToList();
            callers.Select(node => node.Name).ShouldBe(
                ["WordCount_counts_words", "WordCount_namespace_qualified", "DoubleWordCount"],
                ignoreOrder: true);
            AssertTestCallersLabeled(callers);

            var byFile = await impact.AnalyzeFileImpactAsync(Repository, LibFile);
            byFile.ShouldNotBeNull();
            byFile.AffectedNodes.Select(node => node.Name)
                .ShouldContain("WordCount_counts_words");
            byFile.AffectedNodes.Select(node => node.Name)
                .ShouldContain("WordCount_namespace_qualified");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            var drop = new MySqlConnectionStringBuilder(builder.ConnectionString) { Database = "" };
            await using var conn = new MySqlConnection(drop.ConnectionString);
            await conn.OpenAsync();
            // Release pooled sessions (idle ones linger ~180s per database) before dropping.
            await MySqlConnection.ClearAllPoolsAsync();
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
        }
    }

    private static void AssertTestCallersLabeled(IReadOnlyList<AffectedNode> callers)
    {
        foreach (var caller in callers)
        {
            var labeledAsTest = caller.RiskFactors.Contains("Test code (caught by CI)");
            labeledAsTest.ShouldBe(caller.FilePath == TestFile,
                $"{caller.Name} test-code labeling should follow its file ({caller.FilePath})");
        }
    }

    private static IndexingPipeline CreatePipeline(IGraphStore store, string root, bool trusted) => new(
        store,
        [new RoslynExtractor()],
        Options.Create(new IndexingOptions { TrustedDotnetRepositories = trusted ? Identity(root) : "" }),
        new LocalFileSystem(),
        NullLogger<IndexingPipeline>.Instance,
        new SolutionAnalyzer(
            NullLogger<SolutionAnalyzer>.Instance,
            new LintResultCache(),
            new DiagnosticDetailCache()));

    private static string Identity(string root) => $"folder-path:{root}";

    private static List<(string Caller, string? CallerFile, string Callee, string? CalleeFile)> CallsByName(
        InMemoryGraphStore store)
    {
        var nodes = store.Nodes.ToDictionary(node => node.Id);
        return store.Edges
            .Where(edge => edge.Type == EdgeType.CALLS
                && nodes.ContainsKey(edge.SourceId)
                && nodes.ContainsKey(edge.TargetId))
            .Select(edge => (
                Caller: nodes[edge.SourceId].Name,
                CallerFile: (string?)nodes[edge.SourceId].FilePath,
                Callee: nodes[edge.TargetId].Name,
                CalleeFile: (string?)nodes[edge.TargetId].FilePath))
            .ToList();
    }

    private static string CopyFixture()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "StaticCallers");
        var destination = Path.Combine(Path.GetTempPath(), $"codegraph-static-callers-{Guid.NewGuid():N}");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return Path.GetFullPath(destination);
    }
}
