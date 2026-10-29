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
/// sc-26145 (LensWorks re-run): <c>@testable import Compositor</c> in 78 test files bound to a
/// <c>swift-external-module:Compositor</c> stub although Compositor is the repository's own Xcode
/// app target (<c>PRODUCT_MODULE_NAME = Compositor</c>). Native targets of Xcode projects are now
/// local Module nodes that imports resolve to like SwiftPM targets.
/// </summary>
public sealed class XcodeProjectImportTests : IDisposable
{
    private const string Repository = "Lens";
    private const string AppProject = "native/Compositor.xcodeproj/project.pbxproj";
    private const string AppTarget = $"{Repository}:{AppProject}#target:Compositor";
    private const string TestsTarget = $"{Repository}:{AppProject}#target:CompositorTests";
    private const string VendorProject = "vendor/Compositor/Compositor.xcodeproj/project.pbxproj";
    private const string VendorTarget = $"{Repository}:{VendorProject}#target:Compositor";
    private const string RendererTests = $"{Repository}:native/CompositorTests/RendererTests.swift";
    private const string VendorTests = $"{Repository}:vendor/Compositor/Tests/VendorTests.swift";
    private const string Canvas = $"{Repository}:native/Compositor/Canvas.swift";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-xcode-imports-{Guid.NewGuid():N}");
    private readonly InMemoryGraphStore _store = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// A project.pbxproj in Xcode's own layout: a PBXProject with project-level configurations and
    /// native targets with their own Debug/Release configurations.
    /// </summary>
    internal static string ProjectFile(
        params (string Name, string ProductType, string[] Debug, string[] Release)[] targets) =>
        ProjectFile(projectSettings: ["SDKROOT = macosx;"], targets);

    internal static string ProjectFile(
        string[] projectSettings,
        params (string Name, string ProductType, string[] Debug, string[] Release)[] targets)
    {
        string Settings(IEnumerable<string> lines) => string.Join("\n", lines.Select(line => $"\t\t\t\t{line}"));
        var objects = new List<string>
        {
            """
            		AA0000000000000000000001 /* Repository object */ = {
            			isa = PBXProject;
            			buildConfigurationList = AA0000000000000000000002 /* Build configuration list for PBXProject "Compositor" */;
            			targets = (
            """ + "\n" + string.Join("\n", targets.Select((_, i) => $"\t\t\t\tBB{i:D22} /* target */,")) + """

            			);
            		};
            		AA0000000000000000000002 /* Build configuration list for PBXProject "Compositor" */ = {
            			isa = XCConfigurationList;
            			buildConfigurations = (
            				AA0000000000000000000003 /* Debug */,
            				AA0000000000000000000004 /* Release */,
            			);
            		};
            """,
            $$"""
            		AA0000000000000000000003 /* Debug */ = {
            			isa = XCBuildConfiguration;
            			buildSettings = {
            {{Settings(projectSettings)}}
            			};
            			name = Debug;
            		};
            		AA0000000000000000000004 /* Release */ = {
            			isa = XCBuildConfiguration;
            			buildSettings = {
            {{Settings(projectSettings)}}
            			};
            			name = Release;
            		};
            """
        };
        for (var i = 0; i < targets.Length; i++)
        {
            var (name, productType, debug, release) = targets[i];
            objects.Add($$"""
                		BB{{i:D22}} /* {{name}} */ = {
                			isa = PBXNativeTarget;
                			buildConfigurationList = CC{{i:D22}} /* Build configuration list for PBXNativeTarget "{{name}}" */;
                			buildPhases = (
                			);
                			name = "{{name}}";
                			productName = "{{name}}";
                			productType = "{{productType}}";
                		};
                		CC{{i:D22}} /* Build configuration list */ = {
                			isa = XCConfigurationList;
                			buildConfigurations = (
                				DD{{i:D22}} /* Debug */,
                				EE{{i:D22}} /* Release */,
                			);
                			defaultConfigurationName = Release;
                		};
                		DD{{i:D22}} /* Debug */ = {
                			isa = XCBuildConfiguration;
                			buildSettings = {
                				CODE_SIGN_STYLE = Automatic;
                				LD_RUNPATH_SEARCH_PATHS = (
                					"$(inherited)",
                					"@executable_path/../Frameworks",
                				);
                {{Settings(debug)}}
                			};
                			name = Debug;
                		};
                		EE{{i:D22}} /* Release */ = {
                			isa = XCBuildConfiguration;
                			buildSettings = {
                {{Settings(release)}}
                			};
                			name = Release;
                		};
                """);
        }

        return $$"""
            // !$*UTF8*$!
            {
            	archiveVersion = 1;
            	classes = {
            	};
            	objectVersion = 77;
            	objects = {

            /* Begin PBXProject section */
            {{string.Join("\n", objects)}}
            /* End XCConfigurationList section */
            	};
            	rootObject = AA0000000000000000000001 /* Repository object */;
            }
            """;
    }

