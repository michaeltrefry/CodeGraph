using System.Text.Json;
using CodeGraph.Extractors.Rust;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Metadata;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

[Collection("Process environment")]
public sealed class RustCrateGroupPipelineTests
{
    [Fact]
    public async Task IndexProjectAsync_ThreeCrateGroups_FailingGroupFallsBackAloneAndCrossGroupCallLinks()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        toolchain.SetEnvironment(FakeRustToolchain.FailingCratesVariable, "gamma");
        var store = new InMemoryGraphStore();
        var diagnostics = new IndexingRunDiagnostics();
        var pipeline = CreatePipeline(store, diagnostics);

        await pipeline.IndexProjectAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None);

        // Only the failing group falls back, with exactly its crate.
        var fallback = diagnostics.RustSemanticFallbacks.ShouldHaveSingleItem();
        fallback.Crates.ShouldBe(["gamma"]);
        fallback.Manifest.ShouldBe("Cargo.toml");
        fallback.DetailCode.ShouldBe("rust_analyzer_scip_failed");
        var gammaNodes = CodeNodes(store, "crates/gamma/src/lib.rs");
        gammaNodes.ShouldContain(node => node.Name == "gamma_value");
        gammaNodes.ShouldAllBe(node => GraphNodeConfidence.IsLowConfidence(node));
        gammaNodes.ShouldAllBe(node =>
            (string)node.Properties[GraphNodeConfidence.ExtractionSourceProperty] ==
            GraphNodeConfidence.TreeSitterFallbackSource);

        // The other two groups stay semantic.
        foreach (var (crate, function) in new[] { ("alpha", "alpha_entry"), ("beta", "beta_compute") })
        {
            var nodes = CodeNodes(store, $"crates/{crate}/src/lib.rs");
            nodes.ShouldContain(node => node.Name == function && (string)node.Properties["source"] == "scip");
            nodes.ShouldNotContain(node => GraphNodeConfidence.IsLowConfidence(node));
        }

        // alpha (group 0) calls beta (group 1): the CALLS edge joins the two definitions.
        var caller = store.Nodes.Single(node => node.Name == "alpha_entry");
        var target = store.Nodes.Single(node =>
            node.Name == "beta_compute" && node.FilePath == "crates/beta/src/lib.rs");
        store.Edges.ShouldContain(edge =>
            edge.Type == EdgeType.CALLS && edge.SourceId == caller.Id && edge.TargetId == target.Id);
        store.Nodes.ShouldNotContain(node =>
            node.Label == NodeLabel.ExternalSymbol &&
            node.Properties.ContainsKey("scip_symbol") &&
            (string)node.Properties["scip_symbol"] == FakeRustToolchain.Symbol("beta", "beta_compute"));

        var repository = await store.GetRepositoryByName("RustGroups");
        var recorded = ((JsonElement)repository!.Properties!["rustSemanticFallbacks"]).EnumerateArray().Single();
        recorded.GetProperty("crates").EnumerateArray().Select(crate => crate.GetString()).ShouldBe(["gamma"]);
    }

    [Fact]
    public async Task IndexProjectAsync_SeveralFailingGroups_RecordsOneFallbackPerGroupInGroupOrder()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        toolchain.SetEnvironment(FakeRustToolchain.FailingCratesVariable, "gamma alpha");
        var store = new InMemoryGraphStore();
        var diagnostics = new IndexingRunDiagnostics();
        var pipeline = CreatePipeline(store, diagnostics);

        await pipeline.IndexProjectAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None);

        diagnostics.RustSemanticFallbacks.Select(fallback => string.Join(",", fallback.Crates))
            .ShouldBe(["alpha", "gamma"]);
        CodeNodes(store, "crates/alpha/src/lib.rs").ShouldAllBe(node => GraphNodeConfidence.IsLowConfidence(node));
        CodeNodes(store, "crates/gamma/src/lib.rs").ShouldAllBe(node => GraphNodeConfidence.IsLowConfidence(node));
        CodeNodes(store, "crates/beta/src/lib.rs").ShouldNotContain(node => GraphNodeConfidence.IsLowConfidence(node));
    }

    [Fact]
    public async Task IndexProjectAsync_WorkspaceTimeout_EveryUnfinishedGroupFallsBackAndTheRunCompletes()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var toolchain = CreateThreeCrateWorkspace();
        toolchain.SetEnvironment(FakeRustToolchain.SlowCratesVariable, "alpha");
        var store = new InMemoryGraphStore();
        var diagnostics = new IndexingRunDiagnostics();
        var pipeline = CreatePipeline(store, diagnostics, workspaceTimeoutSeconds: 1);

        await pipeline.IndexProjectAsync("RustGroups", toolchain.RootPath, ct: CancellationToken.None);

        diagnostics.RustSemanticFallbacks.Select(fallback => string.Join(",", fallback.Crates))
            .ShouldBe(["alpha", "beta", "gamma"]);
        diagnostics.RustSemanticFallbacks.ShouldAllBe(fallback =>
            fallback.FailureCode == RustSemanticFailureCategory.Timeout &&
            fallback.DetailCode == "rust_semantic_workspace_timeout");
        foreach (var crate in new[] { "alpha", "beta", "gamma" })
        {
            var nodes = CodeNodes(store, $"crates/{crate}/src/lib.rs");
            nodes.ShouldNotBeEmpty();
            nodes.ShouldAllBe(node => GraphNodeConfidence.IsLowConfidence(node));
        }
    }

    private static List<GraphNode> CodeNodes(InMemoryGraphStore store, string filePath) =>
        store.Nodes
            .Where(node => node.Project == "RustGroups" && node.FilePath == filePath &&
                           node.Label is not (NodeLabel.File or NodeLabel.Folder))
            .ToList();

    private static FakeRustToolchain CreateThreeCrateWorkspace()
    {
        var toolchain = new FakeRustToolchain()
            .AddCrate(
                "alpha",
                "pub fn alpha_entry() -> u32 {\n    beta::beta_compute(2)\n}\n",
                FakeRustToolchain.ScipDocument(
                    "alpha", "alpha_entry", FakeRustToolchain.Symbol("beta", "beta_compute")),
                dependencies: ["beta"])
            .AddCrate(
                "beta",
                "pub fn beta_compute(x: u32) -> u32 {\n    x * 2\n}\n",
                FakeRustToolchain.ScipDocument("beta", "beta_compute"))
            .AddCrate(
                "gamma",
                "pub fn gamma_value() -> u32 {\n    7\n}\n",
                FakeRustToolchain.ScipDocument("gamma", "gamma_value"));
        toolchain.Install();
        return toolchain;
    }

    private static IndexingPipeline CreatePipeline(
        InMemoryGraphStore store,
        IndexingRunDiagnostics diagnostics,
        int workspaceTimeoutSeconds = 3600)
    {
        var options = Options.Create(new IndexingOptions
        {
            MaxParallelFiles = 1,
            RustSemanticCrateGroupSize = 1,
            RustSemanticWorkspaceTimeoutSeconds = workspaceTimeoutSeconds
        });
        return new IndexingPipeline(
            store,
            [new CodeGraph.Extractors.TreeSitter.TreeSitterExtractor(
                NullLogger<CodeGraph.Extractors.TreeSitter.TreeSitterExtractor>.Instance)],
            options,
            new LocalFileSystem(),
            NullLogger<IndexingPipeline>.Instance,
            rustAnalyzer: new RustProjectAnalyzer(NullLogger<RustProjectAnalyzer>.Instance, options),
            cargoManifestExtractor: new CargoManifestExtractor(),
            diagnostics: diagnostics);
    }
}
