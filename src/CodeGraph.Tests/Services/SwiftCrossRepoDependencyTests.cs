using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Pipeline;
using CodeGraph.Services.Query;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// Swift package dependencies between indexed repositories: SwiftPM and Xcode manifests are
/// extracted, linked and read back through the model-free MCP tools. "SwiftCore" is the upstream
/// package (with a nested example package); each "App*" repository depends on it by one spelling
/// of its URL; "XcodeApp" only pins it in an Xcode Package.resolved; "Tools" depends on
/// "SharedKit" by a path that leaves its repository; "Fork" defines a package of the same name at
/// another URL.
/// </summary>
public sealed class SwiftCrossRepoDependencyTests
{
    private const string Core = "SwiftCore";
    private const string Fork = "Fork";
    private const string AppHttps = "AppHttps";
    private const string AppScp = "AppScp";
    private const string AppSsh = "AppSsh";
    private const string XcodeApp = "XcodeApp";
    private const string Tools = "Tools";
    private const string Shared = "SharedKit";

    private const string XcodeResolvedPath = "App.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved";

    private readonly InMemoryGraphStore _store = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"swift-cross-repo-{Guid.NewGuid():N}");

    private static string Package(string name, string dependencies = "") => $$"""
        // swift-tools-version:5.9
        import PackageDescription

        let package = Package(
            name: "{{name}}",
            dependencies: [
                {{dependencies}}
            ],
            targets: [.target(name: "{{name}}")]
        )
        """;

    private const string XcodeResolved = """
        {
          "pins" : [
            {
              "identity" : "swiftcore",
              "kind" : "remoteSourceControl",
              "location" : "https://github.com/acme/swiftcore.git",
              "state" : {
                "revision" : "eef1a539a373c1f1a320624b1130fc5de7b2e100",
                "version" : "1.4.0"
              }
            }
          ],
          "version" : 2
        }
        """;

    private async Task SeedAsync()
    {
        Index(Core, "https://github.com/Acme/SwiftCore.git", new()
        {
            ["Package.swift"] = Package(Core),
            // A nested package: a URL dependency resolves to the repository's root manifest only.
            ["Examples/Demo/Package.swift"] = Package("Demo", """.package(path: "../..")""")
        });
        Index(Fork, "https://github.com/someone/SwiftCore", new() { ["Package.swift"] = Package(Core) });
        Index(AppHttps, "https://github.com/acme/app-https", new()
        {
            ["Package.swift"] = Package(AppHttps, """
                .package(url: "https://github.com/acme/swiftcore", from: "1.0.0"),
                .package(url: "https://github.com/apple/swift-log.git", from: "1.5.0"),
                """)
        });
        Index(AppScp, null, new()
        {
            ["Package.swift"] = Package(AppScp, """.package(url: "git@github.com:acme/SwiftCore.git", exact: "1.4.0"),""")
        });
        Index(AppSsh, null, new()
        {
            ["Package.swift"] = Package(AppSsh, """.package(url: "ssh://git@github.com:22/acme/swiftcore.git", branch: "main"),""")
        });
        Index(XcodeApp, null, new() { [XcodeResolvedPath] = XcodeResolved }, hasXcodeProject: true);
        Index(Shared, null, new() { ["Package.swift"] = Package(Shared) });
        Index(Tools, null, new()
        {
            ["Package.swift"] = Package(Tools, """.package(path: "../checkouts/SharedKit"),""")
        });

        _store.AddNode(new GraphNode
        {
            Project = Core,
            Label = NodeLabel.Function,
            Name = "run",
            QualifiedName = $"{Core}:Sources/SwiftCore/Core.swift:run",
            FilePath = "Sources/SwiftCore/Core.swift"
        });

        await new CrossRepoLinker(_store, NullLogger<CrossRepoLinker>.Instance).LinkAsync(CancellationToken.None);
    }

    private void Index(string project, string? repoUrl, Dictionary<string, string> manifests, bool hasXcodeProject = false)
    {
        var rootPath = Path.Combine(_root, project);
        var result = new SwiftPackageManifestExtractor().Extract(
            manifests.ToDictionary(manifest => Path.Combine(rootPath, manifest.Key), manifest => manifest.Value),
            hasXcodeProject,
            new ExtractorContext { ProjectName = project, RootPath = rootPath });

        _store.AddProject(new ProjectInfo(project, repoUrl, null, null, "sha-" + project, null, "Swift", "SwiftPM", false, null));
        var ids = result.Nodes.ToDictionary(node => node.QualifiedName, node => _store.AddNode(node));
        ids[project] = _store.AddNode(new GraphNode
        {
            Project = project,
            Label = NodeLabel.Repository,
            Name = project,
            QualifiedName = project
        });
        foreach (var edge in result.Edges)
        {
            _store.AddEdge(new GraphEdge
            {
                Project = project,
                SourceId = ids[edge.SourceQN],
                TargetId = ids[edge.TargetQN],
                Type = edge.Type,
                Properties = edge.Properties ?? new()
            });
        }
    }

    private CodeGraphMcpServer Mcp() => new(
        new GraphQueryEngine(_store, NullLogger<GraphQueryEngine>.Instance),
        null!,
        _store,
        null!,
        Options.Create(new RepositorySourceOptions()),
        null!,
        new ImpactAnalysisService(_store, NullLogger<ImpactAnalysisService>.Instance));

    private async Task<GraphNode> NodeAsync(long id) => (await _store.FindNodeByIdAsync(id))!;

    [Fact]
    public async Task Linker_LinksEveryUrlSpellingToTheRootPackageOfTheIndexedRepository()
    {
        await SeedAsync();

        var links = new List<(string Source, string Target, string SourceQN, string TargetQN, string MatchedBy, string Band)>();
        foreach (var edge in _store.CrossEdges)
        {
            edge.Type.ShouldBe(EdgeType.REFERENCES_PACKAGE);
            edge.Properties["ecosystem"].ShouldBe("swiftpm");
            var source = await NodeAsync(edge.SourceNodeId);
            var target = await NodeAsync(edge.TargetNodeId);
            source.Project.ShouldBe(edge.SourceProject);
            target.Project.ShouldBe(edge.TargetProject);
            ((bool)target.Properties["is_definition"]).ShouldBeTrue();
            links.Add((edge.SourceProject, edge.TargetProject, source.QualifiedName, target.QualifiedName,
                (string)edge.Properties["matched_by"], (string)edge.Properties["confidence_band"]));
        }

        const string coreDefinition = "package:swiftpm:local:swiftcore:swiftcore@*";
        // Not linked: swift-log (not indexed), Fork (same package name, other URL), the nested Demo
        // package (a URL dependency is the repository's root manifest) and Demo's in-repo path.
        links.ShouldBe(
        [
            (AppHttps, Core, "package:swiftpm:local:apphttps:apphttps@*", coreDefinition, "git_url", "high"),
            (AppScp, Core, "package:swiftpm:local:appscp:appscp@*", coreDefinition, "git_url", "high"),
            (AppSsh, Core, "package:swiftpm:local:appssh:appssh@*", coreDefinition, "git_url", "high"),
            // An Xcode project's pins hang off the repository node.
            (XcodeApp, Core, XcodeApp, coreDefinition, "git_url", "high"),
            (Tools, Shared, "package:swiftpm:local:tools:tools@*", "package:swiftpm:local:sharedkit:sharedkit@*", "path", "medium")
        ], ignoreOrder: true);

        var pinned = _store.CrossEdges.Single(edge => edge.SourceProject == XcodeApp);
        pinned.Properties["package_key"].ShouldBe("swiftpm:scm:github.com/acme/swiftcore");
        pinned.Properties["identity"].ShouldBe("swiftcore");
        pinned.Properties["version"].ShouldBe("1.4.0");
    }

    [Fact]
    public async Task Linker_LeavesAnUnmatchedUrlAsAnExternalPackage()
    {
        await SeedAsync();

        var log = (await _store.FindNodesByNameAsync(AppHttps, "swift-log")).ShouldHaveSingleItem();
        log.Label.ShouldBe(NodeLabel.Package);
        log.Properties["source_kind"].ShouldBe("remote");
        log.Properties["location"].ShouldBe("https://github.com/apple/swift-log.git");
        _store.CrossEdges.ShouldNotContain(edge => edge.TargetNodeId == log.Id || edge.SourceNodeId == log.Id);
        _store.CrossEdges.ShouldNotContain(edge => edge.TargetProject == Fork);
    }

    [Fact]
    public async Task Linker_NeverLinksAcrossPackageEcosystems()
    {
        // A Cargo git dependency and a Swift package dependency on the same repository URL.
        Index(Core, "https://github.com/acme/swiftcore", new() { ["Package.swift"] = Package(Core) });
        var cargo = new CargoManifestExtractor().Extract(
            new Dictionary<string, string>
            {
                [Path.Combine(_root, "Mixed", "Cargo.toml")] = """
                    [package]
                    name = "mixed"
                    version = "0.1.0"

                    [dependencies]
                    swiftcore = { git = "https://github.com/acme/swiftcore" }
                    """
            },
            new ExtractorContext { ProjectName = "Mixed", RootPath = Path.Combine(_root, "Mixed") });
        _store.AddProject("Mixed");
        var ids = cargo.Nodes.ToDictionary(node => node.QualifiedName, node => _store.AddNode(node));
        foreach (var edge in cargo.Edges.Where(edge => ids.ContainsKey(edge.SourceQN)))
            _store.AddEdge(new GraphEdge { Project = "Mixed", SourceId = ids[edge.SourceQN], TargetId = ids[edge.TargetQN], Type = edge.Type });

        await new CrossRepoLinker(_store, NullLogger<CrossRepoLinker>.Instance).LinkAsync(CancellationToken.None);

        // A Cargo dependency never resolves to a Swift package definition, and vice versa.
        _store.CrossEdges.ShouldBeEmpty();
    }

    [Fact]
    public async Task LinkForProject_ScopedToTheUpstream_RelinksItsInboundSwiftDependents()
    {
        await SeedAsync();

        await new CrossRepoLinker(_store, NullLogger<CrossRepoLinker>.Instance)
            .LinkForProjectAsync(Core, CancellationToken.None);

        _store.CrossEdges.Where(edge => edge.TargetProject == Core)
            .Select(edge => edge.SourceProject).Order(StringComparer.Ordinal)
            .ShouldBe([AppHttps, AppScp, AppSsh, XcodeApp]);
        _store.CrossEdges.Count.ShouldBe(5);
    }

    [Fact]
    public async Task GetRepoDependencies_SurfacesSwiftPackageLinks()
    {
        await SeedAsync();
        var mcp = Mcp();

        var core = (await mcp.GetRepoDependencies(Core)).StructuredContent!.Value;
        Dependencies(core, "inbound").ShouldBe(
        [
            (AppHttps, "REFERENCES_PACKAGE", 1, "high"),
            (AppScp, "REFERENCES_PACKAGE", 1, "high"),
            (AppSsh, "REFERENCES_PACKAGE", 1, "high"),
            (XcodeApp, "REFERENCES_PACKAGE", 1, "high")
        ]);
        Dependencies(core, "outbound").ShouldBeEmpty();

        var toolsAnswer = await mcp.GetRepoDependencies(Tools);
        Dependencies(toolsAnswer.StructuredContent!.Value, "outbound")
            .ShouldBe([(Shared, "REFERENCES_PACKAGE", 1, "medium")]);
        toolsAnswer.Text().ShouldContain($"- {Shared} (REFERENCES_PACKAGE, 1, medium confidence)");

        Dependencies((await mcp.GetRepoDependencies(Fork)).StructuredContent!.Value, "inbound").ShouldBeEmpty();
    }

    [Fact]
    public async Task AnalyzeImpactFile_OnAnUpstreamSwiftFile_ReportsDependentRepositories()
    {
        await SeedAsync();

        var answer = await Mcp().AnalyzeImpactFile(Core, "Sources/SwiftCore/Core.swift");

        var s = answer.StructuredContent!.Value;
        s.GetProperty("crossRepoImpacts").EnumerateArray()
            .Select(impact => impact.GetProperty("sourceRepo").GetString())
            .Order(StringComparer.Ordinal)
            .ShouldBe([AppHttps, AppScp, AppSsh, XcodeApp]);
        s.GetProperty("crossRepoCount").GetInt32().ShouldBe(4);
        s.GetProperty("blastRadius").GetString().ShouldBe("none");
    }

    private static List<(string Repo, string EdgeType, int Count, string? Band)> Dependencies(
        System.Text.Json.JsonElement structured, string direction)
        => structured.GetProperty(direction).EnumerateArray()
            .Select(dependency => (
                dependency.GetProperty("repo").GetString()!,
                dependency.GetProperty("edgeType").GetString()!,
                dependency.GetProperty("count").GetInt32(),
                dependency.GetProperty("confidenceBand").GetString()))
            .ToList();
}
