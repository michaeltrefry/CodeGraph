using Shouldly;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Tests.Services;

public class GraphBufferTests
{
    [Fact]
    public void FindByName_ReturnsMatchingNodes()
    {
        var buffer = new GraphBuffer();

        buffer.AddNode(new GraphNode
        {
            Project = "P", Label = NodeLabel.Class,
            Name = "OrderService", QualifiedName = "P.OrderService"
        });
        buffer.AddNode(new GraphNode
        {
            Project = "P", Label = NodeLabel.Class,
            Name = "OrderService", QualifiedName = "P.Other.OrderService"
        });
        buffer.AddNode(new GraphNode
        {
            Project = "P", Label = NodeLabel.Class,
            Name = "WalletService", QualifiedName = "P.WalletService"
        });

        var results = buffer.FindByName("OrderService");
        results.Count.ShouldBe(2);
        results.ShouldAllBe(n => n.Name == "OrderService");
    }

    [Fact]
    public void FindByName_ReturnsEmpty_WhenNoMatch()
    {
        var buffer = new GraphBuffer();
        buffer.AddNode(new GraphNode
        {
            Project = "P", Label = NodeLabel.Class,
            Name = "Foo", QualifiedName = "P.Foo"
        });

        var results = buffer.FindByName("Bar");
        results.ShouldBeEmpty();
    }

    [Fact]
    public void FindByQN_ReturnsCorrectNode()
    {
        var buffer = new GraphBuffer();
        buffer.AddNode(new GraphNode
        {
            Project = "P", Label = NodeLabel.Class,
            Name = "Foo", QualifiedName = "P.Foo"
        });

        buffer.FindByQN("P.Foo").ShouldNotBeNull();
        buffer.FindByQN("P.Bar").ShouldBeNull();
    }

    [Fact]
    public void AddNode_OverwritesByQN()
    {
        var buffer = new GraphBuffer();
        buffer.AddNode(new GraphNode
        {
            Project = "P", Label = NodeLabel.Class,
            Name = "Foo", QualifiedName = "P.Foo"
        });
        buffer.AddNode(new GraphNode
        {
            Project = "P", Label = NodeLabel.Interface,
            Name = "Foo", QualifiedName = "P.Foo"
        });

        buffer.AllNodes.Count.ShouldBe(1);
        buffer.FindByQN("P.Foo")!.Label.ShouldBe(NodeLabel.Interface);
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var buffer = new GraphBuffer();
        buffer.AddNode(new GraphNode
        {
            Project = "P", Label = NodeLabel.Class,
            Name = "Foo", QualifiedName = "P.Foo"
        });
        buffer.AddEdge(new PendingEdge("P.Foo", "P.Bar", EdgeType.CALLS));
        buffer.AddFileHash("file.cs", "abc123");

        buffer.Clear();

        buffer.AllNodes.ShouldBeEmpty();
        buffer.AllPendingEdges.ShouldBeEmpty();
        buffer.AllFileHashes.ShouldBeEmpty();
        buffer.FindByName("Foo").ShouldBeEmpty();
    }

    [Fact]
    public void ResolveEdges_MapsQNsToIds()
    {
        var buffer = new GraphBuffer();
        buffer.AddNode(new GraphNode
        {
            Project = "P", Label = NodeLabel.Class,
            Name = "Src", QualifiedName = "P.Src"
        });
        buffer.AddNode(new GraphNode
        {
            Project = "P", Label = NodeLabel.Class,
            Name = "Tgt", QualifiedName = "P.Tgt"
        });
        buffer.AddEdge(new PendingEdge("P.Src", "P.Tgt", EdgeType.CALLS));
        buffer.AddEdge(new PendingEdge("P.Src", "P.Missing", EdgeType.CALLS));

        var qnToId = new Dictionary<string, long>
        {
            [GraphNodeKey.Create("P", "P.Src")] = 1,
            [GraphNodeKey.Create("P", "P.Tgt")] = 2
        };

        var resolved = buffer.ResolveEdges("P", qnToId);

        resolved.Count.ShouldBe(1);
        resolved[0].SourceId.ShouldBe(1);
        resolved[0].TargetId.ShouldBe(2);
        resolved[0].Type.ShouldBe(EdgeType.CALLS);
    }

    [Fact]
    public void ResolveEdges_UsesProjectScopedQualifiedNames()
    {
        var buffer = new GraphBuffer();
        buffer.AddEdge(new PendingEdge("src/app/service.ts:Widget", "src/app/service.ts:Run", EdgeType.CALLS));

        var qnToId = new Dictionary<string, long>
        {
            [GraphNodeKey.Create("ProjectA", "src/app/service.ts:Widget")] = 10,
            [GraphNodeKey.Create("ProjectA", "src/app/service.ts:Run")] = 11,
            [GraphNodeKey.Create("ProjectB", "src/app/service.ts:Widget")] = 20,
            [GraphNodeKey.Create("ProjectB", "src/app/service.ts:Run")] = 21
        };

        var resolved = buffer.ResolveEdges("ProjectB", qnToId);

        resolved.Count.ShouldBe(1);
        resolved[0].SourceId.ShouldBe(20);
        resolved[0].TargetId.ShouldBe(21);
    }
}
