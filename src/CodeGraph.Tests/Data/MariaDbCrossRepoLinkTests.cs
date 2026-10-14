using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;
using Xunit.Abstractions;

namespace CodeGraph.Tests.Data;

[CollectionDefinition(nameof(MariaDbCrossRepoLinkTests), DisableParallelization = true)]
public sealed class MariaDbCrossRepoLinkCollection;

/// <summary>
/// sc-25698: once the inference repository reached ~130k nodes / ~900k edges, every other
/// repository's post-index cross-repo linking loaded all CALLS edges and all definition nodes
/// (with their JSON properties) of every project and the indexer hit OutOfMemoryException.
/// Runs without parallelization so the managed-heap measurement only sees the linker.
/// </summary>
[Collection(nameof(MariaDbCrossRepoLinkTests))]
public class MariaDbCrossRepoLinkTests(ITestOutputHelper output)
{
    private const string LargeProject = "inference";
    private const string SmallProject = "centralized_logging";
    private const string LinkedSymbol = "rust-analyzer cargo inference 0.1.0 mod/n1().";

    [Fact]
    public async Task LinkForProjectAsync_LinksEveryKindAndLeavesOtherPairsAlone()
    {
        await using var db = await LinkTestDatabase.CreateAsync();
        await db.SeedFleetAsync();
        await using var context = db.CreateContext();
        var store = db.CreateStore(context);

        await new CrossRepoLinker(store, NullLogger<CrossRepoLinker>.Instance)
            .LinkForProjectAsync(CrossRepoLinkFleet.Consumer, CancellationToken.None);

        var qualifiedNames = await db.QualifiedNamesAsync();
        var edges = await store.GetAllCrossRepoEdgesAsync();
        CrossRepoLinkFleet.ShouldMatch(edges, id => qualifiedNames[id], CrossRepoLinkFleet.ConsumerLinks);
        var cargo = edges.Single(edge => edge.Properties.GetValueOrDefault("matched_by")?.ToString() == "package_key");
        cargo.Properties["local_name"].ToString().ShouldBe("provider_alias");
        cargo.Properties["version"].ToString().ShouldBe("1.5.0");
        // The git dependency resolves through Provider's repository URL, not a shared package key.
        var git = edges.Single(edge => edge.Properties.GetValueOrDefault("matched_by")?.ToString() == "git_url" &&
                                       edge.Properties.GetValueOrDefault("ecosystem")?.ToString() == "cargo");
        qualifiedNames[git.TargetNodeId].ShouldBe("package:git-lib-definition");
        git.Properties["package_key"].ToString().ShouldStartWith("cargo:git:");
        // The Swift package dependency resolves through the same URL to Provider's root package.
        var swift = edges.Single(edge => edge.Properties.GetValueOrDefault("ecosystem")?.ToString() == "swiftpm");
        qualifiedNames[swift.TargetNodeId].ShouldBe("package:swift-provider-definition");
        swift.Properties["matched_by"].ToString().ShouldBe("git_url");
        swift.Properties["identity"].ToString().ShouldBe("provider-repo");
        swift.Properties["version"].ToString().ShouldBe("2.0.0");
    }

    [Fact]
    public async Task LinkAsync_LinksEveryProjectPair()
    {
        await using var db = await LinkTestDatabase.CreateAsync();
        await db.SeedFleetAsync();
        await using var context = db.CreateContext();
        var store = db.CreateStore(context);

        await new CrossRepoLinker(store, NullLogger<CrossRepoLinker>.Instance).LinkAsync(CancellationToken.None);

        var qualifiedNames = await db.QualifiedNamesAsync();
        CrossRepoLinkFleet.ShouldMatch(await store.GetAllCrossRepoEdgesAsync(), id => qualifiedNames[id],
            [.. CrossRepoLinkFleet.ConsumerLinks, CrossRepoLinkFleet.OtherLink]);
    }

