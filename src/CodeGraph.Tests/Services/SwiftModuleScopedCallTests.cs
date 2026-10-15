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
/// sc-26145 (LensWorks final check): with a vendored copy of the app, 19 CALLS crossed from
/// <c>vendor/Compositor</c> Swift into <c>native/Compositor</c> extension methods although the
/// calling files belong to (and import) the vendor target. A Swift call binds only to Swift code of
/// the caller's own module or of a module its file imports.
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

    private Task IndexAsync() =>
        new IndexingPipeline(
                _store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance,
                swiftPackageManifestExtractor: new SwiftPackageManifestExtractor())
            .IndexProjectAsync(Project, _root, ct: CancellationToken.None);

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

    [Fact]
    public async Task IndexProject_VendoredAppCopy_CallsBindWithinTheCallersOwnTarget()
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
    /// The LensWorks shape: members of extensions declared in their own files (of a type both
    /// targets declare, and of a type outside the repository) are owned by the extended type path
    /// in any file, so without module scope a vendored caller bound to the original's extension.
    /// </summary>
    [Fact]
    public async Task IndexProject_VendoredAppCopy_ExtensionMembersInOtherFilesBindWithinTheCallersTarget()
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
            Write($"{root}/Compositor/Render.swift", """
                final class Render {
                    func run(layer: Layer, rect: CGRect) {
                        layer.blend()
                        rect.centred()
                    }
                }
                """);
        }

        // Only the original has the CGRect helper.
        Write("native/Compositor/CGRect+Geometry.swift", """
            extension CGRect {
                func centred() -> CGRect { self }
            }
            """);

        await IndexAsync();

        Calls().ShouldBe(
        [
            ("run", "native/Compositor/Render.swift", "blend", "native/Compositor/Layer+Blend.swift"),
            ("run", "native/Compositor/Render.swift", "centred", "native/Compositor/CGRect+Geometry.swift"),
            ("run", "vendor/Compositor/Compositor/Render.swift", "blend", "vendor/Compositor/Compositor/Layer+Blend.swift")
        ], ignoreOrder: true);
    }

    /// <summary>A branch overlay reads the module scopes from the persisted default-branch graph.</summary>
    [Fact]
    public async Task ExtractOverlay_VendoredCaller_BindsWithinItsTargetAgainstThePersistedModules()
    {
        foreach (var root in new[] { "native", "vendor/Compositor" })
        {
            Write($"{root}/Compositor.xcodeproj/project.pbxproj", AppProject());
            Write($"{root}/Compositor/Layer+Blend.swift", "extension CGRect {\n    func blend() {}\n}\n");
        }
        Write("native/Compositor/CGRect+Geometry.swift", "extension CGRect {\n    func centred() -> CGRect { self }\n}\n");
        Write("vendor/Compositor/Compositor/Render.swift", "final class Render {}\n");
        await IndexAsync();

        Write("vendor/Compositor/Compositor/Render.swift", """
            final class Render {
                func run(rect: CGRect) {
                    rect.blend()
                    rect.centred()
                }
            }
            """);
        var slice = await new IndexingPipeline(
                _store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance,
                swiftPackageManifestExtractor: new SwiftPackageManifestExtractor())
            .ExtractOverlayAsync(Project, _root, ["vendor/Compositor/Compositor/Render.swift"], [], [], CancellationToken.None);

        slice.Edges.Where(edge => edge.Type == EdgeType.CALLS)
            .Select(edge => edge.TargetQN)
            .ShouldBe([$"{Project}:vendor/Compositor/Compositor/Layer+Blend.swift#extension:CGRect#method:blend()"]);
    }

    [Fact]
    public async Task IndexProject_SwiftPackageTargets_CallsBindToTheCallersModuleAndItsImportsOnly()
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
            """);
        Write("Sources/Feature/Use.swift", """
            import Core
            final class Use {
                func run(token: Token) {
                    token.apply()
                }
            }
            """);

        await IndexAsync();

        Calls().ShouldBe(
        [
            ("run", "Sources/Feature/Use.swift", "apply", "Sources/Core/Token.swift"),
            ("local", "Sources/Other/Token.swift", "apply", "Sources/Other/Token.swift")
        ], ignoreOrder: true);
    }
}
