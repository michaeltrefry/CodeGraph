using CodeGraph.Data;
using CodeGraph.Data.Neo4j;
using CodeGraph.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class Neo4jGraphStoreIntegrationTests
{
    [Fact]
    public async Task Neo4jGraphStore_AtomicallyReplacesChangedFileSliceWhenConnectionIsConfigured()
    {
        var uri = Require("CODEGRAPH_NEO4J_TEST_URI");
        var username = Require("CODEGRAPH_NEO4J_TEST_USERNAME");
        var password = Require("CODEGRAPH_NEO4J_TEST_PASSWORD");
        var project = $"SliceProvider_{Guid.NewGuid():N}";
        var options = Options.Create(new CodeGraphStorageOptions
        {
            Neo4jUri = uri,
            Neo4jUsername = username,
            Neo4jPassword = password
        });
        await Neo4jTestSchema.EnsureAppliedAsync(options);
        await using var sessions = new Neo4jSessionFactory(options);
        var store = new Neo4jGraphStore(sessions, options, NullLogger<Neo4jGraphStore>.Instance);

        try
        {
            await store.UpsertRepositoryAsync(new RepositoryEntity { Name = project });
            var ids = await store.UpsertNodeBatchAsync(
            [
                Node(NodeLabel.Class, "OldController", $"{project}.OldController", "api.cs"),
                Node(NodeLabel.Method, "OldAction", $"{project}.OldController.OldAction", "api.cs"),
                Node(NodeLabel.Route, "/old", $"{project}:route:GET:/old", "api.cs"),
                Node(NodeLabel.Method, "Caller", $"{project}.Caller", "caller.cs"),
                Node(NodeLabel.Method, "Target", $"{project}.Target", "target.cs")
            ]);
            var oldMethodId = ids[GraphNodeKey.Create(project, $"{project}.OldController.OldAction")];
            var callerId = ids[GraphNodeKey.Create(project, $"{project}.Caller")];
            var targetId = ids[GraphNodeKey.Create(project, $"{project}.Target")];
            await store.InsertEdgeBatchAsync(
            [
                Edge(oldMethodId, targetId, EdgeType.CALLS),
                Edge(callerId, oldMethodId, EdgeType.CALLS)
            ]);
            await store.UpsertFileHashBatchAsync(project, new Dictionary<string, string>
            {
                ["api.cs"] = "old-hash"
            });

            await store.ReplaceProjectFilesAsync(
                project,
                ["api.cs"],
                [
                    Node(NodeLabel.Class, "NewController", $"{project}.NewController", "api.cs"),
                    Node(NodeLabel.Method, "NewAction", $"{project}.NewController.NewAction", "api.cs"),
                    Node(NodeLabel.Route, "/new", $"{project}:route:GET:/new", "api.cs")
                ],
                [
                    new PendingEdge($"{project}.NewController", $"{project}.NewController.NewAction", EdgeType.DEFINES_METHOD),
                    new PendingEdge($"{project}.NewController.NewAction", $"{project}:route:GET:/new", EdgeType.HANDLES)
                ],
                new Dictionary<string, string> { ["api.cs"] = "new-hash" });

            (await store.FindNodeByQualifiedNameAsync(project, $"{project}.OldController")).ShouldBeNull();
            (await store.FindNodeByQualifiedNameAsync(project, $"{project}.OldController.OldAction")).ShouldBeNull();
            (await store.FindNodeByQualifiedNameAsync(project, $"{project}:route:GET:/old")).ShouldBeNull();
            (await store.FindNodeByQualifiedNameAsync(project, $"{project}.NewController")).ShouldNotBeNull();
            (await store.FindNodeByQualifiedNameAsync(project, $"{project}.NewController.NewAction")).ShouldNotBeNull();
            (await store.FindNodeByQualifiedNameAsync(project, $"{project}:route:GET:/new")).ShouldNotBeNull();
            (await store.FindNodeByIdAsync(callerId)).ShouldNotBeNull();
            (await store.FindNodeByIdAsync(targetId)).ShouldNotBeNull();
            (await store.FindEdgesBySourceAsync(callerId, EdgeType.CALLS)).ShouldBeEmpty();
            (await store.FindEdgesByTargetAsync(targetId, EdgeType.CALLS)).ShouldBeEmpty();
            (await store.GetFileHashesAsync(project))["api.cs"].ShouldBe("new-hash");

            var productionRoot = Path.Combine(Path.GetTempPath(), $"codegraph-production-slice-{Guid.NewGuid():N}");
            Directory.CreateDirectory(productionRoot);
            try
            {
                var (oldSlice, newSlice) = await ProductionFileSliceFixture.CreateAsync(project, productionRoot);
                oldSlice.Nodes.Select(node => node.FilePath).Distinct().Order()
                    .ShouldBe(ProductionFileSliceFixture.FilePaths.Order());
                newSlice.Nodes.Select(node => node.FilePath).Distinct().Order()
                    .ShouldBe(ProductionFileSliceFixture.FilePaths.Order());

                await store.ReplaceProjectFilesAsync(
                    project,
                    ProductionFileSliceFixture.FilePaths,
                    oldSlice.Nodes,
                    oldSlice.Edges,
                    ProductionFileSliceFixture.FilePaths.ToDictionary(path => path, _ => "production-old"));
                var oldIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var oldNode in oldSlice.Nodes)
                    oldIds[oldNode.QualifiedName] =
                        (await store.FindNodeByQualifiedNameAsync(project, oldNode.QualifiedName))!.Id;

                await store.ReplaceProjectFilesAsync(
                    project,
                    ProductionFileSliceFixture.FilePaths,
                    newSlice.Nodes,
                    newSlice.Edges,
                    ProductionFileSliceFixture.FilePaths.ToDictionary(path => path, _ => "production-new"));

                foreach (var oldNode in oldSlice.Nodes)
                    (await store.FindNodeByQualifiedNameAsync(project, oldNode.QualifiedName)).ShouldBeNull();
                foreach (var oldId in oldIds.Values)
                {
                    (await store.FindNodeByIdAsync(oldId)).ShouldBeNull();
                    (await store.FindEdgesBySourceAsync(oldId)).ShouldBeEmpty();
                    (await store.FindEdgesByTargetAsync(oldId)).ShouldBeEmpty();
                }
                foreach (var newNode in newSlice.Nodes)
                    (await store.FindNodeByQualifiedNameAsync(project, newNode.QualifiedName)).ShouldNotBeNull();
                (await store.GetFileHashesAsync(project))
                    .Where(hash => ProductionFileSliceFixture.FilePaths.Contains(hash.Key))
                    .ShouldAllBe(hash => hash.Value == "production-new");
            }
            finally
            {
                Directory.Delete(productionRoot, recursive: true);
            }
        }
        finally
        {
            await using var session = sessions.GetSession();
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("""
                    MATCH (edge:CrossRepoEdge)
                    WHERE edge.sourceProject = $project OR edge.targetProject = $project
                    DETACH DELETE edge
                    """, new { project });
                await tx.RunAsync("MATCH (n:CodeNode {project: $project}) DETACH DELETE n", new { project });
                await tx.RunAsync("MATCH (h:FileHash {project: $project}) DELETE h", new { project });
                await tx.RunAsync("MATCH (r:RepositoryRecord {name: $project}) DELETE r", new { project });
            });
        }

        GraphNode Node(NodeLabel label, string name, string qn, string filePath) => new()
        {
            Project = project,
            Label = label,
            Name = name,
            QualifiedName = qn,
            FilePath = filePath
        };
        GraphEdge Edge(long sourceId, long targetId, EdgeType type) => new()
        {
            Project = project,
            SourceId = sourceId,
            TargetId = targetId,
            Type = type
        };
    }

    [Fact]
    public async Task Neo4jGraphStore_ConcurrentNodeWritesNeverShareAnAppIdWhenConnectionIsConfigured()
    {
        var options = Options.Create(new CodeGraphStorageOptions
        {
            Neo4jUri = Require("CODEGRAPH_NEO4J_TEST_URI"),
            Neo4jUsername = Require("CODEGRAPH_NEO4J_TEST_USERNAME"),
            Neo4jPassword = Require("CODEGRAPH_NEO4J_TEST_PASSWORD")
        });
        await Neo4jTestSchema.EnsureAppliedAsync(options);
        await using var sessions = new Neo4jSessionFactory(options);
        var store = new Neo4jGraphStore(sessions, options, NullLogger<Neo4jGraphStore>.Instance);
        var projects = Enumerable.Range(0, 16).Select(i => $"IdRace{i}_{Guid.NewGuid():N}").ToList();

        try
        {
            // Both bulk id-allocation paths (batch upsert and file-slice replace) race each other.
            await Task.WhenAll(projects.Select((project, i) => i % 2 == 0
                ? (Task)store.UpsertNodeBatchAsync(Nodes(project))
                : store.ReplaceProjectFilesAsync(
                    project, ["f.cs"], Nodes(project), [], new Dictionary<string, string> { ["f.cs"] = "1" })));

            var ids = new List<long>();
            foreach (var project in projects)
                foreach (var node in Nodes(project))
                    ids.Add((await store.FindNodeByQualifiedNameAsync(project, node.QualifiedName))!.Id);
            ids.Count.ShouldBe(projects.Count * 5);
            ids.Distinct().Count().ShouldBe(ids.Count, "concurrent writers were handed overlapping appId ranges");
        }
        finally
        {
            foreach (var project in projects)
                await store.DeleteNodesByProjectAsync(project);
        }

        static List<GraphNode> Nodes(string project) => Enumerable.Range(0, 5).Select(i => new GraphNode
        {
            Project = project,
            Label = NodeLabel.Method,
            Name = $"M{i}",
            QualifiedName = $"{project}.M{i}",
            FilePath = "f.cs"
        }).ToList();
    }

    /// <summary>sc-26138: the vitals version is written only by SetVitalsVersionAsync; upserts keep it.</summary>
    [Fact]
    public async Task Neo4jGraphStore_SyncStateKeepsVitalsVersionAcrossUpsertsWhenConnectionIsConfigured()
    {
        var options = Options.Create(new CodeGraphStorageOptions
        {
            Neo4jUri = Require("CODEGRAPH_NEO4J_TEST_URI"),
            Neo4jUsername = Require("CODEGRAPH_NEO4J_TEST_USERNAME"),
            Neo4jPassword = Require("CODEGRAPH_NEO4J_TEST_PASSWORD")
        });
        await Neo4jTestSchema.EnsureAppliedAsync(options);
        await using var sessions = new Neo4jSessionFactory(options);
        var store = new Neo4jGraphStore(sessions, options, NullLogger<Neo4jGraphStore>.Instance);
        var project = $"Vitals_{Guid.NewGuid():N}";

        try
        {
            await store.UpsertSyncStateAsync(new SyncStateEntity { Project = project, LastCommitSha = "abc", Status = "idle" });
            (await store.GetSyncStateAsync(project))!.VitalsVersion.ShouldBeNull();

            await store.SetVitalsVersionAsync(project, 7);
            await store.UpsertSyncStateAsync(new SyncStateEntity { Project = project, LastCommitSha = "def", Status = "idle" });

            (await store.GetSyncStateAsync(project))!.VitalsVersion.ShouldBe(7);
            (await store.GetSyncStatesAsync([project]))[project].VitalsVersion.ShouldBe(7);

            // A full graph replacement writes the sync state too and keeps the vitals record.
            (await store.RecordVitalsFailureAsync(project, "def", 8)).ShouldBe(1);
            await store.ReplaceProjectGraphAsync(
                project,
                [new GraphNode { Project = project, Label = NodeLabel.Class, Name = "B", QualifiedName = $"{project}.B", FilePath = "b.cs" }],
                [],
                new Dictionary<string, string> { ["b.cs"] = "h2" },
                new RepositoryEntity { Name = project },
                new SyncStateEntity { Project = project, LastCommitSha = "ghi", LastSyncAt = DateTime.UtcNow, Status = "idle" });
            var replaced = (await store.GetSyncStateAsync(project))!;
            replaced.LastCommitSha.ShouldBe("ghi");
            replaced.VitalsVersion.ShouldBe(7);
            replaced.VitalsFailedCommitSha.ShouldBe("def");
            replaced.VitalsConsecutiveFailures.ShouldBe(1);

            // Failures count consecutively per commit and version; a success clears them.
            (await store.RecordVitalsFailureAsync(project, "def", 8)).ShouldBe(2);
            (await store.RecordVitalsFailureAsync(project, "def", 9)).ShouldBe(1);
            (await store.RecordVitalsFailureAsync(project, "xyz", 9)).ShouldBe(1);
            (await store.RecordVitalsFailureAsync("Unknown_" + project, "xyz", 9)).ShouldBe(0);
            await store.SetVitalsVersionAsync(project, 9);
            var cleared = (await store.GetSyncStateAsync(project))!;
            cleared.VitalsVersion.ShouldBe(9);
            cleared.VitalsConsecutiveFailures.ShouldBe(0);
            cleared.VitalsFailedCommitSha.ShouldBeNull();
            cleared.VitalsFailedVersion.ShouldBeNull();
        }
        finally
        {
            await store.DeleteSyncStateAsync(project);
            await store.DeleteNodesByProjectAsync(project);
            await store.DeleteRepositoryAsync(project);
        }
    }

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required for the Neo4j integration test suite.");
}
