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

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required for the Neo4j integration test suite.");
}
