using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Data.Neo4j;
using CodeGraph.Models;
using CodeGraph.Services.Query;
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
/// The architecture aggregates (<see cref="IGraphStore.GetTopInDegreeNodesAsync"/>, label counts)
/// give the same answers from the MariaDB and Neo4j stores as from the in-memory store.
/// </summary>
public class ArchitectureAggregateStoreTests
{
    [Fact]
    public async Task GetArchitectureAsync_MariaDb_AggregatesTheWholeGraph()
    {
        var builder = new MySqlConnectionStringBuilder(MariaDbTestEnvironment.RequireConnectionString());
        var databaseName = $"codegraph_architecture_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;
        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        var runner = new MariaDbMigrationRunner(storageOptions, NullLogger<MariaDbMigrationRunner>.Instance);

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

            await ArchitectureGraphFixture.SeedAsync(store, ArchitectureGraphFixture.LargeMethodCount);

            var report = await new GraphQueryEngine(store, NullLogger<GraphQueryEngine>.Instance)
                .GetArchitectureAsync(ArchitectureGraphFixture.Project);
            ArchitectureGraphFixture.AssertReport(report, ArchitectureGraphFixture.LargeMethodCount);

            await AssertTopInDegreeContractAsync(store, ArchitectureGraphFixture.Project);
        }
        finally
        {
            await using var conn = new MySqlConnection(new MySqlConnectionStringBuilder(builder.ConnectionString)
            {
                Database = ""
            }.ConnectionString);
            await conn.OpenAsync();
            await MySqlConnection.ClearAllPoolsAsync();
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
        }
    }

    [Fact]
    public async Task GetArchitectureAsync_Neo4j_AggregatesTheWholeGraph()
    {
        var options = Options.Create(new CodeGraphStorageOptions
        {
            Neo4jUri = Require("CODEGRAPH_NEO4J_TEST_URI"),
            Neo4jUsername = Require("CODEGRAPH_NEO4J_TEST_USERNAME"),
            Neo4jPassword = Require("CODEGRAPH_NEO4J_TEST_PASSWORD")
        });
        var project = $"Arch_{Guid.NewGuid():N}";
        var otherProject = $"ArchOther_{Guid.NewGuid():N}";
        await using var sessions = new Neo4jSessionFactory(options);
        var store = new Neo4jGraphStore(sessions, options, NullLogger<Neo4jGraphStore>.Instance);
        const int fillerMethodCount = 40;

        try
        {
            await ArchitectureGraphFixture.SeedAsync(store, fillerMethodCount, project, otherProject);

            var report = await new GraphQueryEngine(store, NullLogger<GraphQueryEngine>.Instance)
                .GetArchitectureAsync(project);
            ArchitectureGraphFixture.AssertReport(report, fillerMethodCount);

            await AssertTopInDegreeContractAsync(store, project);
        }
        finally
        {
            await using var session = sessions.GetSession();
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    "MATCH (n:CodeNode) WHERE n.project IN $projects DETACH DELETE n",
                    new { projects = new[] { project, otherProject } });
            });
        }
    }

    private static async Task AssertTopInDegreeContractAsync(IGraphStore store, string project)
    {
        // Label filter, minimum and limit are applied before rows are returned.
        (await store.GetTopInDegreeNodesAsync(project, [EdgeType.CALLS], [NodeLabel.Method, NodeLabel.Function], 3, 1))
            .Select(entry => (entry.Node.Name, entry.InDegree))
            .ShouldBe([("hot", 5)]);
        (await store.GetTopInDegreeNodesAsync(project, [EdgeType.CALLS], null, 1, 10))
            .Select(entry => (entry.Node.Name, entry.InDegree))
            .ShouldBe([("Widget", 6), ("hot", 5), ("warm", 3), ("cold", 2)]);
        (await store.GetTopInDegreeNodesAsync(project, [EdgeType.INHERITS], null, 1, 10))
            .Select(entry => entry.Node.Name)
            .ShouldBe(["Base"]);
        (await store.GetTopInDegreeNodesAsync(project, [], null, 1, 10)).ShouldBeEmpty();
    }

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required for the Neo4j integration test suite.");
}