    private static string CompositorProject(string moduleName = "Compositor") => ProjectFile(
        ("Compositor", "com.apple.product-type.application",
            ["PRODUCT_NAME = LensWorks;", $"PRODUCT_MODULE_NAME = {moduleName};"],
            ["PRODUCT_NAME = LensWorks;", $"PRODUCT_MODULE_NAME = {moduleName};"]),
        ("CompositorTests", "com.apple.product-type.bundle.unit-test",
            ["PRODUCT_NAME = \"$(TARGET_NAME)\";", "TEST_HOST = \"$(BUILT_PRODUCTS_DIR)/LensWorks.app/Contents/MacOS/LensWorks\";"],
            ["PRODUCT_NAME = \"$(TARGET_NAME)\";"]));

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Delete(string relativePath) => File.Delete(Path.Combine(_root, relativePath));

    private Task IndexAsync() =>
        new IndexingPipeline(
                _store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance,
                swiftPackageManifestExtractor: new SwiftPackageManifestExtractor())
            .IndexRepositoryAsync(Repository, _root, ct: CancellationToken.None);

    private void WriteRepository()
    {
        Write(AppProject, CompositorProject());
        Write("native/Compositor/Canvas.swift", "import SwiftUI\nstruct Canvas {}\n");
        Write("native/CompositorTests/RendererTests.swift",
            "import XCTest\n@testable import Compositor\nfinal class RendererTests: XCTestCase {}\n");
        // A vendored copy of the app defines the same module: its own tests bind to it.
        Write(VendorProject, CompositorProject());
        Write("vendor/Compositor/Tests/VendorTests.swift", "@testable import Compositor\n");
        // An agent worktree copy is excluded from discovery and never becomes a provider.
        Write(".claude/worktrees/agent-1/native/Compositor.xcodeproj/project.pbxproj", CompositorProject());
    }

    private List<(string Source, string Target, string? MatchedBy, string? Confidence)> Imports()
    {
        var byId = _store.Nodes.ToDictionary(node => node.Id);
        return _store.Edges.Where(edge => edge.Type == EdgeType.IMPORTS)
            .Select(edge => (byId[edge.SourceId].QualifiedName, byId[edge.TargetId].QualifiedName,
                edge.Properties.GetValueOrDefault("matched_by")?.ToString(),
                edge.Properties.GetValueOrDefault("confidence")?.ToString()))
            .ToList();
    }

