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

/// <summary>
/// sc-26145 (LensWorks re-run): every incremental run rewrote the package slices of manifests
/// that are not hash-tracked source files (Cargo.toml, Package.resolved), so their Package rows got
/// new ids each run although nothing changed. Those manifests (and project.pbxproj) are now
/// tracked by content hash and manifest version per group: an unchanged group is not written.
/// </summary>
public sealed class ManifestSliceTrackingTests : IDisposable
{
    internal const string Project = "Mixed";

    internal static readonly string[] TrackedManifests =
    [
        "Cargo.toml",
        "crates/core/Cargo.toml",
        "app/Package.resolved",
        "app/App.xcodeproj/project.pbxproj"
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-manifest-slices-{Guid.NewGuid():N}");
    private readonly InMemoryGraphStore _store = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    internal static void WriteRepository(string root, string sparkleVersion = "2.10.0", string coreVersion = "0.1.0")
    {
        void Write(string relativePath, string content)
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        Write("Cargo.toml", "[workspace]\nmembers = [\"crates/core\"]\n");
        Write("crates/core/Cargo.toml",
            $"[package]\nname = \"core\"\nversion = \"{coreVersion}\"\n\n[dependencies]\nblake3 = \"1\"\n");
        Write("crates/core/src/lib.rs", "pub fn hash() -> u32 { 1 }\n");
        Write("app/Package.swift", """
            // swift-tools-version:5.9
            import PackageDescription
            let package = Package(
                name: "AppKitPlus",
                dependencies: [.package(url: "https://github.com/sparkle-project/Sparkle", from: "2.0.0")],
                targets: [.target(name: "AppKitPlus", dependencies: [.product(name: "Sparkle", package: "Sparkle")])]
            )
            """);
        Write("app/Package.resolved", $$"""
            { "pins" : [ { "identity" : "sparkle", "kind" : "remoteSourceControl",
                "location" : "https://github.com/sparkle-project/Sparkle",
                "state" : { "revision" : "eef1a539a373c1f1a320624b1130fc5de7b2e100", "version" : "{{sparkleVersion}}" } } ],
              "version" : 2 }
            """);
        Write("app/App.xcodeproj/project.pbxproj", XcodeProjectImportTests.ProjectFile(
            ("App", "com.apple.product-type.application", ["PRODUCT_NAME = App;"], ["PRODUCT_NAME = App;"])));
        Write("app/AppTests/AppTests.swift", "import XCTest\n@testable import App\nimport Sparkle\n");
    }

    internal static IndexingPipeline Pipeline(CodeGraph.Data.IGraphStore store) =>
        new(
            store,
            [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
            Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
            new LocalFileSystem(),
            NullLogger<IndexingPipeline>.Instance,
            cargoManifestExtractor: new CargoManifestExtractor(),
            swiftPackageManifestExtractor: new SwiftPackageManifestExtractor());

    private Task IndexAsync() => Pipeline(_store).IndexProjectAsync(Project, _root, ct: CancellationToken.None);

    private Dictionary<string, long> ManifestNodeIds() =>
        _store.Nodes
            .Where(node => node.FilePath is { } path && TrackedManifests.Contains(path))
            .ToDictionary(node => node.QualifiedName, node => node.Id);

    private List<string> ReplacedManifestPaths() =>
        _store.ReplacedFileSlices.SelectMany(slice => slice)
            .Where(path => TrackedManifests.Contains(path))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public async Task IncrementalRun_UnchangedManifests_WritesNoPackageSlicesAndKeepsRowIds()
    {
        WriteRepository(_root);
        await IndexAsync();

        var ids = ManifestNodeIds();
        ids.Keys.ShouldContain(qn => qn.StartsWith("package:cargo:", StringComparison.Ordinal));
        ids.Keys.ShouldContain(qn => qn.Contains("sparkle", StringComparison.Ordinal));
        ids.Keys.ShouldContain($"{Project}:app/App.xcodeproj/project.pbxproj#target:App");
        // Every tracked manifest has a hash record carrying its manifest version.
        TrackedManifests.ShouldAllBe(path => _store.FileHashesOf(Project).ContainsKey(path));
        var callsBefore = _store.ReplacedFileSlices.Count;

        await IndexAsync();
        await IndexAsync();

        _store.ReplacedFileSlices.Skip(callsBefore).SelectMany(slice => slice)
            .Where(path => TrackedManifests.Contains(path))
            .ShouldBeEmpty();
        ManifestNodeIds().ShouldBe(ids);
        // The unchanged import of the Xcode target is still bound to it.
        var byId = _store.Nodes.ToDictionary(node => node.Id);
        _store.Edges.Where(edge => edge.Type == EdgeType.IMPORTS)
            .Select(edge => byId[edge.TargetId].QualifiedName)
            .ShouldContain($"{Project}:app/App.xcodeproj/project.pbxproj#target:App");
    }

    [Fact]
    public async Task IncrementalRun_ChangedManifest_RewritesOnlyItsGroup()
    {
        WriteRepository(_root);
        await IndexAsync();
        var ids = ManifestNodeIds();

        // A re-pinned Package.resolved rewrites the Swift group (pins and Xcode targets), not Cargo.
        WriteRepository(_root, sparkleVersion: "2.11.0");
        var before = _store.ReplacedFileSlices.Count;
        await IndexAsync();

        _store.ReplacedFileSlices.Skip(before).SelectMany(slice => slice)
            .Where(path => TrackedManifests.Contains(path)).Distinct().Order(StringComparer.Ordinal)
            .ShouldBe(["app/App.xcodeproj/project.pbxproj", "app/Package.resolved"]);
        _store.Nodes.ShouldContain(node => node.QualifiedName.Contains("sparkle@2.11.0", StringComparison.Ordinal));
        _store.Nodes.ShouldNotContain(node => node.QualifiedName.Contains("sparkle@2.10.0", StringComparison.Ordinal));
        var cargoIds = ids.Where(entry => entry.Key.StartsWith("package:cargo:", StringComparison.Ordinal)).ToList();
        cargoIds.ShouldNotBeEmpty();
        foreach (var (qualifiedName, id) in cargoIds)
            ManifestNodeIds()[qualifiedName].ShouldBe(id);

        // A changed member Cargo.toml rewrites every Cargo manifest (path dependencies refer
        // across them), and no Swift manifest.
        WriteRepository(_root, sparkleVersion: "2.11.0", coreVersion: "0.2.0");
        before = _store.ReplacedFileSlices.Count;
        await IndexAsync();

        _store.ReplacedFileSlices.Skip(before).SelectMany(slice => slice)
            .Where(path => TrackedManifests.Contains(path)).Distinct().Order(StringComparer.Ordinal)
            .ShouldBe(["Cargo.toml", "crates/core/Cargo.toml"]);
        _store.Nodes.ShouldContain(node => node.QualifiedName.Contains("core@0.2.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncrementalRun_RemovedManifest_RemovesItsSliceAndHashRecord()
    {
        WriteRepository(_root);
        // A manifest that yields no nodes at all (an Xcode project without native targets) still
        // has a hash record that must go with it.
        const string empty = "tools/Empty.xcodeproj/project.pbxproj";
        Directory.CreateDirectory(Path.Combine(_root, "tools/Empty.xcodeproj"));
        File.WriteAllText(Path.Combine(_root, empty), XcodeProjectImportTests.ProjectFile());
        await IndexAsync();
        _store.FileHashesOf(Project).Keys.ShouldContain(empty);

        File.Delete(Path.Combine(_root, "app/App.xcodeproj/project.pbxproj"));
        File.Delete(Path.Combine(_root, "app/Package.resolved"));
        File.Delete(Path.Combine(_root, empty));
        await IndexAsync();
        _store.FileHashesOf(Project).Keys.ShouldNotContain(empty);

        _store.Nodes.ShouldNotContain(node =>
            node.FilePath == "app/App.xcodeproj/project.pbxproj" || node.FilePath == "app/Package.resolved");
        _store.FileHashesOf(Project).Keys.ShouldNotContain("app/App.xcodeproj/project.pbxproj");
        _store.FileHashesOf(Project).Keys.ShouldNotContain("app/Package.resolved");
        // The unchanged test file's import of the removed target is re-bound to a stub.
        var byId = _store.Nodes.ToDictionary(node => node.Id);
        _store.Edges.Where(edge => edge.Type == EdgeType.IMPORTS)
            .Select(edge => byId[edge.TargetId].QualifiedName)
            .ShouldContain("swift-external-module:App");

        // Nothing changes afterwards: nothing is written for the manifests left.
        var before = _store.ReplacedFileSlices.Count;
        await IndexAsync();
        _store.ReplacedFileSlices.Skip(before).SelectMany(slice => slice)
            .Where(path => TrackedManifests.Contains(path) || path.EndsWith("Package.swift", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task IncrementalRun_ManifestVersionChange_RewritesTheGroupOnce()
    {
        WriteRepository(_root);
        await IndexAsync();

        // A record written by an older manifest extractor version is outdated.
        var store = _store;
        var outdated = store.FileHashesOf(Project).ToDictionary(entry => entry.Key, entry => entry.Value);
        await store.ReplaceProjectFilesAsync(
            Project, ["crates/core/Cargo.toml"],
            store.Nodes.Where(node => node.FilePath == "crates/core/Cargo.toml").ToList(),
            [],
            new Dictionary<string, string> { ["crates/core/Cargo.toml"] = outdated["crates/core/Cargo.toml"] },
            new Dictionary<string, string> { ["crates/core/Cargo.toml"] = "p1;CargoManifestExtractor=0" });
        var before = _store.ReplacedFileSlices.Count;

        await IndexAsync();
        await IndexAsync();

        _store.ReplacedFileSlices.Skip(before).SelectMany(slice => slice)
            .Where(path => TrackedManifests.Contains(path)).Distinct().Order(StringComparer.Ordinal)
            .ShouldBe(["Cargo.toml", "crates/core/Cargo.toml"]);
        _store.ReplacedFileSlices.Skip(before + 1).SelectMany(slice => slice)
            .Where(path => TrackedManifests.Contains(path)).ShouldBeEmpty();
    }

    [Fact]
    public void ExtractionVersionCatalog_VersionsTrackedManifestsAndFingerprintsThem()
    {
        var extractors = new ICodeExtractor[] { new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance) };
        var catalog = new ExtractionVersionCatalog(extractors,
            swiftPackageManifestExtractor: new SwiftPackageManifestExtractor(),
            cargoManifestExtractor: new CargoManifestExtractor());

        catalog.ForManifest("crates/core/Cargo.toml")
            .ShouldBe($"p{ExtractionVersionCatalog.PipelineVersion};CargoManifestExtractor=1");
        catalog.ForManifest("app/Package.resolved")
            .ShouldBe($"p{ExtractionVersionCatalog.PipelineVersion};SwiftPackageManifestExtractor=2");
        catalog.ForManifest("app/App.xcodeproj/project.pbxproj")
            .ShouldBe($"p{ExtractionVersionCatalog.PipelineVersion};SwiftPackageManifestExtractor=2");
        catalog.ForManifest("src/lib.rs").ShouldBeNull();
        new ExtractionVersionCatalog(extractors, swiftPackageManifestExtractor: new SwiftPackageManifestExtractor())
            .Fingerprint.ShouldNotBe(catalog.Fingerprint);
    }
}