    [Fact]
    public async Task StreamLinkEdgesAsync_FiltersOnTheServerAndProjectsOnlyRequestedKeys()
    {
        await using var db = await LinkTestDatabase.CreateAsync();
        await db.SeedFleetAsync();
        await using var context = db.CreateContext();
        var store = db.CreateStore(context);

        var rows = new List<LinkEdgeRow>();
        await foreach (var row in store.StreamLinkEdgesAsync(new LinkEdgeQuery([EdgeType.CALLS, EdgeType.USES_TYPE])
                       {
                           TargetLabels = [NodeLabel.ExternalSymbol],
                           TargetPropertyEquals = new Dictionary<string, string>
                           {
                               ["SOURCE"] = "SCIP",
                               ["scip_external"] = "true"
                           }.ToDictionary(pair => pair.Key.ToLowerInvariant(), pair => pair.Value),
                           EdgePropertyKeys = ["line", "missing"],
                           TargetPropertyKeys = ["scip_symbol"]
                       }))
        {
            rows.Add(row);
        }

        rows.Select(row => (row.SourceProject, row.TargetProject, row.TargetLabel, row.Type))
            .OrderBy(row => row.SourceProject)
            .ShouldBe([
                (CrossRepoLinkFleet.Consumer, CrossRepoLinkFleet.Consumer, (NodeLabel?)NodeLabel.ExternalSymbol, EdgeType.CALLS),
                (CrossRepoLinkFleet.Other, CrossRepoLinkFleet.Other, (NodeLabel?)NodeLabel.ExternalSymbol, EdgeType.USES_TYPE)
            ]);
        var consumer = rows.Single(row => row.SourceProject == CrossRepoLinkFleet.Consumer);
        consumer.EdgeProperties["line"].ShouldBe("12");
        consumer.EdgeProperties["missing"].ShouldBeNull();
        consumer.TargetProperties.Keys.ShouldBe(["scip_symbol"]);
        consumer.TargetProperties["scip_symbol"].ShouldBe("rust-analyzer cargo provider 1.0.0 provider/run().");

        var definitions = new List<LinkNodeRow>();
        await foreach (var node in store.StreamLinkNodesAsync(new LinkNodeQuery([NodeLabel.Package])
                       {
                           PropertyEquals = new Dictionary<string, string> { ["ecosystem"] = "cargo", ["is_definition"] = "true" },
                           PropertyKeys = ["package_key"]
                       }))
        {
            definitions.Add(node);
        }

        // JSON true and the string "True" both count; the non-definition dependency does not.
        definitions.Select(node => node.QualifiedName).Order(StringComparer.Ordinal)
            .ShouldBe(["package:consumer", "package:git-lib-definition", "package:provider-lib-definition"]);
    }

    /// <summary>
    /// Post-index analysis submission and vitals read lightweight projections instead of every
    /// node/edge row (with JSON properties) of the repository.
    /// </summary>
    [Fact]
    public async Task AnalysisProjections_MatchTheFullRowQueriesWithoutProperties()
    {
        await using var db = await LinkTestDatabase.CreateAsync();
        await db.SeedFleetAsync();
        await db.ExecuteAsync("""
            INSERT INTO edges (project, source_id, target_id, type, properties)
            SELECT 'Provider', p.id, c.id, 'CALLS', '{"line":7}'
            FROM nodes p JOIN nodes c ON c.qualified_name = 'Consumer:caller'
            WHERE p.qualified_name = 'Provider:run'
            """);
        await db.ExecuteAsync("""
            INSERT INTO nodes (project, label, name, qualified_name, file_path, properties)
            VALUES ('Consumer', 'File', 'lib.rs', 'Consumer:file:src/lib.rs', 'src/lib.rs', '{"documentation":"x"}')
            """);
        await db.ExecuteAsync("UPDATE nodes SET do_not_trust = TRUE WHERE qualified_name = 'Consumer:caller'");
        await using var context = db.CreateContext();
        var store = db.CreateStore(context);

        var consumerNodes = await store.GetAllNodesByProjectAsync(CrossRepoLinkFleet.Consumer);
        var expectedEdges = await store.GetEdgesForNodesAsync(consumerNodes.Select(node => node.Id).ToList());
        var edges = await store.GetEdgeEndpointsForProjectAsync(CrossRepoLinkFleet.Consumer);

        edges.Select(edge => (edge.Id, edge.Project, edge.SourceId, edge.TargetId, edge.Type))
            .ShouldBe(expectedEdges.Select(edge => (edge.Id, edge.Project, edge.SourceId, edge.TargetId, edge.Type)));
        edges.ShouldContain(edge => edge.Project == CrossRepoLinkFleet.Provider);
        edges.ShouldAllBe(edge => edge.Properties == null);

        var vitalsNodes = await store.GetFileAndUntrustedNodesAsync(CrossRepoLinkFleet.Consumer);
        vitalsNodes.Select(node => (node.QualifiedName, node.Label, node.DoNotTrust, node.FilePath))
            .ShouldBe([
                ("Consumer:caller", "Function", true, "src/lib.rs"),
                ("Consumer:file:src/lib.rs", "File", false, "src/lib.rs")
            ]);
        vitalsNodes.ShouldAllBe(node => node.Properties == null);
    }

