using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Query;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class ArchitectureReportTests
{
    [Fact]
    public async Task GetArchitectureAsync_CountsAndHotspotsCoverTheWholeGraphBeyondTenThousandNodes()
    {
        var store = new InMemoryGraphStore();
        await ArchitectureGraphFixture.SeedAsync(store, ArchitectureGraphFixture.LargeMethodCount);

        var report = await new GraphQueryEngine(store, NullLogger<GraphQueryEngine>.Instance)
            .GetArchitectureAsync(ArchitectureGraphFixture.Project);

        ArchitectureGraphFixture.AssertReport(report, ArchitectureGraphFixture.LargeMethodCount);
    }

    [Fact]
    public void Format_RendersEverySectionBounded()
    {
        var report = new ArchitectureReport(
            "Arch",
            Summary: null,
            Confidence: null,
            new Dictionary<NodeLabel, int> { [NodeLabel.Method] = 10_481, [NodeLabel.Struct] = 1_243 },
            [new HotspotInfo("hot", "Arch:hot", 5, "High fan-in method")],
            [
                new CrossRepoEdge { SourceProject = "Up", TargetProject = "Arch", Type = EdgeType.CALLS },
                new CrossRepoEdge { SourceProject = "Up", TargetProject = "Arch", Type = EdgeType.CALLS }
            ],
            [new CrossRepoEdge { SourceProject = "Arch", TargetProject = "Down", Type = EdgeType.REFERENCES_PACKAGE }],
            [
                new PackageDependencyInfo("serde", "cargo", "1.0", 2),
                new PackageDependencyInfo("Newtonsoft.Json", "nuget", "13.0.1", 1)
            ],
            PackageDependencyCount: 30,
            new Dictionary<string, int> { ["cargo"] = 29, ["nuget"] = 1 },
            [new SupertypeInfo("Codable", "swift-external:Codable", NodeLabel.Interface, 2, IsExternal: true)]);

        var text = ArchitectureReportFormatter.Format(report);

        text.ShouldContain("- Method: 10,481");
        text.ShouldContain("- Struct: 1,243");
        text.ShouldContain("**hot** (fan-in: 5)");
        text.ShouldContain("- **Codable** (Interface, external) — 2 implementer/subtypes");
        text.ShouldContain("### Package Dependencies (30 external; cargo: 29, nuget: 1)");
        text.ShouldContain("- serde 1.0 (cargo, referenced by 2)");
        text.ShouldContain("- Newtonsoft.Json 13.0.1 (nuget, referenced by 1)");
        text.ShouldContain("- … and 28 more");
        // Cross-repo edges are summarized per repository and type, not listed one line per edge.
        text.ShouldContain("- Up → this (CALLS, 2 edges)");
        text.ShouldContain("- this → Down (REFERENCES_PACKAGE, 1 edge)");
    }
}

/// <summary>
/// One project graph exercising every architecture aggregate, seeded identically into each store
/// implementation so their results can be compared.
/// </summary>
internal static class ArchitectureGraphFixture
{
    public const string Project = "Arch";
    public const string OtherProject = "ArchOther";
    public const int LargeMethodCount = 10_050;

