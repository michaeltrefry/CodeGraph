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
    /// The usual legacy layout: packages.config lives in the project directory, the restore
    /// folder beside the solution holds one <c>&lt;Id&gt;.&lt;Version&gt;</c> directory per package
    /// with its own <c>.nupkg</c>.
    /// </summary>
    [Fact]
    public async Task IndexProjectAsync_RestoredNuGetPackageDirectories_AreExcluded()
    {
        Write("Legacy.sln", "Microsoft Visual Studio Solution File\n");
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

    [Theory]
    [InlineData("packages.config")]
    [InlineData("packages/repositories.config")]
    public async Task IndexProjectAsync_RestoreFolderMarkedAsAWhole_IsExcluded(string marker)
    {
        Write(marker, "<repositories />\n");
        Write("App/Program.cs", "class Program {}\n");
        Write("packages/Some.Package.1.0.0/lib/Some.cs", "class Some {}\n");
        Write("packages/notes/Readme.cs", "class Readme {}\n");

        var (store, _) = await IndexAsync();

        (await store.GetFileHashesAsync("Legacy")).Keys.ShouldBe(["App/Program.cs"]);
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
        var restored = new HashSet<string>(
            ["packages/Newtonsoft.Json.13.0.1/Newtonsoft.Json.13.0.1.nupkg"], StringComparer.Ordinal);

        pipeline.IsIndexablePath("packages/Newtonsoft.Json.13.0.1/content/JsonConfig.cs", restored.Contains)
            .ShouldBeFalse();
        pipeline.IsIndexablePath("packages/Newtonsoft.Json.13.0.1/content/JsonConfig.cs", _ => false)
            .ShouldBeTrue();
        pipeline.IsIndexablePath("packages/notes/Readme.cs", restored.Contains).ShouldBeTrue();
        pipeline.IsIndexablePath("packages/a/src/main.py", _ => false).ShouldBeTrue();
        pipeline.IsIndexablePath("lib/packages/Some.1.0/Some.cs", "lib/packages.config".Equals).ShouldBeFalse();
        pipeline.IsIndexablePath("packages/Some.1.0/Some.cs", "packages/repositories.config".Equals).ShouldBeFalse();
        pipeline.IsIndexablePath("Packages/Core/Sources/Core/Core.swift", "packages.config".Equals).ShouldBeTrue();
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
            pattern.StartsWith("dir:packages?", StringComparison.Ordinal) && pattern.Contains(".nupkg"));
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