    [Fact]
    public async Task IndexRepository_TestableImportOfTheXcodeAppTarget_BindsTheNearestLocalModule()
    {
        WriteRepository();

        await IndexAsync();

        Imports().ShouldBe(
        [
            (Canvas, "swift-external-module:SwiftUI", "external", "high"),
            (RendererTests, "swift-external-module:XCTest", "external", "high"),
            (RendererTests, AppTarget, "target", "high"),
            (VendorTests, VendorTarget, "target", "high")
        ], ignoreOrder: true);
        _store.Nodes.ShouldNotContain(node => node.QualifiedName == "swift-external-module:Compositor");

        var app = _store.Nodes.Single(node => node.QualifiedName == AppTarget);
        app.Label.ShouldBe(NodeLabel.Module);
        app.Name.ShouldBe("Compositor");
        app.FilePath.ShouldBe(AppProject);
        app.Properties["module_name"].ToString().ShouldBe("Compositor");
        app.Properties["ecosystem"].ToString().ShouldBe("xcode");
        app.Properties["target_name"].ToString().ShouldBe("Compositor");
        app.Properties["project_directory"].ToString().ShouldBe("native");
        app.Properties["target_kind"].ToString().ShouldBe("com.apple.product-type.application");
        _store.Nodes.Single(node => node.QualifiedName == TestsTarget).Name.ShouldBe("CompositorTests");
        _store.Nodes.ShouldNotContain(node => node.FilePath != null && node.FilePath.StartsWith(".claude/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IndexRepository_ChangedXcodeModuleName_RebindsImportsOfUnchangedFiles()
    {
        WriteRepository();
        Delete(VendorProject);
        await IndexAsync();
        Imports().ShouldContain((RendererTests, AppTarget, "target", "high"));

        // The app now builds module CompositorKit: the unchanged test file's import is external.
        Write(AppProject, CompositorProject(moduleName: "CompositorKit"));
        await IndexAsync();

        Imports().Where(import => import.Source == RendererTests).ShouldBe(
        [
            (RendererTests, "swift-external-module:XCTest", "external", "high"),
            (RendererTests, "swift-external-module:Compositor", "external", "high")
        ], ignoreOrder: true);
        _store.Nodes.Single(node => node.QualifiedName == AppTarget).Name.ShouldBe("CompositorKit");

        // Back to Compositor: the import binds the target again and the stub is gone.
        Write(AppProject, CompositorProject());
        await IndexAsync();

        Imports().Where(import => import.Source == RendererTests)
            .ShouldContain((RendererTests, AppTarget, "target", "high"));
        _store.Nodes.ShouldNotContain(node => node.QualifiedName == "swift-external-module:Compositor");
    }

    [Fact]
    public async Task IndexRepository_RemovedXcodeProject_RemovesItsModulesAndRebindsImports()
    {
        WriteRepository();
        await IndexAsync();

        Delete(AppProject);
        await IndexAsync();

        _store.Nodes.ShouldNotContain(node => node.FilePath == AppProject);
        // The nearest provider is gone; the vendored copy is the only Compositor module left.
        Imports().Where(import => import.Source == RendererTests).ShouldBe(
        [
            (RendererTests, "swift-external-module:XCTest", "external", "high"),
            (RendererTests, VendorTarget, "target", "high")
        ], ignoreOrder: true);

        Delete(VendorProject);
        await IndexAsync();

        _store.Nodes.ShouldNotContain(node => node.FilePath == VendorProject);
        Imports().Where(import => import.Source == RendererTests)
            .ShouldContain((RendererTests, "swift-external-module:Compositor", "external", "high"));
    }

    /// <summary>
    /// Dual distribution: a root Package.swift and a root Alamo.xcodeproj both build module Alamo.
    /// The SwiftPM target is the canonical provider: one high-confidence edge, never an ambiguous pair.
    /// </summary>
    [Fact]
    public async Task IndexRepository_SwiftPmAndXcodeTargetOfTheSameModule_BindsOnlyTheSwiftPmTarget()
    {
        Write("Package.swift", """
            // swift-tools-version:5.9
            import PackageDescription
            let package = Package(
                name: "Alamo",
                targets: [
                    .target(name: "Alamo"),
                    .testTarget(name: "AlamoTests", dependencies: ["Alamo"], path: "Tests"),
                ]
            )
            """);
        Write("Alamo.xcodeproj/project.pbxproj", ProjectFile(
            ("Alamo", "com.apple.product-type.framework", ["PRODUCT_NAME = Alamo;"], ["PRODUCT_NAME = Alamo;"])));
        Write("Sources/Alamo/Session.swift", "public struct Session {}\n");
        Write("Tests/T.swift", "@testable import Alamo\n");

        await IndexAsync();

        _store.Nodes.ShouldContain(node => node.QualifiedName == $"{Repository}:Alamo.xcodeproj/project.pbxproj#target:Alamo");
        Imports().Where(import => import.Source == $"{Repository}:Tests/T.swift").ShouldBe(
            [($"{Repository}:Tests/T.swift", $"{Repository}:Package.swift#target:Alamo", "target", "high")]);
    }

    [Fact]
    public void ReadNativeTargets_DerivesTheSwiftModuleNameFromBuildSettings()
    {
        var content = ProjectFile(
            projectSettings: ["PRODUCT_NAME = \"$(TARGET_NAME)\";"],
            // PRODUCT_MODULE_NAME wins over PRODUCT_NAME.
            ("Compositor", "com.apple.product-type.application",
                ["PRODUCT_NAME = LensWorks;", "PRODUCT_MODULE_NAME = Compositor;"],
                ["PRODUCT_NAME = LensWorks;", "PRODUCT_MODULE_NAME = Compositor;"]),
            // PRODUCT_NAME from $(TARGET_NAME), made a C99 identifier.
            ("My App", "com.apple.product-type.application",
                ["PRODUCT_NAME = \"$(TARGET_NAME)\";"], ["PRODUCT_NAME = \"$(TARGET_NAME)\";"]),
            // Xcode's own default expression for the module name.
            ("Kit", "com.apple.product-type.framework",
                ["PRODUCT_NAME = \"Foo-Bar.Kit\";", "PRODUCT_MODULE_NAME = \"$(PRODUCT_NAME:c99extidentifier)\";"],
                ["PRODUCT_NAME = \"Foo-Bar.Kit\";", "PRODUCT_MODULE_NAME = \"$(PRODUCT_NAME:c99extidentifier)\";"]),
            // A module name from an .xcconfig is not literal: fall back to PRODUCT_NAME.
            ("Configured", "com.apple.product-type.framework",
                ["PRODUCT_NAME = Shared;", "PRODUCT_MODULE_NAME = \"$(SHARED_MODULE)\";"],
                ["PRODUCT_NAME = Shared;", "PRODUCT_MODULE_NAME = \"$(SHARED_MODULE)\";"]),
            // No target PRODUCT_NAME: the repository-level $(TARGET_NAME) applies.
            ("3DEngine", "com.apple.product-type.framework", [], []),
            // Configurations disagree: Release wins.
            ("Flavored", "com.apple.product-type.application",
                ["PRODUCT_MODULE_NAME = FlavoredDebug;"], ["PRODUCT_MODULE_NAME = FlavoredRelease;"]),
            // A non-literal PRODUCT_NAME falls back to the target name.
            ("Unresolved", "com.apple.product-type.tool",
                ["PRODUCT_NAME = \"$(SOMETHING_ELSE)\";"], ["PRODUCT_NAME = \"$(SOMETHING_ELSE)\";"]));

        XcodeProjectReader.ReadNativeTargets(content)
            .Select(target => (target.Name, target.ModuleName))
            .ShouldBe([
                ("Compositor", "Compositor"),
                ("My App", "My_App"),
                ("Kit", "Foo_Bar_Kit"),
                ("Configured", "Shared"),
                ("3DEngine", "_3DEngine"),
                ("Flavored", "FlavoredRelease"),
                ("Unresolved", "Unresolved")
            ]);
    }

    [Fact]
    public void ReadNativeTargets_SkipsAggregateTargetsAndReadsQuotedEscapedValues()
    {
        var content = """
            // !$*UTF8*$!
            {
            	objects = {
            		A1 = {isa = PBXAggregateTarget; name = Docs; };
            		A2 = {isa = PBXNativeTarget; buildConfigurationList = A3; name = "Quoted \"Name\""; productType = "com.apple.product-type.framework"; };
            		A3 = {isa = XCConfigurationList; buildConfigurations = (A4, ); };
            		A4 = {isa = XCBuildConfiguration; buildSettings = { PRODUCT_MODULE_NAME = 'Quoted_Name'; /* comment ; } */ OTHER = <0a0b>; }; name = Release; };
            	};
            }
            """;

        XcodeProjectReader.ReadNativeTargets(content)
            .ShouldHaveSingleItem()
            .ShouldBe(new XcodeNativeTarget("Quoted \"Name\"", "Quoted_Name", "com.apple.product-type.framework"));
    }

    [Fact]
    public void ReadNativeTargets_MalformedRepository_Throws()
    {
        Should.Throw<FormatException>(() => XcodeProjectReader.ReadNativeTargets("{ objects = { A = { isa = "));
    }
}
