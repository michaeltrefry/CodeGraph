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
        var repository = $"SliceProvider_{Guid.NewGuid():N}";
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
            await store.UpsertRepositoryAsync(new RepositoryEntity { Name = repository });
            var ids = await store.UpsertNodeBatchAsync(
            [
                Node(NodeLabel.Class, "OldController", $"{repository}.OldController", "api.cs"),
                Node(NodeLabel.Method, "OldAction", $"{repository}.OldController.OldAction", "api.cs"),
                Node(NodeLabel.Route, "/old", $"{repository}:route:GET:/old", "api.cs"),
                Node(NodeLabel.Method, "Caller", $"{repository}.Caller", "caller.cs"),
                Node(NodeLabel.Method, "Target", $"{repository}.Target", "target.cs")
            ]);
            var oldMethodId = ids[GraphNodeKey.Create(repository, $"{repository}.OldController.OldAction")];
            var callerId = ids[GraphNodeKey.Create(repository, $"{repository}.Caller")];
            var targetId = ids[GraphNodeKey.Create(repository, $"{repository}.Target")];
            await store.InsertEdgeBatchAsync(
            [
                Edge(oldMethodId, targetId, EdgeType.CALLS),
                Edge(callerId, oldMethodId, EdgeType.CALLS)
            ]);
            await store.UpsertFileHashBatchAsync(repository, new Dictionary<string, string>
            {
                ["api.cs"] = "old-hash"
            });

            await store.ReplaceRepositoryFilesAsync(
                repository,
                ["api.cs"],
                [
                    Node(NodeLabel.Class, "NewController", $"{repository}.NewController", "api.cs"),
                    Node(NodeLabel.Method, "NewAction", $"{repository}.NewController.NewAction", "api.cs"),
                    Node(NodeLabel.Route, "/new", $"{repository}:route:GET:/new", "api.cs")
                ],
                [
                    new PendingEdge($"{repository}.NewController", $"{repository}.NewController.NewAction", EdgeType.DEFINES_METHOD),
                    new PendingEdge($"{repository}.NewController.NewAction", $"{repository}:route:GET:/new", EdgeType.HANDLES)
                ],
                new Dictionary<string, string> { ["api.cs"] = "new-hash" });

            (await store.FindNodeByQualifiedNameAsync(repository, $"{repository}.OldController")).ShouldBeNull();
            (await store.FindNodeByQualifiedNameAsync(repository, $"{repository}.OldController.OldAction")).ShouldBeNull();
            (await store.FindNodeByQualifiedNameAsync(repository, $"{repository}:route:GET:/old")).ShouldBeNull();
            (await store.FindNodeByQualifiedNameAsync(repository, $"{repository}.NewController")).ShouldNotBeNull();
            (await store.FindNodeByQualifiedNameAsync(repository, $"{repository}.NewController.NewAction")).ShouldNotBeNull();
            (await store.FindNodeByQualifiedNameAsync(repository, $"{repository}:route:GET:/new")).ShouldNotBeNull();
            (await store.FindNodeByIdAsync(callerId)).ShouldNotBeNull();
            (await store.FindNodeByIdAsync(targetId)).ShouldNotBeNull();
            (await store.FindEdgesBySourceAsync(callerId, EdgeType.CALLS)).ShouldBeEmpty();
            (await store.FindEdgesByTargetAsync(targetId, EdgeType.CALLS)).ShouldBeEmpty();
            (await store.GetFileHashesAsync(repository))["api.cs"].ShouldBe("new-hash");

            var productionRoot = Path.Combine(Path.GetTempPath(), $"codegraph-production-slice-{Guid.NewGuid():N}");
            Directory.CreateDirectory(productionRoot);
            try
            {
                var (oldSlice, newSlice) = await ProductionFileSliceFixture.CreateAsync(repository, productionRoot);
                oldSlice.Nodes.Select(node => node.FilePath).Distinct().Order()
                    .ShouldBe(ProductionFileSliceFixture.FilePaths.Order());
                newSlice.Nodes.Select(node => node.FilePath).Distinct().Order()
                    .ShouldBe(ProductionFileSliceFixture.FilePaths.Order());

                await store.ReplaceRepositoryFilesAsync(
                    repository,
                    ProductionFileSliceFixture.FilePaths,
                    oldSlice.Nodes,
                    oldSlice.Edges,
                    ProductionFileSliceFixture.FilePaths.ToDictionary(path => path, _ => "production-old"));
                var oldIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var oldNode in oldSlice.Nodes)
                    oldIds[oldNode.QualifiedName] =
                        (await store.FindNodeByQualifiedNameAsync(repository, oldNode.QualifiedName))!.Id;

                await store.ReplaceRepositoryFilesAsync(
                    repository,
                    ProductionFileSliceFixture.FilePaths,
                    newSlice.Nodes,
                    newSlice.Edges,
                    ProductionFileSliceFixture.FilePaths.ToDictionary(path => path, _ => "production-new"));

                foreach (var oldNode in oldSlice.Nodes)
                    (await store.FindNodeByQualifiedNameAsync(repository, oldNode.QualifiedName)).ShouldBeNull();
                foreach (var oldId in oldIds.Values)
                {
                    (await store.FindNodeByIdAsync(oldId)).ShouldBeNull();
                    (await store.FindEdgesBySourceAsync(oldId)).ShouldBeEmpty();
                    (await store.FindEdgesByTargetAsync(oldId)).ShouldBeEmpty();
                }
                foreach (var newNode in newSlice.Nodes)
                    (await store.FindNodeByQualifiedNameAsync(repository, newNode.QualifiedName)).ShouldNotBeNull();
                (await store.GetFileHashesAsync(repository))
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
                    WHERE edge.sourceRepository = $repository OR edge.targetRepository = $repository
                    DETACH DELETE edge
                    """, new { repository });
                await tx.RunAsync("MATCH (n:CodeNode {repository: $repository}) DETACH DELETE n", new { repository });
                await tx.RunAsync("MATCH (h:FileHash {repository: $repository}) DELETE h", new { repository });
                await tx.RunAsync("MATCH (r:RepositoryRecord {name: $repository}) DELETE r", new { repository });
            });
        }

        GraphNode Node(NodeLabel label, string name, string qn, string filePath) => new()
        {
            Repository = repository,
            Label = label,
            Name = name,
            QualifiedName = qn,
            FilePath = filePath
        };
        GraphEdge Edge(long sourceId, long targetId, EdgeType type) => new()
        {
            Repository = repository,
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
        var repositories = Enumerable.Range(0, 16).Select(i => $"IdRace{i}_{Guid.NewGuid():N}").ToList();

        try
        {
            // Both bulk id-allocation paths (batch upsert and file-slice replace) race each other.
            await Task.WhenAll(repositories.Select((repository, i) => i % 2 == 0
                ? (Task)store.UpsertNodeBatchAsync(Nodes(repository))
                : store.ReplaceRepositoryFilesAsync(
                    repository, ["f.cs"], Nodes(repository), [], new Dictionary<string, string> { ["f.cs"] = "1" })));

            var ids = new List<long>();
            foreach (var repository in repositories)
                foreach (var node in Nodes(repository))
                    ids.Add((await store.FindNodeByQualifiedNameAsync(repository, node.QualifiedName))!.Id);
            ids.Count.ShouldBe(repositories.Count * 5);
            ids.Distinct().Count().ShouldBe(ids.Count, "concurrent writers were handed overlapping appId ranges");
        }
        finally
        {
            foreach (var repository in repositories)
                await store.DeleteNodesByRepositoryAsync(repository);
        }

        static List<GraphNode> Nodes(string repository) => Enumerable.Range(0, 5).Select(i => new GraphNode
        {
            Repository = repository,
            Label = NodeLabel.Method,
            Name = $"M{i}",
            QualifiedName = $"{repository}.M{i}",
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
        var repository = $"Vitals_{Guid.NewGuid():N}";

        try
        {
            await store.UpsertSyncStateAsync(new SyncStateEntity { Repository = repository, LastCommitSha = "abc", Status = "idle" });
            (await store.GetSyncStateAsync(repository))!.VitalsVersion.ShouldBeNull();

            await store.SetVitalsVersionAsync(repository, 7);
            await store.UpsertSyncStateAsync(new SyncStateEntity { Repository = repository, LastCommitSha = "def", Status = "idle" });

            (await store.GetSyncStateAsync(repository))!.VitalsVersion.ShouldBe(7);
            (await store.GetSyncStatesAsync([repository]))[repository].VitalsVersion.ShouldBe(7);

            // A full graph replacement writes the sync state too and keeps the vitals record.
            (await store.RecordVitalsFailureAsync(repository, "def", 8)).ShouldBe(1);
            await store.ReplaceRepositoryGraphAsync(
                repository,
                [new GraphNode { Repository = repository, Label = NodeLabel.Class, Name = "B", QualifiedName = $"{repository}.B", FilePath = "b.cs" }],
                [],
                new Dictionary<string, string> { ["b.cs"] = "h2" },
                new RepositoryEntity { Name = repository },
                new SyncStateEntity { Repository = repository, LastCommitSha = "ghi", LastSyncAt = DateTime.UtcNow, Status = "idle" });
            var replaced = (await store.GetSyncStateAsync(repository))!;
            replaced.LastCommitSha.ShouldBe("ghi");
            replaced.VitalsVersion.ShouldBe(7);
            replaced.VitalsFailedCommitSha.ShouldBe("def");
            replaced.VitalsConsecutiveFailures.ShouldBe(1);

            // Failures count consecutively per commit and version; a success clears them.
            (await store.RecordVitalsFailureAsync(repository, "def", 8)).ShouldBe(2);
            (await store.RecordVitalsFailureAsync(repository, "def", 9)).ShouldBe(1);
            (await store.RecordVitalsFailureAsync(repository, "xyz", 9)).ShouldBe(1);
            (await store.RecordVitalsFailureAsync("Unknown_" + repository, "xyz", 9)).ShouldBe(0);
            await store.SetVitalsVersionAsync(repository, 9);
            var cleared = (await store.GetSyncStateAsync(repository))!;
            cleared.VitalsVersion.ShouldBe(9);
            cleared.VitalsConsecutiveFailures.ShouldBe(0);
            cleared.VitalsFailedCommitSha.ShouldBeNull();
            cleared.VitalsFailedVersion.ShouldBeNull();
        }
        finally
        {
            await store.DeleteSyncStateAsync(repository);
            await store.DeleteNodesByRepositoryAsync(repository);
            await store.DeleteRepositoryAsync(repository);
        }
    }

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required for the Neo4j integration test suite.");
}
