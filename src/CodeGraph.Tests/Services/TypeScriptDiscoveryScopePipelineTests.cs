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
/// sc-26145 (LensWorks): the TypeScript sidecar discovered tsconfig/workspace packages itself and
/// indexed 17 <c>.claude/worktrees/*</c> copies of the repository (Class/Method nodes and stub
/// Interfaces with no File/file_hash rows, so incremental cleanup never removed them), and a
/// vendored copy of a workspace package took over the in-repo package's Module node.
/// </summary>
public class TypeScriptDiscoveryScopePipelineTests
{
    private const string Project = "Lens";

    [Fact]
    public async Task IndexProjectAsync_HandsTheSidecarOnlyTheFilesDiscoveryAdmits()
    {
        var rootPath = CreateRepository();
        try
        {
            WriteApp(rootPath);
            WriteApp(rootPath, ".claude/worktrees/agent-1/");
            WriteVendoredCopy(rootPath);

            var analyzer = new FakeTypeScriptAnalyzer(rootPath, honourScope: true);
            await IndexAsync(rootPath, new InMemoryGraphStore(), analyzer);

            analyzer.Tsconfigs.ShouldBe(["tsconfig.json"]);
            analyzer.IncludedFiles.ShouldHaveSingleItem().ShouldBe([
                "apps/web/package.json",
                "apps/web/src/widget.ts",
                "package.json",
                "src/app.ts",
                "tsconfig.json"
            ]);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>
    /// sc-26199: in a git working copy the sidecar scope and the clamp leave out a gitignored
    /// vendored package and a submodule's working tree.
    /// </summary>
    [Fact]
    public async Task IndexProjectAsync_GitWorkingCopy_LeavesIgnoredPathsAndSubmodulesOutOfTheScope()
    {
        var rootPath = CreateRepository();
        try
        {
            WriteApp(rootPath);
            WriteFile(rootPath, ".gitignore", "vendor/\n");
            WriteFile(rootPath, "vendor/web/package.json", """{ "name": "@lens/web" }""");
            WriteFile(rootPath, "vendor/web/src/widget.ts", "export class Widget {}\n");
            var submodule = Path.Combine(rootPath, "modules", "compositor");
            WriteFile(submodule, "package.json", """{ "name": "@lens/compositor" }""");
            WriteFile(submodule, "src/compose.ts", "export class Compose {}\n");
            GitWorkingCopyDiscoveryTests.Git(submodule, "init", "-q");
            GitWorkingCopyDiscoveryTests.Git(submodule, "add", ".");
            GitWorkingCopyDiscoveryTests.Commit(submodule);
            GitWorkingCopyDiscoveryTests.Git(rootPath, "init", "-q");
            GitWorkingCopyDiscoveryTests.Git(rootPath, "add", ".");
            GitWorkingCopyDiscoveryTests.Commit(rootPath);

            var analyzer = new FakeTypeScriptAnalyzer(rootPath, honourScope: true);
            await IndexAsync(rootPath, new InMemoryGraphStore(), analyzer);
            analyzer.IncludedFiles.ShouldHaveSingleItem().ShouldBe([
                "apps/web/package.json",
                "apps/web/src/widget.ts",
                "package.json",
                "src/app.ts",
                "tsconfig.json"
            ]);

            var store = new InMemoryGraphStore();
            await IndexAsync(rootPath, store, new FakeTypeScriptAnalyzer(rootPath, honourScope: false));
            store.Nodes.ShouldNotContain(node => node.FilePath != null
                && (node.FilePath.StartsWith("vendor") || node.FilePath.StartsWith("modules")));
            store.Nodes.ShouldContain(node => node.Label == NodeLabel.Module && node.FilePath == "apps/web");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>A pnpm monorepo's packages/* is the repository's own code, not a NuGet restore folder.</summary>
    [Fact]
    public async Task IndexProjectAsync_PnpmWorkspacePackages_AreIndexedWithTheirModules()
    {
        var rootPath = CreateRepository();
        try
        {
            WriteFile(rootPath, "package.json", """{ "name": "mono", "workspaces": ["packages/*"] }""");
            WriteFile(rootPath, "pnpm-workspace.yaml", "packages:\n  - 'packages/*'\n");
            WriteFile(rootPath, "tsconfig.json", "{}");
            WriteFile(rootPath, "packages/a/package.json", """{ "name": "@mono/a" }""");
            WriteFile(rootPath, "packages/a/src/index.ts", "export class Index {}\n");
            var store = new InMemoryGraphStore();
            var analyzer = new FakeTypeScriptAnalyzer(rootPath, honourScope: true);

            await IndexAsync(rootPath, store, analyzer);

            analyzer.IncludedFiles.ShouldHaveSingleItem()
                .ShouldBe(["package.json", "packages/a/package.json", "packages/a/src/index.ts", "tsconfig.json"]);
            (await store.GetFileHashesAsync(Project)).Keys.ShouldContain("packages/a/src/index.ts");
            store.Nodes.ShouldContain(node => node.Label == NodeLabel.Module
                && node.QualifiedName == $"typescript-package:{Project}:@mono/a" && node.FilePath == "packages/a");
            store.Nodes.ShouldContain(node => node.QualifiedName == "packages.a.src.index.Index"
                && node.FilePath == "packages/a/src/index.ts");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task IndexProjectAsync_WithoutARootTsconfig_NeverPicksAnExcludedOne()
    {
        var rootPath = CreateRepository();
        try
        {
            WriteFile(rootPath, ".claude/worktrees/agent-1/tsconfig.json", "{}");
            WriteFile(rootPath, "a/node_modules/lib/tsconfig.json", "{}");
            // Shallower and ordinally first, but a SwiftPM build directory.
            WriteFile(rootPath, ".build/tsconfig.json", "{}");
            WriteFile(rootPath, "web/tsconfig.json", "{}");
            WriteFile(rootPath, "web/src/app.ts", "export class App {}\n");

            var analyzer = new FakeTypeScriptAnalyzer(rootPath, honourScope: true);
            await IndexAsync(rootPath, new InMemoryGraphStore(), analyzer);

            analyzer.Tsconfigs.ShouldBe(["web/tsconfig.json"]);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>A sidecar that reads more than it was given cannot put those files in the graph.</summary>
    [Fact]
    public async Task IndexProjectAsync_SidecarOutputUnderExcludedPaths_IsDropped()
    {
        var rootPath = CreateRepository();
        try
        {
            WriteApp(rootPath);
            WriteApp(rootPath, ".claude/worktrees/agent-1/");
            WriteVendoredCopy(rootPath);
            var store = new InMemoryGraphStore();

            await IndexAsync(rootPath, store, new FakeTypeScriptAnalyzer(rootPath, honourScope: false));

            store.Nodes.Where(node => node.Label is NodeLabel.Class or NodeLabel.Module)
                .Select(node => $"{node.Label}:{node.QualifiedName}:{node.FilePath}")
                .Order(StringComparer.Ordinal)
                .ShouldBe([
                    "Class:apps.web.src.widget.Widget:apps/web/src/widget.ts",
                    "Class:src.app.App:src/app.ts",
                    $"Module:typescript-package:{Project}:@lens/app:",
                    $"Module:typescript-package:{Project}:@lens/web:apps/web"
                ]);
            // Edges of dropped worktree nodes create no stubs either.
            store.Nodes.ShouldNotContain(node => node.QualifiedName.Contains(".claude", StringComparison.Ordinal));
            store.Nodes.ShouldNotContain(node => node.QualifiedName.Contains("node_modules", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>
    /// An index written by the earlier sidecar holds worktree/vendored TypeScript slices with no
    /// file hash rows, their stubs, and a Module node pointing at a vendored copy; the next
    /// incremental run removes them and restores the in-repo packages.
    /// </summary>
    [Fact]
    public async Task IndexProjectAsync_PreviouslyIndexedExcludedTypeScriptSlices_AreRemovedOnTheNextRun()
    {
        var rootPath = CreateRepository();
        try
        {
            WriteApp(rootPath);
            WriteApp(rootPath, ".claude/worktrees/agent-1/");
            WriteVendoredCopy(rootPath);
            var store = new InMemoryGraphStore();
            var analyzer = new FakeTypeScriptAnalyzer(rootPath, honourScope: true);
            await IndexAsync(rootPath, store, analyzer);
            await SeedLegacySidecarOutputAsync(store);

            await IndexAsync(rootPath, store, analyzer);

            store.Nodes.ShouldNotContain(node => node.FilePath != null
                && (node.FilePath.StartsWith(".claude/", StringComparison.Ordinal)
                    || node.FilePath.Contains("node_modules", StringComparison.Ordinal)));
            store.Nodes.ShouldNotContain(node => node.QualifiedName == LegacyStubQn);
            store.Nodes.Where(node => node.Label == NodeLabel.Module)
                .Select(node => $"{node.QualifiedName}:{node.FilePath}")
                .Order(StringComparer.Ordinal)
                .ShouldBe([
                    $"typescript-package:{Project}:@lens/app:",
                    $"typescript-package:{Project}:@lens/web:apps/web"
                ]);
            // The restored modules keep their containment edges.
            var moduleIds = store.Nodes.Where(node => node.Label == NodeLabel.Module).Select(node => node.Id).ToHashSet();
            store.Edges.Count(edge => edge.Type == EdgeType.CONTAINS_PROJECT && moduleIds.Contains(edge.TargetId))
                .ShouldBe(2);
            store.Nodes.ShouldContain(node => node.QualifiedName == "src.app.App" && node.FilePath == "src/app.ts");
            // Only TypeScript slices are swept: an excluded non-TypeScript slice is left alone.
            store.Nodes.ShouldContain(node => node.FilePath == "obj/Generated.cs");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>
    /// A TypeScript extraction version bump re-extracts every file; stubs named after the old
    /// checkout-specific <c>import("/abs/path")</c> type text lose their last edge and are swept.
    /// </summary>
    [Fact]
    public async Task IndexProjectAsync_TypeScriptVersionBump_SweepsStubsOfTheOldTypeText()
    {
        var rootPath = CreateRepository();
        try
        {
            WriteApp(rootPath);
            var store = new InMemoryGraphStore();
            const string absoluteTarget = "import(\"/private/tmp/old-checkout/src/errors\").AppError";
            const string relativeTarget = "import(\"src/errors\").AppError";

            await IndexAsync(rootPath, store,
                new FakeTypeScriptAnalyzer(rootPath, honourScope: true, version: "1", injects: absoluteTarget));
            store.Nodes.ShouldContain(node => node.QualifiedName == absoluteTarget);

            await IndexAsync(rootPath, store,
                new FakeTypeScriptAnalyzer(rootPath, honourScope: true, version: "2", injects: relativeTarget));

            store.Nodes.ShouldNotContain(node => node.QualifiedName == absoluteTarget);
            store.Nodes.ShouldContain(node => node.QualifiedName == relativeTarget);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>
    /// With no admitted tsconfig the sidecar never runs, yet stored workspace modules (one keyed
    /// by an excluded directory, one root module) are stale and removed.
    /// </summary>
    [Fact]
    public async Task IndexProjectAsync_NoAdmittedTsconfig_RemovesStoredWorkspaceModules()
    {
        var rootPath = CreateRepository();
        try
        {
            WriteFile(rootPath, ".claude/worktrees/agent-1/tsconfig.json", "{}");
            WriteFile(rootPath, "src/app.ts", "export class App {}\n");
            var store = new InMemoryGraphStore();
            var analyzer = new FakeTypeScriptAnalyzer(rootPath, honourScope: true);
            await IndexAsync(rootPath, store, analyzer);
            await SeedWorkspaceModulesAsync(store,
                ($"typescript-package:{Project}:@lens/vendored", "web/node_modules/vendored"),
                ($"typescript-package:{Project}:@lens/app", null));

            await IndexAsync(rootPath, store, analyzer);

            analyzer.Tsconfigs.ShouldBeEmpty();
            store.Nodes.ShouldNotContain(node => node.Label == NodeLabel.Module);
            store.Nodes.ShouldContain(node => node.QualifiedName == Project && node.Label == NodeLabel.Repository);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>An unavailable sidecar (no result) says nothing about the workspace packages.</summary>
    [Fact]
    public async Task IndexProjectAsync_SidecarUnavailable_KeepsTheWorkspaceModules()
    {
        var rootPath = CreateRepository();
        try
        {
            WriteApp(rootPath);
            var store = new InMemoryGraphStore();
            await IndexAsync(rootPath, store, new FakeTypeScriptAnalyzer(rootPath, honourScope: true));
            File.AppendAllText(Path.Combine(rootPath, "src/app.ts"), "// changed\n");
            // A module under an excluded directory is stale whether or not the sidecar runs.
            await SeedWorkspaceModulesAsync(store,
                ($"typescript-package:{Project}:@lens/vendored", "web/node_modules/vendored"));

            await IndexAsync(rootPath, store, new FakeTypeScriptAnalyzer(rootPath, honourScope: true, unavailable: true));

            store.Nodes.Where(node => node.Label == NodeLabel.Module)
                .Select(node => $"{node.QualifiedName}:{node.FilePath}")
                .Order(StringComparer.Ordinal)
                .ShouldBe([
                    $"typescript-package:{Project}:@lens/app:",
                    $"typescript-package:{Project}:@lens/web:apps/web"
                ]);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static async Task SeedWorkspaceModulesAsync(
        InMemoryGraphStore store, params (string QualifiedName, string? FilePath)[] modules)
    {
        await store.UpsertNodeBatchAsync(modules.Select(module => Node(NodeLabel.Module, module.QualifiedName,
            module.FilePath, new() { ["module_kind"] = "typescript_workspace_package" })).ToList());
        var repository = store.Nodes.Single(node => node.QualifiedName == Project).Id;
        foreach (var module in modules)
        {
            await store.InsertEdgeAsync(new GraphEdge
            {
                Project = Project,
                SourceId = repository,
                TargetId = store.Nodes.Single(node => node.QualifiedName == module.QualifiedName).Id,
                Type = EdgeType.CONTAINS_PROJECT
            });
        }
    }

    private const string LegacyStubQn = "import(\"/private/tmp/lens/.claude/worktrees/agent-1/src/app\").Cleanup";

    /// <summary>What the earlier sidecar left in the graph: no File node, no file hash row.</summary>
    private static async Task SeedLegacySidecarOutputAsync(InMemoryGraphStore store)
    {
        await store.UpsertNodeBatchAsync([
            Node(NodeLabel.Class, ".claude.worktrees.agent-1.src.app.App", ".claude/worktrees/agent-1/src/app.ts"),
            Node(NodeLabel.Method, ".claude.worktrees.agent-1.src.app.App.run", ".claude/worktrees/agent-1/src/app.ts"),
            Node(NodeLabel.Class, "web.node_modules.vendored.src.widget.Widget", "web/node_modules/vendored/src/widget.ts"),
            Node(NodeLabel.Interface, LegacyStubQn, null, new() { ["stub"] = true }),
            Node(NodeLabel.Class, "Generated", "obj/Generated.cs"),
        ]);
        long Id(string qualifiedName) => store.Nodes.Single(node => node.QualifiedName == qualifiedName).Id;
        await store.InsertEdgeAsync(new GraphEdge
        {
            Project = Project,
            SourceId = Id(".claude.worktrees.agent-1.src.app.App"),
            TargetId = Id(LegacyStubQn),
            Type = EdgeType.INJECTS
        });

        // Vendored copies took over the Module nodes of the in-repo @lens/web package and of the
        // root package (same qualified names, the vendored directories as file paths).
        foreach (var (qualifiedName, vendoredPath) in new[]
                 {
                     ($"typescript-package:{Project}:@lens/web", "web/node_modules/vendored"),
                     ($"typescript-package:{Project}:@lens/app", "vendor/node_modules/app")
                 })
        {
            var module = store.Nodes.Single(node => node.QualifiedName == qualifiedName);
            await store.UpsertNodeAsync(module with { FilePath = vendoredPath });
        }
    }

    private static GraphNode Node(NodeLabel label, string qualifiedName, string? filePath,
        Dictionary<string, object>? properties = null) => new()
    {
        Project = Project,
        Label = label,
        Name = qualifiedName[(qualifiedName.LastIndexOf('.') + 1)..],
        QualifiedName = qualifiedName,
        FilePath = filePath,
        Properties = properties ?? []
    };

    private static async Task IndexAsync(string rootPath, InMemoryGraphStore store, FakeTypeScriptAnalyzer analyzer)
    {
        var pipeline = new IndexingPipeline(
            store,
            [new NoOpTypeScriptExtractor()],
            Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
            new LocalFileSystem(),
            NullLogger<IndexingPipeline>.Instance,
            typeScriptAnalyzer: analyzer);
        await pipeline.IndexProjectAsync(Project, rootPath, ct: CancellationToken.None);
    }

    private static void WriteApp(string rootPath, string prefix = "")
    {
        WriteFile(rootPath, $"{prefix}package.json", """{ "name": "@lens/app" }""");
        WriteFile(rootPath, $"{prefix}tsconfig.json", """{ "include": ["**/*.ts"] }""");
        WriteFile(rootPath, $"{prefix}src/app.ts", "export class App { run() {} }\n");
        WriteFile(rootPath, $"{prefix}apps/web/package.json", """{ "name": "@lens/web" }""");
        WriteFile(rootPath, $"{prefix}apps/web/src/widget.ts", "export class Widget {}\n");
    }

    /// <summary>A gitignored vendored copy of the in-repo @lens/web package.</summary>
    private static void WriteVendoredCopy(string rootPath)
    {
        WriteFile(rootPath, "web/node_modules/vendored/package.json", """{ "name": "@lens/web" }""");
        WriteFile(rootPath, "web/node_modules/vendored/src/widget.ts", "export class Widget {}\n");
    }

    private static string CreateRepository()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-ts-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        return rootPath;
    }

    private static void WriteFile(string rootPath, string relativePath, string content)
    {
        var path = Path.Combine(rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Registers .ts for discovery; the project analyzer covers every file.</summary>
    private sealed class NoOpTypeScriptExtractor : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".ts" };

        public Task<ExtractionResult> ExtractAsync(string filePath, string content,
            ExtractorContext context, CancellationToken ct = default) =>
            Task.FromResult(new ExtractionResult());
    }

    /// <summary>
    /// Stands in for the Node.js sidecar: one Class per .ts file and one workspace package Module
    /// per package.json (keyed by its directory, the root package without one). With
    /// <c>honourScope: false</c> it behaves like the earlier sidecar and walks the whole disk.
    /// </summary>
    private sealed class FakeTypeScriptAnalyzer(
        string rootPath, bool honourScope, string version = "2", string? injects = null, bool unavailable = false)
        : ITypeScriptAnalyzer
    {
        public List<string> Tsconfigs { get; } = [];
        public List<IReadOnlyList<string>> IncludedFiles { get; } = [];
        public string ExtractionVersion => version;

        public Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(
            string tsconfigPath, ExtractorContext context, IReadOnlyList<string> includedFiles,
            CancellationToken ct = default)
        {
            Tsconfigs.Add(Relative(tsconfigPath));
            IncludedFiles.Add(includedFiles);
            if (unavailable)
                return Task.FromResult<IReadOnlyList<ExtractionResult>>([]);
            var files = honourScope
                ? includedFiles
                : Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).Select(Relative).ToList();

            var nodes = new List<GraphNode>();
            var edges = new List<PendingEdge>();
            var sources = files.Where(file => file.EndsWith(".ts", StringComparison.Ordinal)).ToList();
            foreach (var file in sources)
            {
                var className = Path.GetFileNameWithoutExtension(file) switch
                {
                    "app" => "App",
                    var name => char.ToUpperInvariant(name[0]) + name[1..]
                };
                var qualifiedName = $"{file[..^3].Replace('/', '.')}.{className}";
                nodes.Add(Node(NodeLabel.Class, qualifiedName, file));
                if (injects is not null)
                    edges.Add(new PendingEdge(qualifiedName, injects, EdgeType.INJECTS));
            }

            foreach (var manifest in files.Where(file => Path.GetFileName(file) == "package.json"))
            {
                var name = JsonDocument.Parse(File.ReadAllText(Path.Combine(rootPath, manifest)))
                    .RootElement.GetProperty("name").GetString()!;
                var qualifiedName = $"typescript-package:{context.ProjectName}:{name}";
                nodes.Add(Node(NodeLabel.Module, qualifiedName,
                    Path.GetDirectoryName(manifest)!.Replace('\\', '/'),
                    new() { ["module_kind"] = "typescript_workspace_package" }));
                edges.Add(new PendingEdge(context.ProjectName, qualifiedName, EdgeType.CONTAINS_PROJECT));
            }

            return Task.FromResult<IReadOnlyList<ExtractionResult>>([
                new ExtractionResult
                {
                    Nodes = nodes,
                    Edges = edges,
                    ProcessedFiles = sources,
                    Metadata = new ProjectMetadata("TypeScript", "Node.js")
                }
            ]);
        }

        private string Relative(string path) => Path.GetRelativePath(rootPath, path).Replace('\\', '/');
    }
}