    public static async Task SeedAsync(
        IGraphStore store,
        int fillerMethodCount,
        string project = Project,
        string otherProject = OtherProject)
    {
        GraphNode Node(
            NodeLabel label,
            string name,
            string qualifiedName,
            Dictionary<string, object>? properties = null,
            bool other = false) => new()
        {
            Project = other ? otherProject : project,
            Label = label,
            Name = name,
            QualifiedName = qualifiedName,
            FilePath = "src/lib.rs",
            Properties = properties ?? new()
        };

        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = project });
        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = otherProject });

        var nodes = new List<GraphNode>();
        for (var i = 0; i < fillerMethodCount; i++)
            nodes.Add(Node(NodeLabel.Method, $"m{i}", $"Arch:m{i}"));
        // Added after the filler, so a first-10,000-rows sample misses them.
        nodes.AddRange(
        [
            Node(NodeLabel.Function, "hot", "Arch:hot"),
            Node(NodeLabel.Method, "warm", "Arch:warm"),
            Node(NodeLabel.Method, "cold", "Arch:cold"),
            Node(NodeLabel.Class, "Widget", "Arch:Widget"),
            Node(NodeLabel.Struct, "S0", "Arch:S0"),
            Node(NodeLabel.Struct, "S1", "Arch:S1"),
            Node(NodeLabel.Struct, "S2", "Arch:S2"),
            Node(NodeLabel.Interface, "Shape", "Arch:Shape"),
            Node(NodeLabel.Interface, "Codable", "swift-external:Codable"),
            Node(NodeLabel.ExternalSymbol, "Serialize", "scip:rust-analyzer cargo serde Serialize",
                new() { ["scip_external"] = true }),
            Node(NodeLabel.Class, "Base", "Arch:Base"),
            Node(NodeLabel.Class, "Derived", "Arch:Derived"),
            Node(NodeLabel.DotnetProject, "Arch.Api", "Arch:project:Arch.Api"),
            Node(NodeLabel.Package, "arch-core", "cargo:arch-core@0.1.0",
                new() { ["ecosystem"] = "cargo", ["version"] = "0.1.0", ["is_definition"] = true }),
            Node(NodeLabel.Package, "arch-cli", "cargo:arch-cli@0.1.0",
                new() { ["ecosystem"] = "cargo", ["version"] = "0.1.0", ["is_definition"] = true }),
            Node(NodeLabel.Package, "serde", "cargo:serde@1.0",
                new() { ["ecosystem"] = "cargo", ["version"] = "1.0", ["is_definition"] = false }),
            Node(NodeLabel.Package, "swift-collections", "swiftpm:swift-collections@1.1.0",
                new() { ["ecosystem"] = "swiftpm", ["version"] = "1.1.0", ["is_definition"] = false }),
            Node(NodeLabel.NuGetPackage, "Newtonsoft.Json", "nuget:Newtonsoft.Json",
                new() { ["version"] = "13.0.1" }),
            Node(NodeLabel.Method, "busy", "ArchOther:busy", other: true)
        ]);
        var ids = await store.UpsertNodeBatchAsync(nodes);
        long Id(string qualifiedName, bool other = false) =>
            ids[GraphNodeKey.Create(other ? otherProject : project, qualifiedName)];

        var edges = new List<GraphEdge>();
        void Add(string source, string target, EdgeType type) =>
            edges.Add(new GraphEdge { Project = project, SourceId = Id(source), TargetId = Id(target), Type = type });

        for (var i = 0; i < 5; i++) Add($"Arch:m{i}", "Arch:hot", EdgeType.CALLS);
        for (var i = 0; i < 3; i++) Add($"Arch:m{i}", "Arch:warm", EdgeType.CALLS);
        for (var i = 0; i < 2; i++) Add($"Arch:m{i}", "Arch:cold", EdgeType.CALLS);
        // A heavily called class is not a method hotspot.
        for (var i = 0; i < 6; i++) Add($"Arch:m{i}", "Arch:Widget", EdgeType.CALLS);

        foreach (var s in new[] { "Arch:S0", "Arch:S1", "Arch:S2" })
            Add(s, "Arch:Shape", EdgeType.IMPLEMENTS);
        Add("Arch:S0", "swift-external:Codable", EdgeType.IMPLEMENTS);
        Add("Arch:S1", "swift-external:Codable", EdgeType.IMPLEMENTS);
        Add("Arch:S0", "scip:rust-analyzer cargo serde Serialize", EdgeType.IMPLEMENTS);
        Add("Arch:Derived", "Arch:Base", EdgeType.INHERITS);

        Add("cargo:arch-core@0.1.0", "cargo:serde@1.0", EdgeType.REFERENCES_PACKAGE);
        Add("cargo:arch-cli@0.1.0", "cargo:serde@1.0", EdgeType.REFERENCES_PACKAGE);
        // A workspace-internal dependency targets the in-repo definition: not external.
        Add("cargo:arch-cli@0.1.0", "cargo:arch-core@0.1.0", EdgeType.REFERENCES_PACKAGE);
        Add("Arch:project:Arch.Api", "nuget:Newtonsoft.Json", EdgeType.REFERENCES_PACKAGE);
        Add("cargo:arch-cli@0.1.0", "swiftpm:swift-collections@1.1.0", EdgeType.REFERENCES_PACKAGE);

        // Another project's edges never count toward this project's figures.
        for (var i = 0; i < 9; i++)
            edges.Add(new GraphEdge
            {
                Project = otherProject,
                SourceId = Id($"Arch:m{i}"),
                TargetId = Id("ArchOther:busy", other: true),
                Type = EdgeType.CALLS
            });

        await store.InsertEdgeBatchAsync(edges);
    }

    public static void AssertReport(ArchitectureReport report, int fillerMethodCount)
    {
        report.NodeCounts[NodeLabel.Method].ShouldBe(fillerMethodCount + 2);
        report.NodeCounts[NodeLabel.Function].ShouldBe(1);
        report.NodeCounts[NodeLabel.Struct].ShouldBe(3);
        report.NodeCounts[NodeLabel.Package].ShouldBe(4);
        report.NodeCounts[NodeLabel.NuGetPackage].ShouldBe(1);

        report.Hotspots.Select(h => (h.Name, h.FanIn)).ShouldBe([("hot", 5), ("warm", 3)]);

        report.TopSupertypes.Count.ShouldBe(4);
        report.TopSupertypes.Take(2).Select(t => (t.Name, t.SubtypeCount, t.IsExternal))
            .ShouldBe([("Shape", 3, false), ("Codable", 2, true)]);
        report.TopSupertypes.Skip(2).Select(t => (t.Name, t.SubtypeCount, t.IsExternal))
            .OrderBy(t => t.Name)
            .ShouldBe([("Base", 1, false), ("Serialize", 1, true)]);

        report.PackageDependencyCount.ShouldBe(3);
        report.PackageDependencyCountsByEcosystem.OrderBy(kv => kv.Key)
            .ShouldBe([
                new KeyValuePair<string, int>("cargo", 1),
                new KeyValuePair<string, int>("nuget", 1),
                new KeyValuePair<string, int>("swiftpm", 1)
            ]);
        report.PackageDependencies.Select(p => (p.Name, p.Ecosystem, p.Version, p.DependentCount))
            .ShouldBe([
                ("serde", "cargo", "1.0", 2),
                ("Newtonsoft.Json", "nuget", "13.0.1", 1),
                ("swift-collections", "swiftpm", "1.1.0", 1)
            ]);
    }
}
