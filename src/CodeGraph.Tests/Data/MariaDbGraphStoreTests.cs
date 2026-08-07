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

    [Fact]
    public async Task MySqlGraphStore_AtomicallyReplacesChangedFileSliceWhenConnectionIsConfigured()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_file_slice_test_{Guid.NewGuid():N}";
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
                .UseMySql(builder.ConnectionString,
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

            await store.UpsertRepositoryAsync(new RepositoryEntity { Name = "SliceProvider" });
            var ids = await store.UpsertNodeBatchAsync(
            [
                Node(NodeLabel.Class, "OldController", "SliceProvider.OldController", "api.cs"),
                Node(NodeLabel.Method, "OldAction", "SliceProvider.OldController.OldAction", "api.cs"),
                Node(NodeLabel.Route, "/old", "route:GET:/old", "api.cs"),
                Node(NodeLabel.Method, "Caller", "SliceProvider.Caller", "caller.cs"),
                Node(NodeLabel.Method, "Target", "SliceProvider.Target", "target.cs")
            ]);
            var oldMethodId = ids[GraphNodeKey.Create("SliceProvider", "SliceProvider.OldController.OldAction")];
            var callerId = ids[GraphNodeKey.Create("SliceProvider", "SliceProvider.Caller")];
            var targetId = ids[GraphNodeKey.Create("SliceProvider", "SliceProvider.Target")];
            await store.InsertEdgeBatchAsync(
            [
                Edge(oldMethodId, targetId, EdgeType.CALLS),
                Edge(callerId, oldMethodId, EdgeType.CALLS)
            ]);
            await store.UpsertFileHashBatchAsync("SliceProvider", new Dictionary<string, string>
            {
                ["api.cs"] = "old-hash"
            });

            await store.ReplaceProjectFilesAsync(
                "SliceProvider",
                ["api.cs"],
                [
                    Node(NodeLabel.Class, "NewController", "SliceProvider.NewController", "api.cs"),
                    Node(NodeLabel.Method, "NewAction", "SliceProvider.NewController.NewAction", "api.cs"),
                    Node(NodeLabel.Route, "/new", "route:GET:/new", "api.cs")
                ],
                [
                    new PendingEdge("SliceProvider.NewController", "SliceProvider.NewController.NewAction", EdgeType.DEFINES_METHOD),
                    new PendingEdge("SliceProvider.NewController.NewAction", "route:GET:/new", EdgeType.HANDLES)
                ],
                new Dictionary<string, string> { ["api.cs"] = "new-hash" });

            (await store.FindNodeByQualifiedNameAsync("SliceProvider", "SliceProvider.OldController")).ShouldBeNull();
            (await store.FindNodeByQualifiedNameAsync("SliceProvider", "SliceProvider.OldController.OldAction")).ShouldBeNull();
            (await store.FindNodeByQualifiedNameAsync("SliceProvider", "route:GET:/old")).ShouldBeNull();
            (await store.FindNodeByQualifiedNameAsync("SliceProvider", "SliceProvider.NewController")).ShouldNotBeNull();
            (await store.FindNodeByQualifiedNameAsync("SliceProvider", "SliceProvider.NewController.NewAction")).ShouldNotBeNull();
            (await store.FindNodeByQualifiedNameAsync("SliceProvider", "route:GET:/new")).ShouldNotBeNull();
            (await store.FindNodeByIdAsync(callerId)).ShouldNotBeNull();
            (await store.FindNodeByIdAsync(targetId)).ShouldNotBeNull();
            (await store.FindEdgesBySourceAsync(callerId, EdgeType.CALLS)).ShouldBeEmpty();
            (await store.FindEdgesByTargetAsync(targetId, EdgeType.CALLS)).ShouldBeEmpty();
            (await store.GetFileHashesAsync("SliceProvider"))["api.cs"].ShouldBe("new-hash");

            var productionRoot = Path.Combine(Path.GetTempPath(), $"codegraph-production-slice-{Guid.NewGuid():N}");
            Directory.CreateDirectory(productionRoot);
            try
            {
                var (oldSlice, newSlice) = await ProductionFileSliceFixture.CreateAsync(
                    "SliceProvider", productionRoot);
                oldSlice.Nodes.Select(node => node.FilePath).Distinct().Order()
                    .ShouldBe(ProductionFileSliceFixture.FilePaths.Order());
                newSlice.Nodes.Select(node => node.FilePath).Distinct().Order()
                    .ShouldBe(ProductionFileSliceFixture.FilePaths.Order());

                await store.ReplaceProjectFilesAsync(
                    "SliceProvider",
                    ProductionFileSliceFixture.FilePaths,
                    oldSlice.Nodes,
                    oldSlice.Edges,
                    ProductionFileSliceFixture.FilePaths.ToDictionary(path => path, _ => "production-old"));
                var oldIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var oldNode in oldSlice.Nodes)
                    oldIds[oldNode.QualifiedName] =
                        (await store.FindNodeByQualifiedNameAsync("SliceProvider", oldNode.QualifiedName))!.Id;

                await store.ReplaceProjectFilesAsync(
                    "SliceProvider",
                    ProductionFileSliceFixture.FilePaths,
                    newSlice.Nodes,
                    newSlice.Edges,
                    ProductionFileSliceFixture.FilePaths.ToDictionary(path => path, _ => "production-new"));

                foreach (var oldNode in oldSlice.Nodes)
                    (await store.FindNodeByQualifiedNameAsync("SliceProvider", oldNode.QualifiedName)).ShouldBeNull();
                foreach (var oldId in oldIds.Values)
                {
                    (await store.FindNodeByIdAsync(oldId)).ShouldBeNull();
                    (await store.FindEdgesBySourceAsync(oldId)).ShouldBeEmpty();
                    (await store.FindEdgesByTargetAsync(oldId)).ShouldBeEmpty();
                }
                foreach (var newNode in newSlice.Nodes)
                    (await store.FindNodeByQualifiedNameAsync("SliceProvider", newNode.QualifiedName)).ShouldNotBeNull();
                (await store.GetFileHashesAsync("SliceProvider"))
                    .Where(hash => ProductionFileSliceFixture.FilePaths.Contains(hash.Key))
                    .ShouldAllBe(hash => hash.Value == "production-new");
            }
            finally
            {
                Directory.Delete(productionRoot, recursive: true);
            }

            var oversizedPath = new string('x', 501);
            await Should.ThrowAsync<MySqlException>(() => store.ReplaceProjectFilesAsync(
                "SliceProvider",
                ["api.cs"],
                [Node(NodeLabel.Class, "Broken", "SliceProvider.Broken", oversizedPath)],
                [],
                new Dictionary<string, string> { ["api.cs"] = "broken-hash" }));
            (await store.FindNodeByQualifiedNameAsync("SliceProvider", "SliceProvider.NewController")).ShouldNotBeNull();
            (await store.GetFileHashesAsync("SliceProvider"))["api.cs"].ShouldBe("new-hash");
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }

        static GraphNode Node(NodeLabel label, string name, string qn, string filePath) => new()
        {
            Project = "SliceProvider",
            Label = label,
            Name = name,
            QualifiedName = qn,
            FilePath = filePath
        };
        static GraphEdge Edge(long sourceId, long targetId, EdgeType type) => new()
        {
            Project = "SliceProvider",
            SourceId = sourceId,
            TargetId = targetId,
            Type = type
        };
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
