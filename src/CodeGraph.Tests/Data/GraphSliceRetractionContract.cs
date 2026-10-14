using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Data.Neo4j;
using CodeGraph.Models;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

/// <summary>
/// The store contract behind incremental re-binding: batched node lookups, edges by property
/// value, and file-slice writes that retract edges and remove orphaned file-less nodes. Every
/// graph store must behave identically.
/// </summary>
public class GraphSliceRetractionContract
{
    private static GraphNode Node(string project, string qualifiedName, string name, string? file, NodeLabel label) => new()
    {
        Project = project,
        Label = label,
        Name = name,
        QualifiedName = qualifiedName,
        FilePath = file
    };

    public static async Task VerifyAsync(IGraphStore store, string project)
    {
        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = project });
        var type = $"{project}:a.swift#type:A";
        var member = $"{project}:b.swift#extension:S#method:m()";
        var other = $"{project}:b.swift#extension:T#method:n()";
        await store.ReplaceProjectFilesAsync(
            project,
            ["a.swift", "b.swift"],
            [
                Node(project, type, "A", "a.swift", NodeLabel.Struct),
                Node(project, member, "m", "b.swift", NodeLabel.Method),
                Node(project, other, "n", "b.swift", NodeLabel.Method),
                Node(project, "swift-external:S", "S", null, NodeLabel.Class),
                Node(project, "swift-external:T", "T", null, NodeLabel.Class)
            ],
            [
                new PendingEdge("swift-external:S", member, EdgeType.DEFINES_METHOD,
                    new() { ["swift_source_name"] = "S", ["origin_file"] = "b.swift" }),
                new PendingEdge("swift-external:T", other, EdgeType.DEFINES_METHOD,
                    new() { ["swift_source_name"] = "T", ["origin_file"] = "b.swift" }),
                new PendingEdge(type, member, EdgeType.IMPLEMENTS, new() { ["confidence"] = "high" })
            ],
            new Dictionary<string, string> { ["a.swift"] = "1", ["b.swift"] = "1" });

        (await store.FindNodesByFilesAsync(project, ["a.swift", "missing.swift"]))
            .Select(node => node.QualifiedName).ShouldBe([type]);
        (await store.FindNodesByNamesAsync(project, ["S", "m"]))
            .Select(node => node.QualifiedName).ShouldBe(["swift-external:S", member], ignoreOrder: true);

        var found = (await store.FindEdgesByPropertyAsync(project, "swift_source_name", ["S", "Z"]))
            .ShouldHaveSingleItem();
        found.SourceQN.ShouldBe("swift-external:S");
        found.SourceFilePath.ShouldBeNullOrEmpty();
        found.TargetQN.ShouldBe(member);
        found.TargetFilePath.ShouldBe("b.swift");
        found.Type.ShouldBe(EdgeType.DEFINES_METHOD);
        found.Properties["origin_file"].ToString().ShouldBe("b.swift");

        // The stub edge is retracted (and so not preserved into the replaced file a.swift's
        // slice), S is left orphaned and removed; T keeps its edge and stays.
        await store.ReplaceProjectFilesAsync(
            project,
            ["a.swift"],
            [Node(project, type, "A", "a.swift", NodeLabel.Struct)],
            [new PendingEdge(type, member, EdgeType.DEFINES_METHOD)],
            new Dictionary<string, string> { ["a.swift"] = "2" },
            retractions: new GraphSliceRetractions(
                [new PendingEdge("swift-external:S", member, EdgeType.DEFINES_METHOD)],
                ["swift-external:S", "swift-external:T"]));

        (await store.FindNodeByQualifiedNameAsync(project, "swift-external:S")).ShouldBeNull();
        (await store.FindNodeByQualifiedNameAsync(project, "swift-external:T")).ShouldNotBeNull();
        var memberNode = (await store.FindNodeByQualifiedNameAsync(project, member)).ShouldNotBeNull();
        var owners = await store.FindEdgesByTargetAsync(memberNode.Id);
        var ownerNodes = await store.FindNodesByIdBatchAsync(owners.Select(edge => edge.SourceId).ToList());
        owners.Select(edge => (ownerNodes[edge.SourceId].QualifiedName, edge.Type))
            .ShouldBe([(type, EdgeType.DEFINES_METHOD)]);
        (await store.FindEdgesByPropertyAsync(project, "swift_source_name", ["S"])).ShouldBeEmpty();
    }

    [Fact]
    public Task InMemoryStore_HonorsTheContract() =>
        VerifyAsync(new CodeGraph.Tests.Extractors.InMemoryGraphStore(), "SliceContract");

    [Fact]
    public async Task MariaDbStore_HonorsTheContract_WhenConnectionIsConfigured()
    {
        var builder = new MySqlConnectionStringBuilder(MariaDbTestEnvironment.RequireConnectionString())
        {
            Database = $"codegraph_slice_contract_{Guid.NewGuid():N}"
        };
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
                context, storageOptions, NullLogger<MySqlGraphStore>.Instance,
                new MySqlAnalysisStore(context), new MySqlMetricsStore(context),
                new MySqlReviewStore(context), runner);
            await VerifyAsync(store, "SliceContract");
        }
        finally
        {
            await using var conn = new MySqlConnection(
                new MySqlConnectionStringBuilder(builder.ConnectionString) { Database = "" }.ConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{builder.Database}`");
        }
    }

    [Fact]
    public async Task MariaDbStore_ReadsFileLessNodesWrittenBeforeMigration070_WhenConnectionIsConfigured()
    {
        var builder = new MySqlConnectionStringBuilder(MariaDbTestEnvironment.RequireConnectionString())
        {
            Database = $"codegraph_null_file_path_{Guid.NewGuid():N}"
        };
        var migrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations");
        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = migrationsPath
        });
        var runner = new MariaDbMigrationRunner(storageOptions, NullLogger<MariaDbMigrationRunner>.Instance);
        try
        {
            await runner.ApplyConfiguredMigrationsAsync();
            await using (var conn = new MySqlConnection(builder.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync("INSERT INTO repositories (name, created_at, updated_at) VALUES ('Legacy', NOW(), NOW())");
                await conn.ExecuteAsync("""
                    INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line)
                    VALUES ('Legacy', 'Class', 'Stub', 'Legacy.Stub', NULL, 0, 0)
                    """);
                await conn.ExecuteAsync(await File.ReadAllTextAsync(
                    Path.Combine(migrationsPath, "070_nodes_file_path_not_null.sql")));
            }

            var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(builder.ConnectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options;
            await using var context = new CodeGraphDbContext(dbOptions);
            var store = new MySqlGraphStore(
                context, storageOptions, NullLogger<MySqlGraphStore>.Instance,
                new MySqlAnalysisStore(context), new MySqlMetricsStore(context),
                new MySqlReviewStore(context), runner);
            (await store.FindNodesByNameAsync("Legacy", "Stub")).ShouldHaveSingleItem().FilePath.ShouldBe("");
        }
        finally
        {
            await using var conn = new MySqlConnection(
                new MySqlConnectionStringBuilder(builder.ConnectionString) { Database = "" }.ConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{builder.Database}`");
        }
    }

    [Fact]
    public async Task Neo4jStore_HonorsTheContract_WhenConnectionIsConfigured()
    {
        var options = Options.Create(new CodeGraphStorageOptions
        {
            Neo4jUri = Require("CODEGRAPH_NEO4J_TEST_URI"),
            Neo4jUsername = Require("CODEGRAPH_NEO4J_TEST_USERNAME"),
            Neo4jPassword = Require("CODEGRAPH_NEO4J_TEST_PASSWORD")
        });
        await using var sessions = new Neo4jSessionFactory(options);
        var store = new Neo4jGraphStore(sessions, options, NullLogger<Neo4jGraphStore>.Instance);
        var project = $"SliceContract_{Guid.NewGuid():N}";
        try
        {
            await VerifyAsync(store, project);
        }
        finally
        {
            await store.DeleteNodesByProjectAsync(project);
            await store.DeleteRepositoryAsync(project);
        }
    }

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is required for the Neo4j integration test suite.");
}
