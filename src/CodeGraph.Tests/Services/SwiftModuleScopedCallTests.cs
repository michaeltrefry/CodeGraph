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
/// sc-26145 (LensWorks final check): with a vendored copy of the app, Swift calls in
/// <c>vendor/Compositor</c> resolved by name into <c>native/Compositor</c> extension members.
/// Module visibility is a tie-breaker: a name with a single candidate binds exactly as before;
/// among several candidates the one the caller can see wins: the single candidate of its own
/// module, else the single one of the modules its file imports (re-exports included).
/// </summary>
public sealed class SwiftModuleScopedCallTests : IDisposable
{
    private const string Project = "Lens";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-swift-scoped-calls-{Guid.NewGuid():N}");
    private readonly InMemoryGraphStore _store = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private IndexingPipeline Pipeline() =>
        new(
            _store,
            [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
            Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
            new LocalFileSystem(),
            NullLogger<IndexingPipeline>.Instance,
            swiftPackageManifestExtractor: new SwiftPackageManifestExtractor());

    private Task IndexAsync() => Pipeline().IndexProjectAsync(Project, _root, ct: CancellationToken.None);

    private List<(string Caller, string CallerFile, string Callee, string CalleeFile)> Calls()
    {
        var byId = _store.Nodes.ToDictionary(node => node.Id);
        return _store.Edges.Where(edge => edge.Type == EdgeType.CALLS)
            .Select(edge => (byId[edge.SourceId].Name, byId[edge.SourceId].FilePath,
                byId[edge.TargetId].Name, byId[edge.TargetId].FilePath))
            .ToList();
    }

    private static string AppProject() => XcodeProjectImportTests.ProjectFile(
        ("Compositor", "com.apple.product-type.application",
            ["PRODUCT_NAME = LensWorks;", "PRODUCT_MODULE_NAME = Compositor;"],
            ["PRODUCT_NAME = LensWorks;", "PRODUCT_MODULE_NAME = Compositor;"]),
        ("CompositorTests", "com.apple.product-type.bundle.unit-test",
            ["PRODUCT_NAME = \"$(TARGET_NAME)\";"], ["PRODUCT_NAME = \"$(TARGET_NAME)\";"]));

    /// <summary>Members declared in the body of a type both copies declare (an ambiguous receiver).</summary>
    [Fact]
    public async Task IndexProject_VendoredAppCopy_AmbiguousReceiverResolvesToTheCallersCopy()
    {
        Write("native/Compositor.xcodeproj/project.pbxproj", AppProject());
        Write("native/Compositor/Layer.swift", """
            struct Layer {}
            extension Layer {
                func apply() {}
                func polish() {}
            }
            """);
        Write("native/Compositor/Render.swift", """
            final class Render {
                func run(layer: Layer) {
                    layer.apply()
                    layer.polish()
                }
            }
            """);
        Write("vendor/Compositor/Compositor.xcodeproj/project.pbxproj", AppProject());
        // The vendored copy is older: it has apply() but no polish().
        Write("vendor/Compositor/Compositor/Layer.swift", """
            struct Layer {}
            extension Layer {
                func apply() {}
            }
            """);
        Write("vendor/Compositor/Compositor/Render.swift", """
            final class Render {
                func run(layer: Layer) {
                    layer.apply()
                    layer.polish()
                }
            }
            """);
        Write("vendor/Compositor/CompositorTests/RenderTests.swift", """
            @testable import Compositor
            final class RenderTests {
                func testApply(layer: Layer) {
                    layer.apply()
                }
            }
            """);

        await IndexAsync();

        Calls().ShouldBe(
        [
            ("run", "native/Compositor/Render.swift", "apply", "native/Compositor/Layer.swift"),
            ("run", "native/Compositor/Render.swift", "polish", "native/Compositor/Layer.swift"),
            ("run", "vendor/Compositor/Compositor/Render.swift", "apply", "vendor/Compositor/Compositor/Layer.swift"),
            ("testApply", "vendor/Compositor/CompositorTests/RenderTests.swift", "apply",
                "vendor/Compositor/Compositor/Layer.swift")
        ], ignoreOrder: true);
    }

    /// <summary>
    /// The LensWorks shape: two modules each declare <c>extension T { func apply() }</c> in their own
    /// files (a repository type both declare, and a type outside the repository): each caller binds
    /// its own module's copy. A name only the original declares still binds (single candidate).
    /// </summary>
    [Fact]
    public async Task IndexProject_VendoredAppCopy_DuplicateExtensionMembersBindPerCaller()
    {
        foreach (var root in new[] { "native", "vendor/Compositor" })
        {
            Write($"{root}/Compositor.xcodeproj/project.pbxproj", AppProject());
            Write($"{root}/Compositor/Layer.swift", "struct Layer {}\n");
            Write($"{root}/Compositor/Layer+Blend.swift", """
                extension Layer {
                    func blend() {}
                }
                """);
            Write($"{root}/Compositor/CGRect+Apply.swift", """
                extension CGRect {
                    func apply() {}
                }
                """);
            Write($"{root}/Compositor/Render.swift", """
                final class Render {
                    func run(layer: Layer, rect: CGRect) {
                        layer.blend()
                        rect.apply()
                        rect.centred()
                    }
                }
                """);
        }

        Write("native/Compositor/CGRect+Geometry.swift", """
            extension CGRect {
                func centred() -> CGRect { self }
            }
            """);

        await IndexAsync();

        Calls().ShouldBe(
        [
            ("run", "native/Compositor/Render.swift", "blend", "native/Compositor/Layer+Blend.swift"),
            ("run", "native/Compositor/Render.swift", "apply", "native/Compositor/CGRect+Apply.swift"),
            ("run", "native/Compositor/Render.swift", "centred", "native/Compositor/CGRect+Geometry.swift"),
            ("run", "vendor/Compositor/Compositor/Render.swift", "blend", "vendor/Compositor/Compositor/Layer+Blend.swift"),
            ("run", "vendor/Compositor/Compositor/Render.swift", "apply", "vendor/Compositor/Compositor/CGRect+Apply.swift"),
            // A single candidate binds exactly as without module scopes.
            ("run", "vendor/Compositor/Compositor/Render.swift", "centred", "native/Compositor/CGRect+Geometry.swift")
        ], ignoreOrder: true);
    }

    /// <summary>A branch overlay reads the module scopes from the persisted default-branch graph.</summary>
    [Fact]
    public async Task ExtractOverlay_VendoredCaller_BreaksTheTieWithThePersistedModules()
    {
        foreach (var root in new[] { "native", "vendor/Compositor" })
        {
            Write($"{root}/Compositor.xcodeproj/project.pbxproj", AppProject());
            Write($"{root}/Compositor/CGRect+Blend.swift", "extension CGRect {\n    func blend() {}\n}\n");
        }
        Write("vendor/Compositor/Compositor/Render.swift", "final class Render {}\n");
        await IndexAsync();

        Write("vendor/Compositor/Compositor/Render.swift", """
            final class Render {
                func run(rect: CGRect) {
                    rect.blend()
                }
            }
            """);
        var slice = await Pipeline().ExtractOverlayAsync(
            Project, _root, ["vendor/Compositor/Compositor/Render.swift"], [], [], CancellationToken.None);

        slice.Edges.Where(edge => edge.Type == EdgeType.CALLS)
            .Select(edge => edge.TargetQN)
            .ShouldBe([$"{Project}:vendor/Compositor/Compositor/CGRect+Blend.swift#extension:CGRect#method:blend()"]);
    }

    [Fact]
    public async Task IndexProject_SwiftPackageTargets_TieBreaksToTheCallersModuleThenItsImports()
    {
        Write("Package.swift", """
            // swift-tools-version:5.9
            import PackageDescription

            let package = Package(
                name: "Kit",
                targets: [
                    .target(name: "Core"),
                    .target(name: "Other"),
                    .target(name: "Feature", dependencies: ["Core"]),
                ]
            )
            """);
        Write("Sources/Core/Token.swift", """
            public struct Token {}
            extension Token {
                public func apply() {}
            }
            """);
        Write("Sources/Other/Token.swift", """
            struct Token {}
            extension Token {
                func apply() {}
                func local() { apply() }
            }
            func solo() {}
            """);
        Write("Sources/Feature/Use.swift", """
            import Core
            final class Use {
                func run(token: Token) {
                    token.apply()
                    solo()
                }
            }
            """);

        await IndexAsync();

        Calls().ShouldBe(
        [
            ("run", "Sources/Feature/Use.swift", "apply", "Sources/Core/Token.swift"),
            // Not imported, but the only candidate: binds as without module scopes.
            ("run", "Sources/Feature/Use.swift", "solo", "Sources/Other/Token.swift"),
            ("local", "Sources/Other/Token.swift", "apply", "Sources/Other/Token.swift")
        ], ignoreOrder: true);
    }

    /// <summary>
    /// Targets sharing one <c>path:</c> are told apart by their <c>sources:</c>; targets sharing a
    /// root without <c>sources:</c> are one module scope.
    /// </summary>
    [Fact]
    public async Task IndexProject_TargetsSharingASourcePath_TieBreakByTheirSources()
    {
        Write("Package.swift", """
            // swift-tools-version:5.9
            import PackageDescription

            let package = Package(
                name: "Kit",
                targets: [
                    .target(name: "A", path: "Sources", sources: ["A"]),
                    .target(name: "B", path: "Sources", sources: ["B"]),
                    .target(name: "C", path: "Shared"),
                    .target(name: "D", path: "Shared"),
                    .target(name: "Feature", path: "App", dependencies: ["B", "D"]),
                ]
            )
            """);
        Write("Sources/A/Alpha.swift", "func beta() {}\nfunc gamma() {}\n");
        Write("Sources/B/Beta.swift", "public func beta() {}\n");
        Write("Shared/Gamma.swift", "public func gamma() {}\n");
        Write("App/Use.swift", """
            import B
            import D
            func run() {
                beta()
                gamma()
            }
            """);

        await IndexAsync();

        _store.Nodes.Single(node => node.Label == NodeLabel.Module && node.Name == "A")
            .Properties[SwiftPackageManifestExtractor.SourcePathsProperty].ToString().ShouldBe("Sources/A");
        Calls().ShouldBe(
        [
            ("run", "App/Use.swift", "beta", "Sources/B/Beta.swift"),
            ("run", "App/Use.swift", "gamma", "Shared/Gamma.swift")
        ], ignoreOrder: true);
    }

    private void WriteReexportPackage()
    {
        Write("Package.swift", """
            // swift-tools-version:5.9
            import PackageDescription

            let package = Package(
                name: "Kit",
                targets: [
                    .target(name: "Models"),
                    .target(name: "CoreKit", dependencies: ["Models"]),
                    .target(name: "Other"),
                    .target(name: "Feature", dependencies: ["CoreKit"]),
                ]
            )
            """);
        Write("Sources/Models/Model.swift", "public func make() {}\n");
        Write("Sources/CoreKit/Exports.swift", "@_exported import Models\n");
        Write("Sources/Other/Other.swift", "func make() {}\n");
        Write("Sources/Feature/Use.swift", """
            import CoreKit
            func run() {
                make()
            }
            """);
    }

    /// <summary>Feature imports CoreKit, which re-exports Models: Models' <c>make()</c> is visible.</summary>
    [Fact]
    public async Task IndexProject_ReexportedModule_IsVisibleThroughTheImportingModule()
    {
        WriteReexportPackage();

        await IndexAsync();

        var byId = _store.Nodes.ToDictionary(node => node.Id);
        _store.Edges.Single(edge => edge.Type == EdgeType.IMPORTS
                                    && byId[edge.SourceId].QualifiedName == $"{Project}:Sources/CoreKit/Exports.swift")
            .Properties[IndexingPipeline.ExportedProperty].ShouldBe(true);
        Calls().ShouldBe([("run", "Sources/Feature/Use.swift", "make", "Sources/Models/Model.swift")]);
    }

    /// <summary>
    /// An incremental run that re-extracts only the caller still sees the re-export, which lives in
    /// a file it does not re-extract (the persisted import edges).
    /// </summary>
    [Fact]
    public async Task IndexProject_ReexportedModule_IncrementalRunOfTheCallerOnly_StillSeesIt()
    {
        WriteReexportPackage();
        await IndexAsync();

        Write("Sources/Feature/Use.swift", """
            import CoreKit
            func run() {
                make()
            }
            func again() {
                make()
            }
            """);
        await IndexAsync();

        Calls().ShouldBe(
        [
            ("run", "Sources/Feature/Use.swift", "make", "Sources/Models/Model.swift"),
            ("again", "Sources/Feature/Use.swift", "make", "Sources/Models/Model.swift")
        ], ignoreOrder: true);
    }
}
