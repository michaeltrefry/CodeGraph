using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// sc-26145: <c>packages/</c> is skipped only as a legacy NuGet (packages.config) restore folder,
/// not by a blanket <c>**/packages/**</c> glob, so JS/TS monorepo <c>packages/*</c> and SwiftPM
/// <c>Packages/</c> are indexed.
/// </summary>
public class NuGetPackagesDirectoryPipelineTests : IDisposable
{
    private readonly string _rootPath =
        Path.Combine(Path.GetTempPath(), $"codegraph-nuget-packages-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    /// <summary>
    /// The per-package rule on its own (no whole-folder signal beside packages/): packages.config
    /// lives in the project directory and each <c>&lt;Id&gt;.&lt;Version&gt;</c> directory holds its
    /// own <c>.nupkg</c>. A solution beside packages/ would skip the whole folder (see below).
    /// </summary>
    [Fact]
    public async Task IndexProjectAsync_RestoredNuGetPackageDirectories_AreExcluded()
    {
        Write("src/App/packages.config", """<packages><package id="Newtonsoft.Json" version="13.0.1" /></packages>""");
        Write("src/App/Program.cs", "class Program {}\n");
        Write("packages/Newtonsoft.Json.13.0.1/Newtonsoft.Json.13.0.1.nupkg", "nupkg");
        Write("packages/Newtonsoft.Json.13.0.1/content/App_Start/JsonConfig.cs", "class JsonConfig {}\n");
        Write("packages/Newtonsoft.Json.13.0.1/tools/install.js", "function install() {}\n");
        // A directory under packages/ without its own .nupkg is not a restored package.
        Write("packages/notes/Readme.cs", "class Readme {}\n");

        var (store, _) = await IndexAsync();

        (await store.GetFileHashesAsync("Legacy")).Keys.Order(StringComparer.Ordinal)
            .ShouldBe(["packages/notes/Readme.cs", "src/App/Program.cs"]);
    }

    /// <summary>
    /// Whole-folder signals. The .NET solution signals (.sln/.slnx, NuGet.Config, .nuget/) cover a
    /// committed restore folder whose *.nupkg files are gitignored (NuGet 3+ with packages.config in
    /// the project directories and no repositories.config).
    /// </summary>
    [Theory]
    [InlineData("packages.config")]
    [InlineData("packages/repositories.config")]
    [InlineData("paket.dependencies")]
    [InlineData("paket.lock")]
    [InlineData("Legacy.sln")]
    [InlineData("Legacy.slnx")]
    [InlineData("NuGet.Config")]
    [InlineData("nuget.config")]
    [InlineData(".nuget/NuGet.Config")]
    [InlineData(".nuget/packages.config")]
    public async Task IndexProjectAsync_RestoreFolderMarkedAsAWhole_IsExcluded(string marker)
    {
        Write(marker, "<repositories />\n");
        Write("App/Program.cs", "class Program {}\n");
        Write("packages/Some.Package.1.0.0/lib/Some.cs", "class Some {}\n");
        Write("packages/notes/Readme.cs", "class Readme {}\n");

        var (store, _) = await IndexAsync();

        (await store.GetFileHashesAsync("Legacy")).Keys.ShouldBe(["App/Program.cs"]);
    }

    /// <summary>Paket restores into packages/&lt;Id&gt;/ holding &lt;Id&gt;.&lt;Version&gt;.nupkg.</summary>
    [Fact]
    public async Task IndexProjectAsync_PaketRestoreFolder_IsExcluded()
    {
        Write("paket.dependencies", "source https://api.nuget.org/v3/index.json\nnuget FSharp.Core\n");
        Write("src/App/Program.cs", "class Program {}\n");
        Write("packages/FSharp.Core/FSharp.Core.4.7.0.nupkg", "nupkg");
        Write("packages/FSharp.Core/content/Prelude.cs", "class Prelude {}\n");

        var (store, _) = await IndexAsync();

        (await store.GetFileHashesAsync("Legacy")).Keys.ShouldBe(["src/App/Program.cs"]);
    }

    /// <summary>Visual Studio restores into Packages/ as well; its package directories are skipped.</summary>
    [Fact]
    public async Task IndexProjectAsync_CapitalisedRestoreFolder_SkipsItsPackageDirectories()
    {
        Write("src/App/Program.cs", "class Program {}\n");
        Write("Packages/jQuery.3.4.1/jQuery.3.4.1.nupkg", "nupkg");
        Write("Packages/jQuery.3.4.1/Content/Scripts/Bundle.cs", "class Bundle {}\n");
        Write("Packages/Core/Sources/Core/Core.cs", "class Core {}\n");

        var (store, _) = await IndexAsync();

        (await store.GetFileHashesAsync("Legacy")).Keys.Order(StringComparer.Ordinal)
            .ShouldBe(["Packages/Core/Sources/Core/Core.cs", "src/App/Program.cs"]);
    }

    /// <summary>
    /// Precedence: a solution beside packages/ is weaker evidence than JS workspace packages in it.
    /// packages/ whose children hold package.json and no .nupkg stays indexed; one restored
    /// package (a .nupkg) makes it the restore folder again.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexProjectAsync_SolutionBesideAJsMonorepo_KeepsTheWorkspacePackages(bool withRestoredPackage)
    {
        Write("Tools.sln", "Microsoft Visual Studio Solution File\n");
        Write("package.json", """{ "name": "mono", "workspaces": ["packages/*"] }""");
        Write("packages/a/package.json", """{ "name": "@mono/a" }""");
        Write("packages/a/src/main.py", "def main(): pass\n");
        Write("packages/b/package.json", """{ "name": "@mono/b" }""");
        Write("packages/b/tool.py", "def tool(): pass\n");
        if (withRestoredPackage)
            Write("packages/Some.Package.1.0.0/Some.Package.1.0.0.nupkg", "nupkg");

        var (store, _) = await IndexAsync();

        var hashes = (await store.GetFileHashesAsync("Legacy")).Keys.Order(StringComparer.Ordinal).ToList();
        if (withRestoredPackage)
            hashes.ShouldBeEmpty();
        else
            hashes.ShouldBe(["packages/a/src/main.py", "packages/b/tool.py"]);
    }

    [Fact]
    public async Task IndexProjectAsync_SwiftPackagesDirectory_IsIndexedAndLinksTheLocalPackage()
    {
        Write("Package.swift", """
            // swift-tools-version:5.9
            import PackageDescription
            let package = Package(name: "ShapeKit", dependencies: [.package(path: "Packages/Core")])
            """);
        Write("Packages/Core/Package.swift", """
            // swift-tools-version:5.9
            import PackageDescription
            let package = Package(name: "Core", targets: [.target(name: "Core")])
            """);
        Write("Packages/Core/Sources/Core/Core.swift", "public struct Core {}\n");
        // Also beside a packages.config: the rule matches the exact name `packages` only.
        Write("packages.config", "<packages />\n");

        var (store, _) = await IndexAsync("ShapeKit");

        (await store.GetFileHashesAsync("ShapeKit")).Keys
            .ShouldContain("Packages/Core/Sources/Core/Core.swift");
        var packages = store.Nodes.Where(node => node.Label == NodeLabel.Package).ToList();
        var core = packages.Single(node => node.Name == "Core");
        core.FilePath.ShouldBe("Packages/Core/Package.swift");
        var shapeKit = packages.Single(node => node.Name == "ShapeKit");
        store.Edges.ShouldContain(edge => edge.SourceId == shapeKit.Id && edge.TargetId == core.Id
                                          && edge.Type == EdgeType.REFERENCES_PACKAGE);
    }

    [Fact]
    public void IsIndexablePath_AppliesTheNuGetRuleAgainstTheOverlayTree()
    {
        var pipeline = CreatePipeline(new InMemoryGraphStore());
        static RepositoryTree Tree(params string[] files) => RepositoryTree.FromFiles(files);
        const string content = "packages/Newtonsoft.Json.13.0.1/content/JsonConfig.cs";
        var restored = Tree("packages/Newtonsoft.Json.13.0.1/Newtonsoft.Json.13.0.1.nupkg", content,
            "packages/notes/Readme.cs");

        pipeline.IsIndexablePath(content, restored).ShouldBeFalse();
        pipeline.IsIndexablePath(content, Tree(content)).ShouldBeTrue();
        pipeline.IsIndexablePath("packages/notes/Readme.cs", restored).ShouldBeTrue();
        pipeline.IsIndexablePath("packages/a/src/main.py", Tree()).ShouldBeTrue();
        pipeline.IsIndexablePath("lib/packages/Some.1.0/Some.cs", Tree("lib/packages.config")).ShouldBeFalse();
        pipeline.IsIndexablePath("packages/Some.1.0/Some.cs", Tree("packages/repositories.config")).ShouldBeFalse();
        pipeline.IsIndexablePath("packages/Some/Some.cs", Tree("paket.lock")).ShouldBeFalse();
        pipeline.IsIndexablePath("Packages/Core/Sources/Core/Core.swift", Tree("packages.config")).ShouldBeTrue();

        // Visual Studio's Packages/: restored package directories are skipped, a SwiftPM package is not.
        pipeline.IsIndexablePath("Packages/jQuery.3.4.1/Content/Bundle.cs",
            Tree("Packages/jQuery.3.4.1/jQuery.3.4.1.nupkg")).ShouldBeFalse();
        pipeline.IsIndexablePath("Packages/Core/Sources/Core/Core.swift",
            Tree("Packages/jQuery.3.4.1/jQuery.3.4.1.nupkg")).ShouldBeTrue();

        // A solution beside packages/ (gitignored .nupkg files), unless packages/ holds JS workspaces.
        pipeline.IsIndexablePath("packages/Some.1.0/lib/Some.cs",
            Tree("App.sln", "packages/Some.1.0/lib/Some.cs")).ShouldBeFalse();
        pipeline.IsIndexablePath("packages/Some.1.0/lib/Some.cs",
            Tree(".nuget/NuGet.Config", "packages/Some.1.0/lib/Some.cs")).ShouldBeFalse();
        pipeline.IsIndexablePath("packages/a/src/main.py",
            Tree("App.sln", "packages/a/package.json", "packages/a/src/main.py")).ShouldBeTrue();
        pipeline.IsIndexablePath("packages/a/src/main.py",
            Tree("App.sln", "packages/a/package.json", "packages/a/src/main.py",
                "packages/X.1.0/X.1.0.nupkg")).ShouldBeFalse();
        // No .NET signal at all: a packages/ directory is the repository's own code.
        pipeline.IsIndexablePath("packages/Some.1.0/lib/Some.cs", Tree("packages/Some.1.0/lib/Some.cs"))
            .ShouldBeTrue();
    }

    [Fact]
    public void ExtractionFingerprint_ChangesWithTheNuGetRule()
    {
        var current = CreatePipeline(new InMemoryGraphStore());
        var withOldGlob = CreatePipeline(new InMemoryGraphStore(),
            [.. new IndexingOptions().SkipPatterns, "**/packages/**"]);

        current.ExtractionFingerprint.ShouldNotBe(withOldGlob.ExtractionFingerprint);
        // The rule itself is fingerprint text, so a later change to it also refreshes repositories.
        current.DiscoveryExcludePatterns.ShouldContain(pattern =>
            pattern.StartsWith("dir:packages?", StringComparison.Ordinal) && pattern.Contains(".nupkg")
            && pattern.Contains("paket.lock") && pattern.Contains("*.sln") && pattern.Contains(".nuget/NuGet.Config")
            && pattern.Contains("package.json"));
    }

    private async Task<(InMemoryGraphStore Store, IndexingPipeline Pipeline)> IndexAsync(string project = "Legacy")
    {
        var store = new InMemoryGraphStore();
        var pipeline = CreatePipeline(store);
        await pipeline.IndexProjectAsync(project, _rootPath, ct: CancellationToken.None);
        return (store, pipeline);
    }

    private static IndexingPipeline CreatePipeline(InMemoryGraphStore store, string[]? skipPatterns = null) =>
        new(
            store,
            [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance), new NoOpCSharpExtractor()],
            Options.Create(skipPatterns is null
                ? new IndexingOptions { MaxParallelFiles = 1 }
                : new IndexingOptions { MaxParallelFiles = 1, SkipPatterns = skipPatterns }),
            new LocalFileSystem(),
            NullLogger<IndexingPipeline>.Instance,
            swiftPackageManifestExtractor: new SwiftPackageManifestExtractor());

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Registers .cs for discovery without needing Roslyn.</summary>
    private sealed class NoOpCSharpExtractor : CodeGraph.Services.Extractors.ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".cs" };

        public Task<ExtractionResult> ExtractAsync(string filePath, string content,
            CodeGraph.Services.Extractors.ExtractorContext context, CancellationToken ct = default) =>
            Task.FromResult(new ExtractionResult());
    }
}
