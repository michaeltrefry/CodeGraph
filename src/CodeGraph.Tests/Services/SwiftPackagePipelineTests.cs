using System.Text.Json;
using CodeGraph.Extractors.TreeSitter;
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

public class SwiftPackagePipelineTests : IDisposable
{
    private const string XcodeResolvedPath =
        "native/Compositor.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved";

    private readonly string _rootPath =
        Path.Combine(Path.GetTempPath(), $"codegraph-swiftpm-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    [Fact]
    public async Task IndexProjectAsync_XcodeRepository_ClassifiesSwiftXcodeAndPersistsResolvedPins()
    {
        Write("native/Compositor.xcodeproj/project.pbxproj", "// !$*UTF8*$!\n{}\n");
        Write("native/Compositor.xcodeproj/project.xcworkspace/contents.xcworkspacedata", "<Workspace/>\n");
        Write(XcodeResolvedPath, Resolved("2.10.0", "eef1a539a373c1f1a320624b1130fc5de7b2e100"));
        // E5: a manifest that fails to parse is skipped without failing the run.
        Write("native/Other.xcworkspace/xcshareddata/swiftpm/Package.resolved", "{ not json");
        Write("native/Other.xcworkspace/contents.xcworkspacedata", "<Workspace/>\n");
        Write("native/Sources/App.swift", "struct App {}\n");

        var store = new InMemoryGraphStore();
        await CreatePipeline(store).IndexProjectAsync("SwiftApp", _rootPath, ct: CancellationToken.None);

        var repo = await store.GetRepositoryByName("SwiftApp");
        repo.ShouldNotBeNull();
        repo.Language.ShouldBe("Swift");
        repo.Framework.ShouldBe("Xcode");

        var sparkle = store.Nodes.Where(node => node.Label == NodeLabel.Package).ShouldHaveSingleItem();
        sparkle.Name.ShouldBe("sparkle");
        sparkle.FilePath.ShouldBe(XcodeResolvedPath);
        sparkle.Properties["location"].ToString().ShouldBe("https://github.com/sparkle-project/Sparkle");
        sparkle.Properties["version"].ToString().ShouldBe("2.10.0");
        sparkle.Properties["revision"].ToString().ShouldBe("eef1a539a373c1f1a320624b1130fc5de7b2e100");
        var repositoryNode = store.Nodes.Single(node =>
            node.Label == NodeLabel.Repository && node.Project == "SwiftApp");
        store.Edges.ShouldContain(edge =>
            edge.SourceId == repositoryNode.Id &&
            edge.TargetId == sparkle.Id &&
            edge.Type == EdgeType.REFERENCES_PACKAGE);
    }

    [Fact]
    public async Task IndexProjectAsync_PackageSwiftOnly_ClassifiesSwiftPMAndIgnoresBuildCheckouts()
    {
        Write("Package.swift", """
            // swift-tools-version:5.9
            import PackageDescription
            let package = Package(
                name: "ShapeKit",
                dependencies: [.package(url: "https://github.com/apple/swift-log.git", from: "1.5.0")]
            )
            """);
        Write("Package.resolved", Resolved("1.5.3", "532d8b529501fb73a2455b179e0bbb6d49b652ed",
            identity: "swift-log", location: "https://github.com/apple/swift-log.git"));
        Write("Sources/ShapeKit/Shape.swift", "public struct Shape {}\n");
        // SwiftPM build output and Xcode's generated package workspace are not part of the repo's packages.
        Write(".build/checkouts/swift-log/Package.swift",
            "let package = Package(name: \"Logging\", dependencies: [.package(url: \"https://example.test/x\", from: \"1.0.0\")])\n");
        Write(".build/checkouts/swift-log/Package.resolved", Resolved("9.9.9", "ffff", identity: "x",
            location: "https://example.test/x"));
        Write(".swiftpm/xcode/package.xcworkspace/contents.xcworkspacedata", "<Workspace/>\n");
        Write("DerivedData/App/SourcePackages/checkouts/y/Package.swift", "let package = Package(name: \"Y\")\n");

        var store = new InMemoryGraphStore();
        await CreatePipeline(store).IndexProjectAsync("ShapeKit", _rootPath, ct: CancellationToken.None);

        var repo = await store.GetRepositoryByName("ShapeKit");
        repo.ShouldNotBeNull();
        repo.Language.ShouldBe("Swift");
        repo.Framework.ShouldBe("SwiftPM");

        var packages = store.Nodes.Where(node => node.Label == NodeLabel.Package).ToList();
        packages.Select(node => node.QualifiedName).ShouldBe([
            "package:swiftpm:local:shapekit:shapekit@*",
            "package:swiftpm:scm:github.com/apple/swift-log@upToNextMajor:1.5.0",
            "package:swiftpm:scm:github.com/apple/swift-log@1.5.3"
        ], ignoreOrder: true);
        var definition = packages.Single(node => node.Name == "ShapeKit");
        var declared = packages.Single(node => node.QualifiedName.EndsWith("upToNextMajor:1.5.0", StringComparison.Ordinal));
        store.Edges.ShouldContain(edge =>
            edge.SourceId == definition.Id && edge.TargetId == declared.Id &&
            edge.Type == EdgeType.REFERENCES_PACKAGE);
    }

    [Fact]
    public async Task IndexProjectAsync_MixedSwiftAndRust_KeepsBothLanguagesAndPicksTheDominantOne()
    {
        Write("native/Compositor.xcodeproj/project.pbxproj", "// !$*UTF8*$!\n{}\n");
        Write(XcodeResolvedPath, Resolved("2.10.0", "eef1a539a373c1f1a320624b1130fc5de7b2e100"));
        Write("native/Sources/App.swift", "struct App {}\n");
        Write("Cargo.toml", "[package]\nname = \"engine\"\nversion = \"0.1.0\"\n");
        Write("src/lib.rs", string.Concat(Enumerable.Range(0, 20).Select(i => $"pub fn f{i}() {{}}\n")));

        var store = new InMemoryGraphStore();
        await CreatePipeline(store).IndexProjectAsync("Mixed", _rootPath, ct: CancellationToken.None);

        var repo = await store.GetRepositoryByName("Mixed");
        repo.ShouldNotBeNull();
        var languageStats = (JsonElement)repo.Properties!["languageStats"];
        languageStats.GetProperty("Swift").GetProperty("files").GetInt32().ShouldBe(1);
        languageStats.GetProperty("Rust").GetProperty("files").GetInt32().ShouldBe(1);
        // Rust has more non-blank lines, so the Cargo metadata wins; Swift stays in the stats.
        repo.Language.ShouldBe("Rust");
        repo.Framework.ShouldBe("Cargo");
        store.Nodes.ShouldContain(node => node.Label == NodeLabel.Package && node.Name == "sparkle");
        store.Nodes.ShouldContain(node => node.Label == NodeLabel.Package && node.Name == "engine");

        // With Swift dominant the same repository is Swift/Xcode.
        Write("native/Sources/More.swift",
            string.Concat(Enumerable.Range(0, 40).Select(i => $"func g{i}() {{}}\n")));
        var swiftStore = new InMemoryGraphStore();
        await CreatePipeline(swiftStore).IndexProjectAsync("Mixed", _rootPath, ct: CancellationToken.None);
        var swiftRepo = await swiftStore.GetRepositoryByName("Mixed");
        swiftRepo.ShouldNotBeNull();
        swiftRepo.Language.ShouldBe("Swift");
        swiftRepo.Framework.ShouldBe("Xcode");
        ((JsonElement)swiftRepo.Properties!["languageStats"]).TryGetProperty("Rust", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task IndexProjectAsync_IncrementalRun_ReplacesChangedResolvedPins()
    {
        Write(XcodeResolvedPath, Resolved("2.10.0", "eef1a539a373c1f1a320624b1130fc5de7b2e100"));
        Write("native/Compositor.xcodeproj/project.pbxproj", "{}\n");
        Write("native/Sources/App.swift", "struct App {}\n");
        var store = new InMemoryGraphStore();
        await CreatePipeline(store).IndexProjectAsync("SwiftApp", _rootPath, ct: CancellationToken.None);

        Write(XcodeResolvedPath, Resolved("2.11.0", "0000000000000000000000000000000000000001"));
        await CreatePipeline(store).IndexProjectAsync("SwiftApp", _rootPath, ct: CancellationToken.None);

        var sparkle = store.Nodes.Where(node => node.Label == NodeLabel.Package).ShouldHaveSingleItem();
        sparkle.Properties["version"].ToString().ShouldBe("2.11.0");
        sparkle.Properties["revision"].ToString().ShouldBe("0000000000000000000000000000000000000001");
        store.Edges.Count(edge => edge.TargetId == sparkle.Id && edge.Type == EdgeType.REFERENCES_PACKAGE)
            .ShouldBe(1);
        store.Nodes.ShouldContain(node => node.Name == "App" && node.FilePath == "native/Sources/App.swift");
    }

    [Fact]
    public async Task IndexProjectAsync_IncrementalRun_ReplacesDeclaredDependenciesEvenWhenSwiftSourceExtractionFails()
    {
        static string Manifest(string dependency) => $$"""
            let package = Package(
                name: "ShapeKit",
                dependencies: [.package(url: "https://github.com/apple/{{dependency}}", from: "1.0.0")]
            )
            """;
        Write("Package.swift", Manifest("swift-log"));
        var store = new InMemoryGraphStore();
        await CreatePipeline(store, new FailingSwiftExtractor())
            .IndexProjectAsync("ShapeKit", _rootPath, ct: CancellationToken.None);
        store.Nodes.ShouldContain(node => node.Label == NodeLabel.Package && node.Name == "swift-log");

        Write("Package.swift", Manifest("swift-nio"));
        await CreatePipeline(store, new FailingSwiftExtractor())
            .IndexProjectAsync("ShapeKit", _rootPath, ct: CancellationToken.None);

        var packages = store.Nodes.Where(node => node.Label == NodeLabel.Package).ToList();
        packages.Select(node => node.QualifiedName).ShouldBe([
            "package:swiftpm:local:shapekit:shapekit@*",
            "package:swiftpm:scm:github.com/apple/swift-nio@upToNextMajor:1.0.0"
        ], ignoreOrder: true);
        var definition = packages.Single(node => node.Name == "ShapeKit");
        var nio = packages.Single(node => node.Name == "swift-nio");
        store.Edges.ShouldContain(edge =>
            edge.SourceId == definition.Id && edge.TargetId == nio.Id &&
            edge.Type == EdgeType.REFERENCES_PACKAGE);
    }

    private sealed class FailingSwiftExtractor : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } =
            new HashSet<string>([".swift"], StringComparer.OrdinalIgnoreCase);

        public Task<ExtractionResult> ExtractAsync(
            string filePath, string content, ExtractorContext context, CancellationToken ct = default) =>
            Task.FromResult(new ExtractionResult { Succeeded = false, FailureReason = "grammar unavailable" });
    }

    [Fact]
    public void ExtractionVersionCatalog_VersionsPackageSwiftWithTheManifestExtractor()
    {
        ICodeExtractor[] extractors = [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)];
        var without = new ExtractionVersionCatalog(extractors);
        var with = new ExtractionVersionCatalog(extractors,
            swiftPackageManifestExtractor: new SwiftPackageManifestExtractor());

        with.ForPath("Sources/App.swift").ShouldBe(without.ForPath("Sources/App.swift"));
        with.ForPath("Packages/Core/Package.swift")
            .ShouldBe(without.ForPath("Package.swift") + ";SwiftPackageManifestExtractor=1");
        with.Fingerprint.ShouldNotBe(without.Fingerprint);
        with.ForPath("src/lib.rs").ShouldBe(without.ForPath("src/lib.rs"));
    }

    private IndexingPipeline CreatePipeline(InMemoryGraphStore store, ICodeExtractor? extractor = null) =>
        new(
            store,
            [extractor ?? new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
            Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
            new LocalFileSystem(),
            NullLogger<IndexingPipeline>.Instance,
            cargoManifestExtractor: new CargoManifestExtractor(),
            swiftPackageManifestExtractor: new SwiftPackageManifestExtractor());

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Resolved(
        string version,
        string revision,
        string identity = "sparkle",
        string location = "https://github.com/sparkle-project/Sparkle") => $$"""
        {
          "originHash" : "e721da7f9826abdffcb6185e886155efa2514bd6234475f1afa893e29eb258d6",
          "pins" : [
            {
              "identity" : "{{identity}}",
              "kind" : "remoteSourceControl",
              "location" : "{{location}}",
              "state" : {
                "revision" : "{{revision}}",
                "version" : "{{version}}"
              }
            }
          ],
          "version" : 3
        }
        """;
}
