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
/// Swift imports resolve to the repository's SwiftPM targets, to the dependency packages that
/// provide them, or to external module stubs, and stay bound across incremental runs.
/// </summary>
public sealed class SwiftImportResolutionTests : IDisposable
{
    private const string Project = "ShapeKit";
    private const string Shape = $"{Project}:Sources/ShapeKit/Shape.swift";
    private const string ShapeTests = $"{Project}:Tests/ShapeKitTests/ShapeTests.swift";
    private const string GeometryTarget = $"{Project}:Package.swift#target:Geometry";
    private const string ShapeKitTarget = $"{Project}:Package.swift#target:ShapeKit";
    private const string Point = $"{Project}:Sources/Geometry/Point.swift#type:Point";
    private const string SwiftLog = "package:swiftpm:scm:github.com/apple/swift-log@upToNextMajor:1.5.0";
    private const string Collections = "package:swiftpm:scm:github.com/apple/swift-collections@upToNextMajor:1.0.0";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-swift-imports-{Guid.NewGuid():N}");
    private readonly InMemoryGraphStore _store = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string Manifest(bool withCollections) => $$"""
        // swift-tools-version:5.9
        import PackageDescription

        let package = Package(
            name: "ShapeKit",
            dependencies: [
                .package(url: "https://github.com/apple/swift-log.git", from: "1.5.0"),
                {{(withCollections ? ".package(url: \"https://github.com/apple/swift-collections\", from: \"1.0.0\")," : "")}}
            ],
            targets: [
                .target(name: "ShapeKit", dependencies: [
                    "Geometry",
                    .product(name: "Logging", package: "swift-log"),
                    {{(withCollections ? ".product(name: \"Collections\", package: \"swift-collections\")," : "")}}
                ]),
                .target(name: "Geometry"),
                .testTarget(name: "ShapeKitTests", dependencies: ["ShapeKit"]),
            ]
        )
        """;

    private static string Resolved(string sparkleVersion) => $$"""
        {
          "pins" : [
            {
              "identity" : "sparkle",
              "kind" : "remoteSourceControl",
              "location" : "https://github.com/sparkle-project/Sparkle",
              "state" : { "revision" : "eef1a539a373c1f1a320624b1130fc5de7b2e100", "version" : "{{sparkleVersion}}" }
            },
            {
              "identity" : "swift-log",
              "kind" : "remoteSourceControl",
              "location" : "https://github.com/apple/swift-log.git",
              "state" : { "revision" : "532d8b529501fb73a2455b179e0bbb6d49b652ed", "version" : "1.5.3" }
            }
          ],
          "version" : 2
        }
        """;

    private const string ShapeSource = """
        import Foundation
        import Geometry
        import Logging
        import Sparkle
        import Collections
        import struct Geometry.Point
        import func Geometry.distance
        import Darwin.C
        import SwiftUI

        struct Shape {}
        """;

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private Task IndexAsync() =>
        new IndexingPipeline(
                _store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance,
                swiftPackageManifestExtractor: new SwiftPackageManifestExtractor())
            .IndexProjectAsync(Project, _root, ct: CancellationToken.None);

    private void WriteRepository(bool withCollections = false, string sparkleVersion = "2.10.0")
    {
        Write("Package.swift", Manifest(withCollections));
        Write("Package.resolved", Resolved(sparkleVersion));
        Write("Sources/ShapeKit/Shape.swift", ShapeSource);
        Write("Sources/Geometry/Point.swift", "public struct Point {}\npublic func distance() -> Double { 0 }\n");
        Write("Tests/ShapeKitTests/ShapeTests.swift", "import XCTest\n@testable import ShapeKit\n");
    }

    private List<(string Source, string Target, string? MatchedBy, string? Symbols)> Imports()
    {
        var byId = _store.Nodes.ToDictionary(node => node.Id);
        return _store.Edges.Where(edge => edge.Type == EdgeType.IMPORTS)
            .Select(edge => (byId[edge.SourceId].QualifiedName, byId[edge.TargetId].QualifiedName,
                edge.Properties.GetValueOrDefault("matched_by")?.ToString(),
                edge.Properties.GetValueOrDefault("imported_symbols")?.ToString()))
            .ToList();
    }

    private static (string, string, string?, string?)[] Expected(string sparkle, string collections, string collectionsMatch) =>
    [
        (Shape, "swift-external-module:Foundation", "external", null),
        (Shape, GeometryTarget, "target", "Point,distance"),
        (Shape, SwiftLog, "product", null),
        (Shape, sparkle, "identity", null),
        (Shape, collections, collectionsMatch, null),
        (Shape, "swift-external-module:Darwin", "external", null),
        (Shape, "swift-external-module:SwiftUI", "external", null),
        // `import struct Geometry.Point` also imports the type; `import func` imports no type.
        (Shape, Point, null, "Point"),
        (ShapeTests, "swift-external-module:XCTest", "external", null),
        (ShapeTests, ShapeKitTarget, "target", null),
        ($"{Project}:Package.swift", "swift-external-module:PackageDescription", "external", null)
    ];

