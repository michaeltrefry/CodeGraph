using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Query;
using Microsoft.Extensions.Options;
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
            .GetArchitectureAsync(ArchitectureGraphFixture.Repository);

        ArchitectureGraphFixture.AssertReport(report, ArchitectureGraphFixture.LargeMethodCount);
    }

    /// <summary>sc-26138 (G3): get_architecture names the default-branch index commit it describes.</summary>
    [Fact]
    public async Task GetArchitectureTool_ReportsTheDefaultBranchIndexCommit()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var store = new InMemoryGraphStore();
        await ArchitectureGraphFixture.SeedAsync(store, fillerMethodCount: 10);
        await store.UpsertSyncStateAsync(new SyncStateEntity
        {
            Repository = ArchitectureGraphFixture.Repository, LastCommitSha = sha, Status = "idle"
        });
        var mcp = new CodeGraphMcpServer(
            new GraphQueryEngine(store, NullLogger<GraphQueryEngine>.Instance),
            null!,
            store,
            null!,
            Options.Create(new RepositorySourceOptions()),
            null!,
            null!);

        var answer = await mcp.GetArchitecture(ArchitectureGraphFixture.Repository);

        answer.Text().ShouldStartWith($"Commit: {sha}");
        answer.Text().ShouldContain("Arch");
        var structured = answer.StructuredContent!.Value;
        structured.GetProperty("commitSha").GetString().ShouldBe(sha);
        structured.GetProperty("source").GetString().ShouldBe("default-branch");
        structured.GetProperty("repo").GetString().ShouldBe(ArchitectureGraphFixture.Repository);

        // The consolidated repository_report envelope carries the same commit.
        var wrapped = await ConsolidatedMcpServer.RepositoryReport(mcp, "architecture", ArchitectureGraphFixture.Repository);
        using var envelope = System.Text.Json.JsonDocument.Parse(wrapped.Text());
        envelope.RootElement.GetProperty("commitSha").GetString().ShouldBe(sha);
    }

    [Theory]
    [InlineData(NodeLabel.NuGetPackage, "npm:react", null, null, "npm")]
    [InlineData(NodeLabel.NuGetPackage, "npm:@scope/pkg", "npm", null, "npm")]
    [InlineData(NodeLabel.NuGetPackage, "nuget:Newtonsoft.Json", null, null, "nuget")]
    [InlineData(NodeLabel.NuGetPackage, "Newtonsoft.Json", null, null, "nuget")]
    [InlineData(NodeLabel.Package, "cargo:serde@1", null, "cargo", "cargo")]
    [InlineData(NodeLabel.Package, "x", null, null, "unknown")]
    public void PackageEcosystem_DerivesNpmForSidecarPackagesLabelledNuGet(
        NodeLabel label, string qualifiedName, string? packageType, string? ecosystem, string expected)
    {
        var properties = new Dictionary<string, object>();
        if (packageType is not null)
            properties["package_type"] = packageType;
        if (ecosystem is not null)
            properties["ecosystem"] = ecosystem;

        GraphQueryEngine.PackageEcosystem(new GraphNode
        {
            Repository = "Arch",
            Label = label,
            Name = qualifiedName,
            QualifiedName = qualifiedName,
            Properties = properties
        }).ShouldBe(expected);
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
                new CrossRepoEdge { SourceRepository = "Up", TargetRepository = "Arch", Type = EdgeType.CALLS },
                new CrossRepoEdge { SourceRepository = "Up", TargetRepository = "Arch", Type = EdgeType.CALLS }
            ],
            [new CrossRepoEdge { SourceRepository = "Arch", TargetRepository = "Down", Type = EdgeType.REFERENCES_PACKAGE }],
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
/// One repository graph exercising every architecture aggregate, seeded identically into each store
/// implementation so their results can be compared.
/// </summary>
internal static class ArchitectureGraphFixture
{
    public const string Repository = "Arch";
    public const string OtherRepository = "ArchOther";
    public const int LargeMethodCount = 10_050;

    public static async Task SeedAsync(
        IGraphStore store,
        int fillerMethodCount,
        string repository = Repository,
        string otherRepository = OtherRepository)
    {
        GraphNode Node(
            NodeLabel label,
            string name,
            string qualifiedName,
            Dictionary<string, object>? properties = null,
            bool other = false) => new()
        {
            Repository = other ? otherRepository : repository,
            Label = label,
            Name = name,
            QualifiedName = qualifiedName,
            FilePath = "src/lib.rs",
            Properties = properties ?? new()
        };

        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = repository });
        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = otherRepository });

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
            // The TypeScript sidecar labels npm packages NuGetPackage (sc-26145: reported as nuget).
            Node(NodeLabel.NuGetPackage, "react", "npm:react",
                new() { ["package_type"] = "npm" }),
            Node(NodeLabel.Method, "busy", "ArchOther:busy", other: true)
        ]);
        var ids = await store.UpsertNodeBatchAsync(nodes);
        long Id(string qualifiedName, bool other = false) =>
            ids[GraphNodeKey.Create(other ? otherRepository : repository, qualifiedName)];

        var edges = new List<GraphEdge>();
        void Add(string source, string target, EdgeType type) =>
            edges.Add(new GraphEdge { Repository = repository, SourceId = Id(source), TargetId = Id(target), Type = type });

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
        Add("Arch:Widget", "npm:react", EdgeType.REFERENCES_PACKAGE);

        // Another repository's edges never count toward this repository's figures.
        for (var i = 0; i < 9; i++)
            edges.Add(new GraphEdge
            {
                Repository = otherRepository,
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
        report.NodeCounts[NodeLabel.NuGetPackage].ShouldBe(2);

        report.Hotspots.Select(h => (h.Name, h.FanIn)).ShouldBe([("hot", 5), ("warm", 3)]);

        report.TopSupertypes.Count.ShouldBe(4);
        report.TopSupertypes.Take(2).Select(t => (t.Name, t.SubtypeCount, t.IsExternal))
            .ShouldBe([("Shape", 3, false), ("Codable", 2, true)]);
        report.TopSupertypes.Skip(2).Select(t => (t.Name, t.SubtypeCount, t.IsExternal))
            .OrderBy(t => t.Name)
            .ShouldBe([("Base", 1, false), ("Serialize", 1, true)]);

        report.PackageDependencyCount.ShouldBe(4);
        report.PackageDependencyCountsByEcosystem.OrderBy(kv => kv.Key)
            .ShouldBe([
                new KeyValuePair<string, int>("cargo", 1),
                new KeyValuePair<string, int>("npm", 1),
                new KeyValuePair<string, int>("nuget", 1),
                new KeyValuePair<string, int>("swiftpm", 1)
            ]);
        report.PackageDependencies.Select(p => (p.Name, p.Ecosystem, p.Version, p.DependentCount))
            .ShouldBe([
                ("serde", "cargo", "1.0", 2),
                ("react", "npm", (string?)null, 1),
                ("Newtonsoft.Json", "nuget", "13.0.1", 1),
                ("swift-collections", "swiftpm", "1.1.0", 1)
            ]);
    }
}
