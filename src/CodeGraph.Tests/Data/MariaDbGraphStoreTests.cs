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
    public async Task MySqlGraphStore_RoundTripsCoreGraphDataWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

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
                    Project = "Dependency",
                    Label = NodeLabel.Class,
                    Name = "Dependency",
                    QualifiedName = "Dependency.Root",
                    FilePath = "Dependency.cs"
                }
            ]);

            var classId = nodeIds["CodeGraph.Widget"];
            var methodId = nodeIds["CodeGraph.Widget.Run"];
            var dependencyId = nodeIds["Dependency.Root"];

            (await store.FindNodeByIdAsync(classId))!.Name.ShouldBe("Widget");
            (await store.FindNodeByQualifiedNameAsync("CodeGraph", "CodeGraph.Widget.Run"))!.Label.ShouldBe(NodeLabel.Method);
            (await store.FindNodesByNameAsync("CodeGraph", "Widget")).Single().Id.ShouldBe(classId);
            (await store.FindNodesByLabelAsync("CodeGraph", NodeLabel.Method)).Single().Id.ShouldBe(methodId);
            (await store.FindNodesByFileAsync("CodeGraph", "Widget.cs")).Count.ShouldBe(2);
            (await store.SearchNodesAsync("CodeGraph", "Wid")).Single().Id.ShouldBe(classId);
            (await store.SearchNodesCountAsync("CodeGraph", "Wid")).ShouldBe(1);
            (await store.FindAllNodesByLabelAsync(NodeLabel.Class)).Count.ShouldBe(2);
            (await store.GetNodeCountsByLabelAsync())[NodeLabel.Class].ShouldBe(2);
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
}