    [Fact]
    public async Task IndexProject_SwiftImports_ResolveToTargetsPackagesAndExternalModules()
    {
        WriteRepository();
        await IndexAsync();

        Imports().ShouldBe(
            Expected("package:swiftpm:scm:github.com/sparkle-project/sparkle@2.10.0",
                "swift-external-module:Collections", "external"),
            ignoreOrder: true);
        var foundation = _store.Nodes.Single(node => node.QualifiedName == "swift-external-module:Foundation");
        foundation.Label.ShouldBe(NodeLabel.Module);
        foundation.Name.ShouldBe("Foundation");
        foundation.Properties["external"].ShouldBe(true);
    }

    [Fact]
    public async Task IndexProject_IncrementalRun_RebindsImportsOfUnchangedFilesWhenManifestsChange()
    {
        WriteRepository();
        await IndexAsync();

        // Sparkle is re-pinned and Collections becomes a declared product; Shape.swift is unchanged.
        WriteRepository(withCollections: true, sparkleVersion: "2.11.0");
        await IndexAsync();

        Imports().ShouldBe(
            Expected("package:swiftpm:scm:github.com/sparkle-project/sparkle@2.11.0", Collections, "product"),
            ignoreOrder: true);
        // The stub Collections lost its last import and is removed; the old pin is gone.
        _store.Nodes.ShouldNotContain(node => node.QualifiedName == "swift-external-module:Collections");
        _store.Nodes.ShouldNotContain(node => node.QualifiedName.EndsWith("sparkle@2.10.0", StringComparison.Ordinal));

        // Removing the dependency again makes the import external again.
        WriteRepository(withCollections: false, sparkleVersion: "2.11.0");
        await IndexAsync();

        Imports().ShouldBe(
            Expected("package:swiftpm:scm:github.com/sparkle-project/sparkle@2.11.0",
                "swift-external-module:Collections", "external"),
            ignoreOrder: true);
    }

    [Fact]
    public async Task IndexProject_DeletingTheOnlyImporterOfAnExternalModule_RemovesItsStub()
    {
        WriteRepository();
        await IndexAsync();
        _store.Nodes.ShouldContain(node => node.QualifiedName == "swift-external-module:XCTest");

        File.Delete(Path.Combine(_root, "Tests/ShapeKitTests/ShapeTests.swift"));
        await IndexAsync();

        _store.Nodes.ShouldNotContain(node => node.QualifiedName == "swift-external-module:XCTest");
        _store.Nodes.ShouldContain(node => node.QualifiedName == "swift-external-module:Foundation");
    }

    [Fact]
    public async Task IndexProject_SameTargetNameInSeveralPackages_BindsTheNearestEnclosingPackage()
    {
        Write("Package.swift", "let package = Package(name: \"Root\", targets: [.target(name: \"Core\"), .target(name: \"App\")])\n");
        Write("Modules/Feature/Package.swift", "let package = Package(name: \"Feature\", targets: [.target(name: \"Core\"), .target(name: \"Feature\")])\n");
        Write("Modules/A/Package.swift", "let package = Package(name: \"A\", targets: [.target(name: \"Util\")])\n");
        Write("Modules/B/Package.swift", "let package = Package(name: \"B\", targets: [.target(name: \"Util\")])\n");
        Write("Sources/App/App.swift", "import Core\nimport Util\n");
        Write("Modules/Feature/Sources/Feature/Feature.swift", "import Core\n");
        await IndexAsync();

        var byId = _store.Nodes.ToDictionary(node => node.Id);
        _store.Edges.Where(edge => edge.Type == EdgeType.IMPORTS)
            .Select(edge => (byId[edge.SourceId].QualifiedName, byId[edge.TargetId].QualifiedName,
                edge.Properties["confidence"].ToString()))
            .ShouldBe(
            [
                ($"{Project}:Sources/App/App.swift", $"{Project}:Package.swift#target:Core", "high"),
                ($"{Project}:Modules/Feature/Sources/Feature/Feature.swift",
                    $"{Project}:Modules/Feature/Package.swift#target:Core", "high"),
                // Neither package encloses the importer: both stay candidates, with low confidence.
                ($"{Project}:Sources/App/App.swift", $"{Project}:Modules/A/Package.swift#target:Util", "low"),
                ($"{Project}:Sources/App/App.swift", $"{Project}:Modules/B/Package.swift#target:Util", "low")
            ], ignoreOrder: true);
    }

    [Fact]
    public async Task ExtractOverlay_ResolvesSwiftImportsAgainstTheDefaultBranchPackages()
    {
        WriteRepository();
        await IndexAsync();

        Write("Sources/ShapeKit/Extra.swift", "import Geometry\nimport Logging\nimport Combine\n");
        var slice = await new IndexingPipeline(
                _store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance,
                swiftPackageManifestExtractor: new SwiftPackageManifestExtractor())
            .ExtractOverlayAsync(Project, _root, ["Sources/ShapeKit/Extra.swift"], [], [], CancellationToken.None);

        const string extra = $"{Project}:Sources/ShapeKit/Extra.swift";
        slice.Edges.Where(edge => edge.Type == EdgeType.IMPORTS)
            .Select(edge => (edge.SourceQN, edge.TargetQN))
            .ShouldBe(
            [
                (extra, GeometryTarget),
                (extra, SwiftLog),
                (extra, "swift-external-module:Combine")
            ], ignoreOrder: true);
        slice.Nodes.ShouldContain(node => node.QualifiedName == "swift-external-module:Combine");
    }
}
