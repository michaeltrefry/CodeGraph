using Shouldly;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Tests.Extractors;

namespace TC.CodeGraphApi.Tests.Data;

public class InMemoryGraphStoreNewMethodsTests
{
    private readonly InMemoryGraphStore _store = new();

    [Fact]
    public async Task FindNodesByIdBatchAsync_ReturnsMatchingNodes()
    {
        var id1 = _store.AddNode(new GraphNode
        {
            Project = "P", Label = NodeLabel.Class,
            Name = "A", QualifiedName = "P.A"
        });
        var id2 = _store.AddNode(new GraphNode
        {
            Project = "P", Label = NodeLabel.Class,
            Name = "B", QualifiedName = "P.B"
        });
        var id3 = _store.AddNode(new GraphNode
        {
            Project = "P", Label = NodeLabel.Class,
            Name = "C", QualifiedName = "P.C"
        });

        var result = await _store.FindNodesByIdBatchAsync([id1, id3]);

        result.Count.ShouldBe(2);
        result[id1].Name.ShouldBe("A");
        result[id3].Name.ShouldBe("C");
    }

    [Fact]
    public async Task FindNodesByIdBatchAsync_EmptyInput_ReturnsEmpty()
    {
        var result = await _store.FindNodesByIdBatchAsync([]);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task FindNodesByIdBatchAsync_MissingIds_ReturnsOnlyFound()
    {
        var id1 = _store.AddNode(new GraphNode
        {
            Project = "P", Label = NodeLabel.Class,
            Name = "A", QualifiedName = "P.A"
        });

        var result = await _store.FindNodesByIdBatchAsync([id1, 999]);

        result.Count.ShouldBe(1);
        result.ShouldContainKey(id1);
    }

    [Fact]
    public async Task GetNodeCountsByDotnetProjectAsync_GroupsCorrectly()
    {
        _store.AddNode(new GraphNode
        {
            Project = "P", DotnetProject = "P.Services",
            Label = NodeLabel.Class, Name = "A", QualifiedName = "P.A"
        });
        _store.AddNode(new GraphNode
        {
            Project = "P", DotnetProject = "P.Services",
            Label = NodeLabel.Method, Name = "B", QualifiedName = "P.B"
        });
        _store.AddNode(new GraphNode
        {
            Project = "P", DotnetProject = "P.Models",
            Label = NodeLabel.Class, Name = "C", QualifiedName = "P.C"
        });
        // No dotnet_project — should be excluded
        _store.AddNode(new GraphNode
        {
            Project = "P", Label = NodeLabel.Class,
            Name = "D", QualifiedName = "P.D"
        });

        var result = await _store.GetNodeCountsByDotnetProjectAsync("P");

        result.Count.ShouldBe(2);
        result["P.Services"]["Class"].ShouldBe(1);
        result["P.Services"]["Method"].ShouldBe(1);
        result["P.Models"]["Class"].ShouldBe(1);
    }

    [Fact]
    public async Task SearchNodesAsync_DotnetProjectFilter_FiltersAtQueryLevel()
    {
        _store.AddNode(new GraphNode
        {
            Project = "P", DotnetProject = "P.Services",
            Label = NodeLabel.Class, Name = "A", QualifiedName = "P.A"
        });
        _store.AddNode(new GraphNode
        {
            Project = "P", DotnetProject = "P.Models",
            Label = NodeLabel.Class, Name = "B", QualifiedName = "P.B"
        });

        var result = await _store.SearchNodesAsync("P", "", dotnetProject: "P.Services");

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("A");
    }

    [Fact]
    public async Task SearchNodesAsync_WithoutDotnetProject_ReturnsAll()
    {
        _store.AddNode(new GraphNode
        {
            Project = "P", DotnetProject = "P.Services",
            Label = NodeLabel.Class, Name = "A", QualifiedName = "P.A"
        });
        _store.AddNode(new GraphNode
        {
            Project = "P", DotnetProject = "P.Models",
            Label = NodeLabel.Class, Name = "B", QualifiedName = "P.B"
        });

        var result = await _store.SearchNodesAsync("P", "");

        result.Count.ShouldBe(2);
    }
}