    /// <summary>
    /// A synthetic inference-sized project (scaled by CODEGRAPH_LINKER_BENCHMARK_NODES, default 20k
    /// nodes with 7 edges each) and a small repository linked to it. Linking the small repository must
    /// not grow the managed heap with the size of the large one.
    /// </summary>
    [Fact]
    public async Task LinkForProjectAsync_SmallRepoNextToLargeProject_KeepsManagedHeapBounded()
    {
        var nodeCount = int.TryParse(
            Environment.GetEnvironmentVariable("CODEGRAPH_LINKER_BENCHMARK_NODES"), out var configured)
            ? configured
            : 20_000;

        await using var db = await LinkTestDatabase.CreateAsync();
        await db.SeedLargeProjectAsync(LargeProject, nodeCount, edgesPerNode: 7);
        await db.SeedSmallProjectAsync(SmallProject, LinkedSymbol);
        await using var context = db.CreateContext();
        var store = db.CreateStore(context);
        var linker = new CrossRepoLinker(store, NullLogger<CrossRepoLinker>.Instance);

        var peakGrowth = await MeasurePeakManagedGrowthAsync(
            () => linker.LinkForProjectAsync(SmallProject, CancellationToken.None));

        output.WriteLine(
            $"{nodeCount} nodes / {nodeCount * 7} edges: peak managed heap growth {peakGrowth / (1024.0 * 1024.0):F1} MiB");
        var edges = await store.GetAllCrossRepoEdgesAsync();
        edges.ShouldContain(edge => edge.SourceProject == SmallProject && edge.TargetProject == LargeProject);
        // Bounded by the small repository and the referenced definitions, not the large project:
        // reading the large project's CALLS edges and definition nodes needs several times this.
        peakGrowth.ShouldBeLessThan(48L * 1024 * 1024);
    }

