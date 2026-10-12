using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services;
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
/// Cargo git and path dependencies between indexed repositories: manifests are extracted, linked
/// and then read back through the model-free MCP tools. "inference" is the upstream workspace
/// (api depends on core); "SceneWorks" depends on api by git URL, "Tools" depends on core by a
/// path that leaves its repository, and "Fork" defines crates of the same names at another URL.
/// </summary>
public sealed class CargoCrossRepoDependencyTests
{
    private const string Inference = "inference";
    private const string SceneWorks = "SceneWorks";
    private const string Tools = "Tools";
    private const string Fork = "Fork";

    private readonly InMemoryGraphStore _store = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cargo-cross-repo-{Guid.NewGuid():N}");

    private const string UpstreamWorkspace = """
        [workspace]
        members = ["crates/core", "crates/api", "crates/solo"]
        """;

    private static string Crate(string name, string dependencies = "") => $"""
        [package]
        name = "{name}"
        version = "0.1.0"
        publish = false

        [dependencies]
        {dependencies}
        """;

    private async Task SeedAsync()
    {
        Index(Inference, "https://github.com/Acme/Inference.git", new()
        {
            ["Cargo.toml"] = UpstreamWorkspace,
            ["crates/core/Cargo.toml"] = Crate("core"),
            ["crates/api/Cargo.toml"] = Crate("api", """core = { path = "../core" }"""),
            ["crates/solo/Cargo.toml"] = Crate("solo")
        });
        Index(Fork, "https://github.com/someone/inference", new()
        {
            ["Cargo.toml"] = UpstreamWorkspace,
            ["crates/core/Cargo.toml"] = Crate("core"),
            ["crates/api/Cargo.toml"] = Crate("api"),
        });
        Index(SceneWorks, "https://github.com/acme/sceneworks", new()
        {
            ["Cargo.toml"] = Crate("sceneworks-app", """
                inf-api = { package = "api", git = "ssh://git@github.com/acme/inference.git", rev = "abc123" }
                unindexed = { package = "solo", git = "https://github.com/someone/else" }
                vendored = { package = "solo", path = "vendor/solo" }
                """)
        });
        Index(Tools, null, new()
        {
            ["Cargo.toml"] = """
                [workspace]
                members = ["crates/cli"]

                [workspace.dependencies]
                core = { path = "../inference/crates/core" }
                """,
            ["crates/cli/Cargo.toml"] = Crate("tools-cli", "core.workspace = true")
        });

        foreach (var (project, file) in new[] { (Inference, "crates/core/src/lib.rs"), (Inference, "crates/api/src/lib.rs"), (Fork, "crates/core/src/lib.rs") })
        {
            _store.AddNode(new GraphNode
            {
                Project = project,
                Label = NodeLabel.Function,
                Name = "run",
                QualifiedName = $"{project}:{file}:run",
                FilePath = file
            });
        }

        await new CrossRepoLinker(_store, NullLogger<CrossRepoLinker>.Instance).LinkAsync(CancellationToken.None);
    }

