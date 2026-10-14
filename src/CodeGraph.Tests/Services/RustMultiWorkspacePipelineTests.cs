using System.Collections.Concurrent;
using System.Text.Json;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// sc-26145 (LensWorks): a repository with several independent Cargo workspaces and no root
/// manifest only had its first workspace semantically analyzed; the rest were tree-sitter only.
/// </summary>
public class RustMultiWorkspacePipelineTests
{
    [Fact]
    public async Task IndexProjectAsync_TwoIndependentWorkspaces_AnalyzesEveryWorkspaceOnce()
    {
        var rootPath = CreateRepository();
        try
        {
            WriteWorkspace(rootPath, "adapters/catalog", "adapter_core", "adapter_http");
            WriteWorkspace(rootPath, "engines/catalog", "engine_core");
            // Build output never counts as a workspace.
            WriteFile(rootPath, "target/package/stale-0.1.0/Cargo.toml",
                "[workspace]\nmembers = []\n");

            var (analyzer, diagnostics, store) = await IndexAsync(rootPath);

            analyzer.Manifests.Order(StringComparer.Ordinal).ShouldBe([
                "adapters/catalog/Cargo.toml",
                "engines/catalog/Cargo.toml"
            ]);

            var outcomes = diagnostics.RustSemanticOutcomes.ShouldNotBeNull();
            outcomes.Manifest.ShouldBe("adapters/catalog/Cargo.toml, engines/catalog/Cargo.toml");
            outcomes.Groups.Select(group => (group.Index, group.Manifest, string.Join("+", group.Crates)))
                .ShouldBe([
                    (0, "adapters/catalog/Cargo.toml", "adapter_core+adapter_http"),
                    (1, "engines/catalog/Cargo.toml", "engine_core")
                ]);
            (outcomes.GroupCount, outcomes.SemanticGroups, outcomes.CrateCount).ShouldBe((2, 2, 3));

            // Semantic nodes of both workspaces reached the graph.
            store.Nodes.Where(node => node.Name.StartsWith("semantic_", StringComparison.Ordinal))
                .Select(node => node.Name).Order(StringComparer.Ordinal)
                .ShouldBe(["semantic_adapter_core", "semantic_adapter_http", "semantic_engine_core"]);

            var repository = await store.GetRepositoryByName("Multi");
            var recorded = (JsonElement)repository!.Properties!["rustSemanticOutcomes"];
            recorded.GetProperty("groups").GetArrayLength().ShouldBe(2);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_RootWorkspaceWithNestedMembers_AnalyzesOnlyTheRoot()
    {
        var rootPath = CreateRepository();
        try
        {
            WriteWorkspace(rootPath, "", "alpha", "beta");

            var (analyzer, diagnostics, _) = await IndexAsync(rootPath);

            analyzer.Manifests.ShouldBe(["Cargo.toml"]);
            var outcomes = diagnostics.RustSemanticOutcomes.ShouldNotBeNull();
            outcomes.Manifest.ShouldBe("Cargo.toml");
            outcomes.Groups.ShouldHaveSingleItem().Manifest.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_RootPackageAndNestedIndependentWorkspace_AnalyzesBoth()
    {
        var rootPath = CreateRepository();
        try
        {
            WriteCrate(rootPath, "", "root_app");
            WriteWorkspace(rootPath, "tools/codegen", "codegen");

            var (analyzer, _, _) = await IndexAsync(rootPath);

            analyzer.Manifests.Order(StringComparer.Ordinal).ShouldBe(["Cargo.toml", "tools/codegen/Cargo.toml"]);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public void CargoWorkspaceRoots_WorkspaceSubTableAloneDeclaresTheWorkspace()
    {
        var rootPath = CreateRepository();
        try
        {
            WriteCrate(rootPath, "crates/member", "member");
            WriteFile(rootPath, "Cargo.toml", "[workspace.package]\nversion = \"0.1.0\"\n");

            CargoWorkspaceRoots.Find(new LocalFileSystem(),
                    Directory.EnumerateFiles(rootPath, "Cargo.toml", SearchOption.AllDirectories))
                .Select(path => Path.GetRelativePath(rootPath, path).Replace('\\', '/'))
                .ShouldBe(["Cargo.toml"]);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>
    /// A Cargo workspace copied under an agent worktree or a dependency directory is not the
    /// repository's own code: it is neither analyzed (a full SCIP run per copy) nor emitted as
    /// duplicate Package definitions.
    /// </summary>
    [Fact]
    public async Task IndexProjectAsync_WorkspacesUnderExcludedDirectories_AreNeitherAnalyzedNorEmittedAsPackages()
    {
        var rootPath = CreateRepository();
        try
        {
            WriteWorkspace(rootPath, "engine", "engine_core");
            WriteWorkspace(rootPath, ".claude/worktrees/agent-1/engine", "engine_core");
            WriteWorkspace(rootPath, "web/node_modules/native-addon", "addon_core");

            var (analyzer, _, store) = await IndexAsync(rootPath);

            analyzer.Manifests.ShouldBe(["engine/Cargo.toml"]);
            store.Nodes.Where(node => node.Label == NodeLabel.Package)
                .Select(node => node.FilePath).Distinct().Order(StringComparer.Ordinal)
                .ShouldBe(["engine/crates/engine_core/Cargo.toml"]);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>
    /// Package slices of a workspace copy indexed before it was excluded are removed by the next
    /// run, whose changed exclusions alter the extraction fingerprint.
    /// </summary>
    [Fact]
    public async Task IndexProjectAsync_PreviouslyIndexedWorktreeWorkspace_IsRemovedOnceExcluded()
    {
        var rootPath = CreateRepository();
        try
        {
            WriteWorkspace(rootPath, "engine", "engine_core");
            WriteWorkspace(rootPath, ".claude/worktrees/agent-1/engine", "engine_copy");
            var store = new InMemoryGraphStore();
            var previousDefaults = new IndexingOptions().SkipPatterns
                .Where(pattern => pattern != "**/.claude/worktrees/**").ToArray();

            var (before, _, _) = await IndexAsync(rootPath, store, previousDefaults);
            before.Manifests.Count.ShouldBe(2);
            store.Nodes.ShouldContain(node => node.Label == NodeLabel.Package
                && node.FilePath!.StartsWith(".claude/", StringComparison.Ordinal));

            var (after, _, _) = await IndexAsync(rootPath, store);
            after.Manifests.ShouldBe(["engine/Cargo.toml"]);
            store.Nodes.ShouldNotContain(node => node.Label == NodeLabel.Package
                && node.FilePath!.StartsWith(".claude/", StringComparison.Ordinal));
            store.Nodes.ShouldContain(node => node.Label == NodeLabel.Package
                && node.FilePath == "engine/crates/engine_core/Cargo.toml");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static async Task<(RecordingRustAnalyzer Analyzer, IndexingRunDiagnostics Diagnostics, InMemoryGraphStore Store)>
        IndexAsync(string rootPath, InMemoryGraphStore? existingStore = null, string[]? skipPatterns = null)
    {
        var store = existingStore ?? new InMemoryGraphStore();
        var diagnostics = new IndexingRunDiagnostics();
        var analyzer = new RecordingRustAnalyzer(rootPath);
        var options = new IndexingOptions { MaxParallelFiles = 1 };
        if (skipPatterns is not null)
            options.SkipPatterns = skipPatterns;
        var pipeline = new IndexingPipeline(
            store,
            [new CodeGraph.Extractors.TreeSitter.TreeSitterExtractor(
                NullLogger<CodeGraph.Extractors.TreeSitter.TreeSitterExtractor>.Instance)],
            Options.Create(options),
            new LocalFileSystem(),
            NullLogger<IndexingPipeline>.Instance,
            rustAnalyzer: analyzer,
            cargoManifestExtractor: new CargoManifestExtractor(),
            diagnostics: diagnostics);

        await pipeline.IndexProjectAsync("Multi", rootPath, ct: CancellationToken.None);
        return (analyzer, diagnostics, store);
    }

    private static string CreateRepository()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-rust-multi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        return rootPath;
    }

    private static void WriteWorkspace(string rootPath, string workspace, params string[] crates)
    {
        foreach (var crate in crates)
            WriteCrate(rootPath, Path.Combine(workspace, "crates", crate), crate);
        WriteFile(rootPath, Path.Combine(workspace, "Cargo.toml"),
            "[workspace]\nresolver = \"2\"\nmembers = [" +
            string.Join(", ", crates.Select(crate => $"\"crates/{crate}\"")) + "]\n");
    }

    private static void WriteCrate(string rootPath, string directory, string name)
    {
        WriteFile(rootPath, Path.Combine(directory, "Cargo.toml"),
            $"[package]\nname = \"{name}\"\nversion = \"0.1.0\"\nedition = \"2021\"\n");
        WriteFile(rootPath, Path.Combine(directory, "src", "lib.rs"), $"pub fn {name}_value() -> i32 {{ 1 }}\n");
    }

    private static void WriteFile(string rootPath, string relativePath, string content)
    {
        var path = Path.Combine(rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Stands in for cargo metadata + rust-analyzer: each analyzed manifest yields one crate group
    /// holding the crates below it, with one semantic node per crate.
    /// </summary>
    private sealed class RecordingRustAnalyzer(string rootPath) : IRustAnalyzer
    {
        private readonly ConcurrentQueue<string> _manifests = new();

        public IReadOnlyList<string> Manifests => _manifests.ToArray();

        public Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(
            string cargoManifestPath, ExtractorContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RustWorkspaceAnalysis> AnalyzeWorkspaceAsync(
            string cargoManifestPath, ExtractorContext context, CancellationToken ct = default)
        {
            _manifests.Enqueue(Path.GetRelativePath(rootPath, cargoManifestPath).Replace('\\', '/'));
            var workspaceDirectory = Path.GetDirectoryName(cargoManifestPath)!;
            var crateLibs = Directory.EnumerateFiles(workspaceDirectory, "lib.rs", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(rootPath, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToList();
            var crates = crateLibs.Select(CrateName).ToList();
            var result = new ExtractionResult
            {
                Nodes = crateLibs.Select(path => new GraphNode
                {
                    Project = context.ProjectName,
                    Label = NodeLabel.Function,
                    Name = $"semantic_{CrateName(path)}",
                    QualifiedName = $"{context.ProjectName}:semantic_{CrateName(path)}",
                    FilePath = path
                }).ToList(),
                ProcessedFiles = crateLibs,
                Metadata = new ProjectMetadata("Rust", "Cargo")
            };
            return Task.FromResult(new RustWorkspaceAnalysis(
                [new RustCrateGroupAnalysis(crates, [Path.GetRelativePath(rootPath, workspaceDirectory)], [result], null)]));
        }

        private string CrateName(string relativeLibPath)
        {
            var manifest = Path.Combine(rootPath, Path.GetDirectoryName(Path.GetDirectoryName(relativeLibPath))!, "Cargo.toml");
            var line = File.ReadLines(manifest).First(text => text.StartsWith("name = ", StringComparison.Ordinal));
            return line["name = ".Length..].Trim('"');
        }
    }
}
