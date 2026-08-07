using System.Diagnostics;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class MariaDbGraphStoreTests
{
    [Fact]
    public void MySqlGraphStore_ImplementsStandaloneGraphContract()
    {
        typeof(IGraphStore).IsAssignableFrom(typeof(MySqlGraphStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task SearchSchemaRepositoriesAsync_ExecutesFourStatementsAtBoth32And512Schemas()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_schema_scale_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        var runner = new MariaDbMigrationRunner(
            storageOptions,
            NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(
                    builder.ConnectionString,
                    ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
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
            var server = $"scale-{Guid.NewGuid():N}";

            await SeedSchemaRangeAsync(builder.ConnectionString, server, 0, 31);
            var small = await CountSchemaStatementsAsync(store, databaseName, server);

            await SeedSchemaRangeAsync(builder.ConnectionString, server, 32, 511);
            var large = await CountSchemaStatementsAsync(store, databaseName, server);

            small.Result.TotalCount.ShouldBe(32);
            small.Result.TotalTables.ShouldBe(32);
            large.Result.TotalCount.ShouldBe(512);
            large.Result.TotalTables.ShouldBe(512);
            small.StatementCount.ShouldBe(4);
            large.StatementCount.ShouldBe(small.StatementCount);
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task MySqlGraphStore_RoundTripsCoreGraphDataWhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_graph_store_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });

        var runner = new MariaDbMigrationRunner(
            storageOptions,
            NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
                .UseMySql(
                    builder.ConnectionString,
                    ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                .Options;

            await using var context = new CodeGraphDbContext(dbOptions);
            var analysisStore = new MySqlAnalysisStore(context);
            var metricsStore = new MySqlMetricsStore(context);
            var reviewStore = new MySqlReviewStore(context);
            var store = new MySqlGraphStore(
                context,
                storageOptions,
                NullLogger<MySqlGraphStore>.Instance,
                analysisStore,
                metricsStore,
                reviewStore,
                runner);

            await store.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = "CodeGraph",
                RepoUrl = "https://example.test/codegraph",
                SourceGroup = "platform",
                Language = "C#"
            });
            await store.UpsertRepositoryAsync(new RepositoryEntity { Name = "Dependency" });
            await store.UpdateRepositoryCommitShaAsync("CodeGraph", "abc123");

            (await store.ListRepositoriesAsync()).Select(r => r.Name).ShouldContain("CodeGraph");
            (await store.SearchRepositoriesAsync("Code", "platform")).TotalCount.ShouldBe(1);
            (await store.GetDistinctGroupsAsync()).ShouldContain("platform");
            (await store.GetRepositoryByName("CodeGraph"))!.LastCommitSha.ShouldBe("abc123");

            var nodeIds = await store.UpsertNodeBatchAsync(
            [
                new GraphNode
                {
                    Project = "CodeGraph",
                    Label = NodeLabel.Class,
                    Name = "Widget",
                    QualifiedName = "CodeGraph.Widget",
                    FilePath = "Widget.cs",
                    StartLine = 1,
                    EndLine = 20,
                    Properties = new Dictionary<string, object> { ["signature"] = "class Widget" }
                },
                new GraphNode
                {
                    Project = "CodeGraph",
                    Label = NodeLabel.Method,
                    Name = "Run",
                    QualifiedName = "CodeGraph.Widget.Run",
                    FilePath = "Widget.cs",
                    StartLine = 5,
                    EndLine = 10
                },
                new GraphNode
                {
                    Project = "CodeGraph",
                    Label = NodeLabel.Package,
                    Name = "serde",
                    QualifiedName = "cargo:serde",
                    FilePath = "Cargo.toml"
                },
                new GraphNode
                {
                    Project = "CodeGraph",
                    Label = NodeLabel.ExternalSymbol,
                    Name = "Serialize",
                    QualifiedName = "scip:rust-analyzer cargo serde Serialize",
                    FilePath = "src/lib.rs"
                },
                new GraphNode
                {
                    Project = "Dependency",
                    Label = NodeLabel.Class,
                    Name = "Dependency",
                    QualifiedName = "Dependency.Root",
                    FilePath = "Dependency.cs"
                }
            ]);

            var classId = nodeIds[GraphNodeKey.Create("CodeGraph", "CodeGraph.Widget")];
            var methodId = nodeIds[GraphNodeKey.Create("CodeGraph", "CodeGraph.Widget.Run")];
            var packageId = nodeIds[GraphNodeKey.Create("CodeGraph", "cargo:serde")];
            var externalSymbolId = nodeIds[GraphNodeKey.Create("CodeGraph", "scip:rust-analyzer cargo serde Serialize")];
            var dependencyId = nodeIds[GraphNodeKey.Create("Dependency", "Dependency.Root")];

            (await store.FindNodeByIdAsync(classId))!.Name.ShouldBe("Widget");
            (await store.FindNodeByQualifiedNameAsync("CodeGraph", "CodeGraph.Widget.Run"))!.Label.ShouldBe(NodeLabel.Method);
            (await store.FindNodesByNameAsync("CodeGraph", "Widget")).Single().Id.ShouldBe(classId);
            (await store.FindNodesByLabelAsync("CodeGraph", NodeLabel.Method)).Single().Id.ShouldBe(methodId);
            (await store.FindNodesByLabelAsync("CodeGraph", NodeLabel.Package)).Single().Id.ShouldBe(packageId);
            (await store.FindNodesByLabelAsync("CodeGraph", NodeLabel.ExternalSymbol)).Single().Id.ShouldBe(externalSymbolId);
            (await store.FindNodesByFileAsync("CodeGraph", "Widget.cs")).Count.ShouldBe(2);
            (await store.SearchNodesAsync("CodeGraph", "Wid")).Single().Id.ShouldBe(classId);
            (await store.SearchNodesCountAsync("CodeGraph", "Wid")).ShouldBe(1);
            (await store.FindAllNodesByLabelAsync(NodeLabel.Class)).Count.ShouldBe(2);
            (await store.GetNodeCountsByLabelAsync())[NodeLabel.Class].ShouldBe(2);
            (await store.GetNodeCountsByLabelAsync())[NodeLabel.Package].ShouldBe(1);
            (await store.GetNodeCountsByLabelAsync())[NodeLabel.ExternalSymbol].ShouldBe(1);
            (await store.GetNodeCountsByLabelForProjectAsync("CodeGraph"))["Class"].ShouldBe(1);
            (await store.FindNodesByIdBatchAsync([classId, methodId])).ShouldContainKey(methodId);

            await store.SetDoNotTrustAsync(methodId, true);
            (await store.FindNodeByIdAsync(methodId))!.DoNotTrust.ShouldBeTrue();

            await store.InsertEdgeBatchAsync(
            [
                new GraphEdge
                {
                    Project = "CodeGraph",
                    SourceId = classId,
                    TargetId = methodId,
                    Type = EdgeType.DEFINES,
                    Properties = new Dictionary<string, object> { ["confidence"] = 0.9 }
                },
                new GraphEdge
                {
                    Project = "CodeGraph",
                    SourceId = methodId,
                    TargetId = classId,
                    Type = EdgeType.CALLS
                }
            ]);

            (await store.FindEdgesBySourceAsync(classId, EdgeType.DEFINES)).Single().TargetId.ShouldBe(methodId);
            (await store.FindEdgesByTargetAsync(classId, EdgeType.CALLS)).Single().SourceId.ShouldBe(methodId);
            (await store.FindEdgesByTargetBatchAsync([classId], [EdgeType.CALLS])).Single().SourceId.ShouldBe(methodId);
            (await store.FindAllEdgesByTypeAsync(EdgeType.CALLS)).Single().TargetId.ShouldBe(classId);
            (await store.GetEdgeCountsByTypeAsync())[EdgeType.CALLS].ShouldBe(1);
            (await store.GetCallFanInAsync("CodeGraph", 1))[classId].ShouldBe(1);
            (await store.TraverseAsync(classId, TraceDirection.Outbound, 2))
                .ShouldContain(entry => entry.Node.Id == methodId && entry.Depth == 1);

            await store.InsertCrossRepoEdgeAsync(new CrossRepoEdge
            {
                SourceProject = "CodeGraph",
                TargetProject = "Dependency",
                SourceNodeId = methodId,
                TargetNodeId = dependencyId,
                Type = EdgeType.CALLS
            });
            (await store.FindCrossRepoEdgesAsync("CodeGraph")).Single().TargetProject.ShouldBe("Dependency");
            (await store.GetAllCrossRepoEdgesAsync()).Single().SourceProject.ShouldBe("CodeGraph");
            (await store.FindProjectsWithNoCrossRepoEdgesAsync()).ShouldBeEmpty();

            await store.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = "db:sql-prod/Orders",
                SourceGroup = "sql-prod",
                Properties = """{"serverName":"sql-prod","databaseName":"Orders"}"""
            });
            await store.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = "db:sql-prod/Billing",
                SourceGroup = "sql-prod"
            });
            await store.UpsertNodeBatchAsync(
            [
                new GraphNode
                {
                    Project = "db:sql-prod/Orders",
                    Label = NodeLabel.Table,
                    Name = "Customers",
                    QualifiedName = "dbo.Customers"
                },
                new GraphNode
                {
                    Project = "db:sql-prod/Orders",
                    Label = NodeLabel.View,
                    Name = "ActiveCustomers",
                    QualifiedName = "dbo.ActiveCustomers"
                }
            ]);

            var schemaPage = await store.SearchSchemaRepositoriesAsync(page: 2, pageSize: 1);
            schemaPage.TotalCount.ShouldBe(2);
            schemaPage.TotalTables.ShouldBe(1);
            schemaPage.TotalViews.ShouldBe(1);
            schemaPage.Items.Single().DatabaseName.ShouldBe("Orders");
            schemaPage.Items.Single().TableCount.ShouldBe(1);
            schemaPage.Servers.ShouldBe(["sql-prod"]);
            schemaPage.Databases.ShouldBe(["Billing", "Orders"]);

            var filteredSchemas = await store.SearchSchemaRepositoriesAsync(
                search: "order",
                server: "SQL-PROD",
                database: "ORDERS");
            filteredSchemas.TotalCount.ShouldBe(1);
            filteredSchemas.TotalTables.ShouldBe(1);
            filteredSchemas.Items.Single().Project.Name.ShouldBe("db:sql-prod/Orders");

            await store.UpsertFileHashBatchAsync("CodeGraph", new Dictionary<string, string>
            {
                ["Widget.cs"] = "hash"
            });
            (await store.GetFileHashesAsync("CodeGraph"))["Widget.cs"].ShouldBe("hash");
            await store.DeleteFileHashesAsync("CodeGraph", ["Widget.cs"]);
            (await store.GetFileHashesAsync("CodeGraph")).ShouldBeEmpty();

            await store.UpsertSyncStateAsync(new SyncStateEntity
            {
                Project = "CodeGraph",
                Status = "syncing",
                LastCommitSha = "abc123"
            });
            (await store.GetSyncStateAsync("CodeGraph"))!.Status.ShouldBe("syncing");
            (await store.GetSyncStatesAsync(["CodeGraph"])).ShouldContainKey("CodeGraph");
            await store.DeleteSyncStateAsync("CodeGraph");
            (await store.GetSyncStateAsync("CodeGraph")).ShouldBeNull();

            await store.ReplaceRepoClustersAsync(
            [
                new RepoCluster
                {
                    ProjectName = "CodeGraph",
                    ClusterId = 7,
                    ClusterLabel = "Core",
                    ModularityScore = 0.7m,
                    Level = 0,
                    BetweennessCentrality = 0.1m,
                    ComputedAt = DateTime.UtcNow
                }
            ]);
            (await store.GetRepoClustersAsync()).Single().ClusterLabel.ShouldBe("Core");
            (await store.GetRepoClusterMembersAsync(7)).Single().ProjectName.ShouldBe("CodeGraph");
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = ""
        };

        await using var conn = new MySqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
    }

    private static async Task SeedSchemaRangeAsync(
        string connectionString,
        string server,
        int first,
        int last)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($$"""
            INSERT INTO repositories (name, gitlab_group)
            SELECT CONCAT('db:', @Server, '/Database', LPAD(seq, 4, '0')), @Server
            FROM seq_{{first}}_to_{{last}};

            INSERT INTO nodes (project, label, name, qualified_name)
            SELECT name, 'Table', CONCAT('Table', RIGHT(name, 4)), CONCAT('dbo.Table', RIGHT(name, 4))
            FROM repositories
            WHERE gitlab_group = @Server
                AND name NOT IN (SELECT project FROM nodes WHERE label = 'Table');
            """, new { Server = server });
    }

    private static async Task<(SchemaRepositorySearchResult Result, int StatementCount)> CountSchemaStatementsAsync(
        MySqlGraphStore store,
        string databaseName,
        string server)
    {
        var statementCount = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "MySqlConnector",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                var activityDatabase = activity.GetTagItem("db.namespace")?.ToString()
                    ?? activity.GetTagItem("db.name")?.ToString();
                var statement = activity.GetTagItem("db.query.text")?.ToString()
                    ?? activity.GetTagItem("db.statement")?.ToString();
                if (activity.OperationName == "Execute"
                    && string.Equals(activityDatabase, databaseName, StringComparison.Ordinal)
                    && statement?.Contains("WITH schema_repositories", StringComparison.Ordinal) == true)
                {
                    Interlocked.Increment(ref statementCount);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        var result = await store.SearchSchemaRepositoriesAsync(server: server, page: 3, pageSize: 10);
        return (result, statementCount);
    }
}