    private void Index(string project, string? repoUrl, Dictionary<string, string> manifests)
    {
        var rootPath = Path.Combine(_root, project);
        var result = new CargoManifestExtractor().Extract(
            manifests.ToDictionary(manifest => Path.Combine(rootPath, manifest.Key), manifest => manifest.Value),
            new ExtractorContext { ProjectName = project, RootPath = rootPath });

        _store.AddProject(new ProjectInfo(project, repoUrl, null, null, "sha-" + project, null, "Rust", "Cargo", false, null));
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
    public async Task Linker_LinksGitDependencyByRepositoryUrl_AndEscapingPathDependencyByCrateName()
    {
        await SeedAsync();

        var links = new List<(string Source, string Target, string Crate, string MatchedBy, string Band)>();
        foreach (var edge in _store.CrossEdges)
        {
            edge.Type.ShouldBe(EdgeType.REFERENCES_PACKAGE);
            var target = await NodeAsync(edge.TargetNodeId);
            target.Project.ShouldBe(edge.TargetProject);
            ((bool)target.Properties["is_definition"]).ShouldBeTrue();
            (await NodeAsync(edge.SourceNodeId)).Project.ShouldBe(edge.SourceProject);
            links.Add((edge.SourceProject, edge.TargetProject, target.Name,
                (string)edge.Properties["matched_by"], (string)edge.Properties["confidence_band"]));
        }

        // Not linked: the git dependency on a repository that is not indexed and the in-repo path
        // dependency without a manifest, although "solo" is defined by exactly one other repository;
        // nor anything to Fork, which defines "api" and "core" at a different URL and directory.
        links.ShouldBe(
        [
            (SceneWorks, Inference, "api", "git_url", "high"),
            (Tools, Inference, "core", "path", "medium")
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task Extractor_MarksOnlyPathDependenciesThatLeaveTheRepository()
    {
        await SeedAsync();

        var escaping = (await _store.FindNodesByNameAsync(Tools, "core")).ShouldHaveSingleItem();
        escaping.Properties["external_path"].ShouldBe("../inference/crates/core");

        var vendored = (await _store.FindNodesByNameAsync(SceneWorks, "solo"))
            .Single(node => (string)node.Properties["source_kind"] == "path");
        vendored.Properties.ShouldNotContainKey("external_path");
        // An in-repo path dependency resolves to the crate's definition and is not marked either.
        (await _store.FindNodesByNameAsync(Inference, "core")).ShouldHaveSingleItem()
            .Properties.ShouldNotContainKey("external_path");
    }

    [Theory]
    [InlineData("https://github.com/Acme/Inference.git", "github.com/acme/inference")]
    [InlineData("https://github.com/acme/inference/", "github.com/acme/inference")]
    [InlineData("ssh://git@github.com:22/acme/inference.git", "github.com/acme/inference")]
    [InlineData("git@github.com:acme/inference.git", "github.com/acme/inference")]
    [InlineData("https://token@gitlab.example.com/group/sub/repo.git?ref=main", "gitlab.example.com/group/sub/repo")]
    [InlineData("github.com/acme/inference", "github.com/acme/inference")]
    [InlineData("inference", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeGitUrl_ComparesSpellingsOfOneRepositoryAsEqual(string? url, string? expected)
        => CrossRepoLinker.NormalizeGitUrl(url).ShouldBe(expected);

    [Fact]
    public async Task AnalyzeImpactFile_OnAnUpstreamCrateFile_ReportsDownstreamRepositoriesAsCrossRepoImpact()
    {
        await SeedAsync();
        var mcp = Mcp();

        // api: SceneWorks depends on it directly by git URL.
        var api = (await mcp.AnalyzeImpactFile(Inference, "crates/api/src/lib.rs")).StructuredContent!.Value;
        CrossRepoImpacts(api).ShouldBe([(SceneWorks, Inference, "REFERENCES_PACKAGE", 1)]);
        api.GetProperty("crossRepoCount").GetInt32().ShouldBe(1);

        // core: Tools depends on it by path, and SceneWorks through api (two package hops).
        var coreAnswer = await mcp.AnalyzeImpactFile(Inference, "crates/core/src/lib.rs");
        var core = coreAnswer.StructuredContent!.Value;
        CrossRepoImpacts(core).ShouldBe(
        [
            (SceneWorks, Inference, "REFERENCES_PACKAGE", 1),
            (Tools, Inference, "REFERENCES_PACKAGE", 1)
        ], ignoreOrder: true);
        core.GetProperty("crossRepoCount").GetInt32().ShouldBe(2);
        coreAnswer.Text().ShouldContain($"- {Tools} → {Inference} (REFERENCES_PACKAGE, 1)");
        // Crate-level dependents are not affected nodes: the risk reading is unchanged.
        core.GetProperty("blastRadius").GetString().ShouldBe("none");
        core.GetProperty("totalAffected").GetInt32().ShouldBe(0);

        var oneHop = (await mcp.AnalyzeImpactFile(Inference, "crates/core/src/lib.rs", depth: 1)).StructuredContent!.Value;
        CrossRepoImpacts(oneHop).ShouldBe([(Tools, Inference, "REFERENCES_PACKAGE", 1)]);

        // Fork's core has the same crate name but nothing links to it.
        var fork = (await mcp.AnalyzeImpactFile(Fork, "crates/core/src/lib.rs")).StructuredContent!.Value;
        CrossRepoImpacts(fork).ShouldBeEmpty();
        fork.GetProperty("crossRepoCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task GetRepoDependencies_AnswersInboundAndOutboundRepositoriesWithEdgeTypes()
    {
        await SeedAsync();
        // A second link kind and a second link of one kind, to pin the grouping.
        await _store.InsertCrossRepoEdgeBatchAsync(
        [
            new CrossRepoEdge { SourceProject = Inference, TargetProject = Tools, SourceNodeId = 1, TargetNodeId = 2, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceProject = Inference, TargetProject = Tools, SourceNodeId = 3, TargetNodeId = 2, Type = EdgeType.CALLS },
            new CrossRepoEdge { SourceProject = SceneWorks, TargetProject = Inference, SourceNodeId = 4, TargetNodeId = 5, Type = EdgeType.HTTP_CALLS }
        ]);
        var mcp = Mcp();

        var answer = await mcp.GetRepoDependencies(Inference);

        answer.IsError.ShouldNotBe(true);
        var s = answer.StructuredContent!.Value;
        s.GetProperty("repo").GetString().ShouldBe(Inference);
        s.GetProperty("commitSha").GetString().ShouldBe("sha-" + Inference);
        Dependencies(s, "inbound").ShouldBe(
        [
            (SceneWorks, "HTTP_CALLS", 1),
            (SceneWorks, "REFERENCES_PACKAGE", 1),
            (Tools, "REFERENCES_PACKAGE", 1)
        ]);
        Dependencies(s, "outbound").ShouldBe([(Tools, "CALLS", 2)]);
        answer.Text().ShouldContain($"- {Tools} (CALLS, 2)");

        var downstream = (await mcp.GetRepoDependencies(SceneWorks)).StructuredContent!.Value;
        Dependencies(downstream, "inbound").ShouldBeEmpty();
        Dependencies(downstream, "outbound").ShouldBe(
        [
            (Inference, "HTTP_CALLS", 1),
            (Inference, "REFERENCES_PACKAGE", 1)
        ]);

        var missing = await mcp.GetRepoDependencies("nope");
        missing.IsError.ShouldBe(true);
        missing.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString()
            .ShouldBe(CodeGraphMcpServer.RepoNotFoundCode);
    }

    [Fact]
    public async Task GetRepoDependencies_ReportsWhichCrateGroupsFellBackToTreeSitter()
    {
        // As stored in repositories.properties by a run whose second group failed and third was skipped.
        var outcomes = new RustSemanticOutcomes(
            "Cargo.toml", CrateCount: 5, GroupCount: 3, GroupSize: 2,
            SemanticGroups: 1, FallbackGroups: 1, SkippedGroups: 1, TotalDurationMs: 10,
            new RustSemanticSettingsSnapshot(2, 60, 600, 4096, true, true),
            [
                new RustSemanticGroupOutcome(0, ["core", "api"], RustSemanticGroupOutcome.Semantic, null, null, 5, null, 2, 10, 20),
                new RustSemanticGroupOutcome(1, ["gpu", "metal"], RustSemanticGroupOutcome.Fallback, "timeout", "rust_analyzer_scip_timeout", 5, null, null, null, null),
                new RustSemanticGroupOutcome(2, ["solo"], RustSemanticGroupOutcome.Skipped, "timeout", null, 0, null, null, null, null)
            ]);
        var camelCase = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        AddRustRepository("mixed", JsonSerializer.SerializeToElement(outcomes, camelCase));
        AddRustRepository("clean", JsonSerializer.SerializeToElement(
            outcomes with { Groups = [outcomes.Groups[0]] }, camelCase));
        AddRustRepository("failed", JsonSerializer.SerializeToElement(
            outcomes with { Groups = [outcomes.Groups[1]] }, camelCase));
        _store.AddProject("plain");
        var mcp = Mcp();

        var mixedAnswer = await mcp.GetRepoDependencies("mixed");
        var mixed = mixedAnswer.StructuredContent!.Value.GetProperty("rustSemantic");
        mixed.GetProperty("status").GetString().ShouldBe("partial");
        mixed.GetProperty("manifest").GetString().ShouldBe("Cargo.toml");
        mixed.GetProperty("groupCount").GetInt32().ShouldBe(3);
        mixed.GetProperty("semanticGroups").GetInt32().ShouldBe(1);
        mixed.GetProperty("fallbackGroups").GetInt32().ShouldBe(1);
        mixed.GetProperty("skippedGroups").GetInt32().ShouldBe(1);
        mixed.GetProperty("treeSitterGroups").EnumerateArray()
            .Select(group => (
                group.GetProperty("index").GetInt32(),
                string.Join(",", group.GetProperty("crates").EnumerateArray().Select(crate => crate.GetString())),
                group.GetProperty("outcome").GetString(),
                group.GetProperty("failureCode").GetString(),
                group.GetProperty("detailCode").GetString()))
            .ShouldBe(
            [
                (1, "gpu,metal", "fallback", "timeout", "rust_analyzer_scip_timeout"),
                (2, "solo", "skipped", "timeout", null)
            ]);
        mixedAnswer.Text().ShouldContain("- group 1 fallback (timeout): gpu, metal");

        string Status(JsonElement structured) => structured.GetProperty("rustSemantic").GetProperty("status").GetString()!;
        var clean = (await mcp.GetRepoDependencies("clean")).StructuredContent!.Value;
        Status(clean).ShouldBe("semantic");
        clean.GetProperty("rustSemantic").GetProperty("treeSitterGroups").GetArrayLength().ShouldBe(0);
        Status((await mcp.GetRepoDependencies("failed")).StructuredContent!.Value).ShouldBe("fallback");
        var plain = (await mcp.GetRepoDependencies("plain")).StructuredContent!.Value;
        Status(plain).ShouldBe("not_analyzed");
        plain.GetProperty("rustSemantic").GetProperty("treeSitterGroups").GetArrayLength().ShouldBe(0);
    }

    private void AddRustRepository(string name, JsonElement outcomes) =>
        _store.AddProject(new ProjectInfo(name, null, null, null, null, null, "Rust", "Cargo", false,
            new Dictionary<string, object> { ["rustSemanticOutcomes"] = outcomes }));

    private static List<(string Source, string Target, string EdgeType, int Count)> CrossRepoImpacts(JsonElement structured)
        => structured.GetProperty("crossRepoImpacts").EnumerateArray()
            .Select(impact => (
                impact.GetProperty("sourceRepo").GetString()!,
                impact.GetProperty("targetRepo").GetString()!,
                impact.GetProperty("edgeType").GetString()!,
                impact.GetProperty("count").GetInt32()))
            .ToList();

    private static List<(string Repo, string EdgeType, int Count)> Dependencies(JsonElement structured, string direction)
        => structured.GetProperty(direction).EnumerateArray()
            .Select(dependency => (
                dependency.GetProperty("repo").GetString()!,
                dependency.GetProperty("edgeType").GetString()!,
                dependency.GetProperty("count").GetInt32()))
            .ToList();
}