    private static async Task<long> MeasurePeakManagedGrowthAsync(Func<Task> action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        long peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
                await Task.Delay(1);
            }
        });

        await action();
        peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
        await stop.CancelAsync();
        await sampler;
        return peak - baseline;
    }

    private sealed class LinkTestDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly string _migrationsPath =
            Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations");

        private LinkTestDatabase(string connectionString) => _connectionString = connectionString;

        public static async Task<LinkTestDatabase> CreateAsync()
        {
            var builder = new MySqlConnectionStringBuilder(MariaDbTestEnvironment.RequireConnectionString())
            {
                Database = $"cg_xrepo_link_{Guid.NewGuid():N}"
            };
            var database = new LinkTestDatabase(builder.ConnectionString);
            await new MariaDbMigrationRunner(
                    Options.Create(new MariaDbStorageOptions
                    {
                        ConnectionString = builder.ConnectionString,
                        MigrationsPath = database._migrationsPath
                    }),
                    NullLogger<MariaDbMigrationRunner>.Instance)
                .ApplyConfiguredMigrationsAsync();
            return database;
        }

        public CodeGraphDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(_connectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options);

        public MySqlGraphStore CreateStore(CodeGraphDbContext context)
        {
            var accessor = Options.Create(new MariaDbStorageOptions
            {
                ConnectionString = _connectionString,
                MigrationsPath = _migrationsPath
            });
            return new MySqlGraphStore(context, accessor, NullLogger<MySqlGraphStore>.Instance,
                new MySqlAnalysisStore(context), new MySqlMetricsStore(context), new MySqlReviewStore(context),
                new MariaDbMigrationRunner(accessor, NullLogger<MariaDbMigrationRunner>.Instance));
        }

        public async Task SeedFleetAsync()
        {
            await using var context = CreateContext();
            var store = CreateStore(context);
            foreach (var (project, (nodes, edges)) in CrossRepoLinkFleet.Build())
            {
                await store.ReplaceProjectGraphAsync(project, nodes, edges, new Dictionary<string, string>(),
                    new RepositoryEntity { Name = project, RepoUrl = CrossRepoLinkFleet.RepoUrl(project) }, null);
            }
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.ExecuteAsync(sql);
        }

        public async Task<Dictionary<long, string>> QualifiedNamesAsync()
        {
            await using var connection = new MySqlConnection(_connectionString);
            return (await connection.QueryAsync<(long Id, string QualifiedName)>(
                    "SELECT id, qualified_name FROM nodes"))
                .ToDictionary(row => row.Id, row => row.QualifiedName);
        }

        /// <summary>
        /// Rust-shaped project: 70% SCIP definitions and 10% SCIP external symbols, each node with
        /// documentation/signature text like the real importer, and <paramref name="edgesPerNode"/>
        /// CALLS/USES_TYPE edges per node carrying small JSON properties.
        /// </summary>
        public async Task SeedLargeProjectAsync(string project, int nodeCount, int edgesPerNode)
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection,
                "INSERT INTO repositories (name, created_at, updated_at) VALUES (@Project, NOW(), NOW())",
                new { Project = project });
            await ExecuteAsync(connection, $$"""
                INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line, properties)
                SELECT @Project,
                       CASE WHEN seq % 10 = 0 THEN 'ExternalSymbol' WHEN seq % 10 < 8 THEN 'Function' ELSE 'Struct' END,
                       CONCAT('n', seq),
                       CONCAT(@Project, ':n', seq),
                       CONCAT('src/f', seq % 2000, '.rs'),
                       seq % 500, seq % 500 + 20,
                       CASE WHEN seq % 10 = 0
                            THEN CONCAT('{"source":"scip","scip_external":true,"confidence":"high","scip_symbol":"rust-analyzer cargo std 1.0.0 std/sym', seq % 3000, '().","documentation":"', REPEAT('d', 400), '","signature":"', REPEAT('s', 120), '"}')
                            ELSE CONCAT('{"source":"scip","confidence":"high","scip_symbol":"rust-analyzer cargo {{project}} 0.1.0 mod/n', seq, '().","documentation":"', REPEAT('d', 400), '","signature":"', REPEAT('s', 120), '"}')
                       END
                FROM seq_1_to_{{nodeCount}}
                """, new { Project = project });
            var firstId = await connection.ExecuteScalarAsync<long>(
                "SELECT MIN(id) FROM nodes WHERE project = @Project", new { Project = project });
            for (var k = 1; k <= edgesPerNode; k++)
            {
                await ExecuteAsync(connection, $$"""
                    INSERT INTO edges (project, source_id, target_id, type, properties)
                    SELECT @Project,
                           @FirstId + seq - 1,
                           @FirstId + MOD(seq * 7 + {{k * 13}}, {{nodeCount}}),
                           '{{(k <= 5 ? "CALLS" : "USES_TYPE")}}',
                           CONCAT('{"source":"scip","confidence":"high","line":', seq % 500, ',"file":"src/f', seq % 2000, '.rs"}')
                    FROM seq_1_to_{{nodeCount}}
                    """, new { Project = project, FirstId = firstId });
            }
        }

        public async Task SeedSmallProjectAsync(string project, string referencedSymbol)
        {
            var nodes = new List<GraphNode>
            {
                new()
                {
                    Project = project, Label = NodeLabel.Function, Name = "ship", QualifiedName = $"{project}:ship",
                    Properties = new() { ["source"] = "scip", ["scip_symbol"] = $"rust-analyzer cargo {project} 0.1.0 ship()." }
                },
                new()
                {
                    Project = project, Label = NodeLabel.ExternalSymbol, Name = "n1", QualifiedName = $"{project}:external:n1",
                    Properties = new() { ["source"] = "scip", ["scip_external"] = true, ["scip_symbol"] = referencedSymbol }
                }
            };
            await using var context = CreateContext();
            await CreateStore(context).ReplaceProjectGraphAsync(project, nodes,
                [new PendingEdge($"{project}:ship", $"{project}:external:n1", EdgeType.CALLS)],
                new Dictionary<string, string>(), new RepositoryEntity { Name = project }, null);
        }

        private static Task ExecuteAsync(MySqlConnection connection, string sql, object parameters) =>
            connection.ExecuteAsync(new CommandDefinition(sql, parameters, commandTimeout: 1800));

        public async ValueTask DisposeAsync()
        {
            var builder = new MySqlConnectionStringBuilder(_connectionString);
            var database = builder.Database;
            builder.Database = "";
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.ExecuteAsync(new CommandDefinition($"DROP DATABASE IF EXISTS `{database}`", commandTimeout: 300));
        }
    }
}
