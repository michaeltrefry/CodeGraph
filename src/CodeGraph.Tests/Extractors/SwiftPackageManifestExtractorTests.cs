using CodeGraph.Models;
using CodeGraph.Services.Extractors;
using Shouldly;

namespace CodeGraph.Tests.Extractors;

public class SwiftPackageManifestExtractorTests
{
    private static readonly string RootPath =
        Path.Combine(Path.GetTempPath(), "swiftpm-manifest-fixture");

    private static readonly ExtractorContext Context = new()
    {
        ProjectName = "SwiftRepo",
        RootPath = RootPath
    };

    private const string PackageSwift = """
        // swift-tools-version:5.9
        import PackageDescription

        let package = Package(
            name: "ShapeKit",
            platforms: [.macOS(.v13)],
            products: [.library(name: "ShapeKit", targets: ["ShapeKit"])],
            dependencies: [
                .package(url: "https://github.com/apple/swift-argument-parser.git", from: "1.2.0"),
                .package(url: "https://github.com/apple/swift-log", exact: "1.5.3"),
                .package(url: "https://github.com/apple/swift-nio.git", branch: "main"),
                .package(url: "https://github.com/pointfreeco/swift-snapshot-testing", revision: "abc123"),
                .package(url: "https://github.com/apple/swift-collections.git", .upToNextMajor(from: "1.0.0")),
                .package(url: "https://github.com/apple/swift-algorithms", .upToNextMinor(from: "1.1.0")),
                .package(url: "https://github.com/apple/swift-crypto.git", "2.0.0"..<"4.0.0"),
                .package(url: "https://github.com/apple/swift-numerics", "1.0.0"..."1.0.2"),
                .package(name: "Sparkle", url: "git@github.com:sparkle-project/Sparkle.git", from: "2.6.0"),
                .package(path: "../SiblingKit"),
                .package(path: "Packages/Core"),
                .package(id: "mona.LinkedList", from: "1.0.0"),
                // .package(url: "https://github.com/commented/out-line", from: "9.9.9"),
                /* .package(url: "https://github.com/commented/out-block", from: "9.9.9"), */
            ],
            targets: [
                .target(name: "ShapeKit", dependencies: [
                    .product(name: "ArgumentParser", package: "swift-argument-parser"),
                ]),
            ]
        )
        """;

    private const string CorePackageSwift = """
        // swift-tools-version:5.9
        import PackageDescription

        let package = Package(name: "Core", targets: [.target(name: "Core")])
        """;

    private const string ResolvedV1 = """
        {
          "object": {
            "pins": [
              {
                "package": "SwiftArgumentParser",
                "repositoryURL": "https://github.com/apple/swift-argument-parser.git",
                "state": {
                  "branch": null,
                  "revision": "fee6933f37fde9a5e12a1e4aeaa93fe60116ff2a",
                  "version": "1.2.2"
                }
              },
              {
                "package": "swift-nio",
                "repositoryURL": "https://github.com/apple/swift-nio.git",
                "state": {
                  "branch": "main",
                  "revision": "0123456789abcdef0123456789abcdef01234567",
                  "version": null
                }
              }
            ]
          },
          "version": 1
        }
        """;

    private const string ResolvedV2 = """
        {
          "pins" : [
            {
              "identity" : "swift-log",
              "kind" : "remoteSourceControl",
              "location" : "https://github.com/apple/swift-log",
              "state" : {
                "revision" : "532d8b529501fb73a2455b179e0bbb6d49b652ed",
                "version" : "1.5.3"
              }
            },
            {
              "identity" : "linkedlist",
              "kind" : "registry",
              "location" : "",
              "state" : {
                "version" : "1.0.4"
              }
            }
          ],
          "version" : 2
        }
        """;

    private const string ResolvedV3 = """
        {
          "originHash" : "e721da7f9826abdffcb6185e886155efa2514bd6234475f1afa893e29eb258d6",
          "pins" : [
            {
              "identity" : "sparkle",
              "kind" : "remoteSourceControl",
              "location" : "https://github.com/sparkle-project/Sparkle",
              "state" : {
                "revision" : "eef1a539a373c1f1a320624b1130fc5de7b2e100",
                "version" : "2.10.0"
              }
            },
            {
              "identity" : "localkit",
              "kind" : "localSourceControl",
              "location" : "/Users/dev/LocalKit",
              "state" : {
                "branch" : "develop",
                "revision" : "1111111111111111111111111111111111111111"
              }
            }
          ],
          "version" : 3
        }
        """;

    private static string Full(string relative) => Path.Combine(RootPath, relative);

    private static ExtractionResult Extract(
        IReadOnlyDictionary<string, string> manifests,
        bool hasXcodeProject = false) =>
        new SwiftPackageManifestExtractor().Extract(manifests, hasXcodeProject, Context);

    private static GraphNode Dependency(ExtractionResult result, string identity, string kind) =>
        result.Nodes.Where(node =>
                node.Label == NodeLabel.Package &&
                (string)node.Properties["identity"] == identity &&
                !(bool)node.Properties["is_definition"] &&
                (string)node.Properties["dependency_kind"] == kind)
            .ShouldHaveSingleItem();

    [Fact]
    public void Extract_PackageSwift_EmitsDefinitionAndEveryDeclaredDependencyForm()
    {
        var result = Extract(new Dictionary<string, string>
        {
            [Full("Package.swift")] = PackageSwift
        });

        result.Metadata.ShouldBe(new ProjectMetadata("Swift", "SwiftPM"));
        result.ProcessedFiles.ShouldBe(["Package.swift"]);

        var definition = result.Nodes.Single(node => (bool)node.Properties["is_definition"]);
        definition.Name.ShouldBe("ShapeKit");
        definition.QualifiedName.ShouldBe("package:swiftpm:local:swiftrepo:shapekit@*");
        definition.FilePath.ShouldBe("Package.swift");
        definition.Properties["ecosystem"].ShouldBe("swiftpm");
        result.Edges.ShouldContain(edge =>
            edge.SourceQN == "SwiftRepo" &&
            edge.TargetQN == definition.QualifiedName &&
            edge.Type == EdgeType.CONTAINS_PROJECT);

        AssertDeclared(result, "swift-argument-parser",
            "https://github.com/apple/swift-argument-parser.git",
            "github.com/apple/swift-argument-parser", "1.2.0", "upToNextMajor:1.2.0");
        AssertDeclared(result, "swift-log",
            "https://github.com/apple/swift-log",
            "github.com/apple/swift-log", "1.5.3", "exact:1.5.3");
        var nio = AssertDeclared(result, "swift-nio",
            "https://github.com/apple/swift-nio.git",
            "github.com/apple/swift-nio", "", "branch:main");
        nio.Properties["git_reference"].ShouldBe("branch:main");
        var snapshot = AssertDeclared(result, "swift-snapshot-testing",
            "https://github.com/pointfreeco/swift-snapshot-testing",
            "github.com/pointfreeco/swift-snapshot-testing", "", "revision:abc123");
        snapshot.Properties["git_reference"].ShouldBe("rev:abc123");
        AssertDeclared(result, "swift-collections",
            "https://github.com/apple/swift-collections.git",
            "github.com/apple/swift-collections", "1.0.0", "upToNextMajor:1.0.0");
        AssertDeclared(result, "swift-algorithms",
            "https://github.com/apple/swift-algorithms",
            "github.com/apple/swift-algorithms", "1.1.0", "upToNextMinor:1.1.0");
        AssertDeclared(result, "swift-crypto",
            "https://github.com/apple/swift-crypto.git",
            "github.com/apple/swift-crypto", "2.0.0", "range:2.0.0..<4.0.0");
        AssertDeclared(result, "swift-numerics",
            "https://github.com/apple/swift-numerics",
            "github.com/apple/swift-numerics", "1.0.0", "range:1.0.0...1.0.2");

        var sparkle = AssertDeclared(result, "sparkle",
            "git@github.com:sparkle-project/Sparkle.git",
            "github.com/sparkle-project/sparkle", "2.6.0", "upToNextMajor:2.6.0");
        sparkle.Name.ShouldBe("sparkle");
        sparkle.Properties["package_name"].ShouldBe("Sparkle");
        sparkle.QualifiedName.ShouldBe(
            "package:swiftpm:scm:github.com/sparkle-project/sparkle@upToNextMajor:2.6.0");
        sparkle.Properties["source_kind"].ShouldBe("remote");

        var registry = Dependency(result, "mona.linkedlist", "declared");
        registry.Properties["source_kind"].ShouldBe("registry");
        registry.Properties["location"].ShouldBe("mona.LinkedList");
        registry.Properties["package_key"].ShouldBe("swiftpm:registry:mona.linkedlist");
        registry.Properties["version"].ShouldBe("1.0.0");

        var sibling = Dependency(result, "siblingkit", "declared");
        sibling.Properties["source_kind"].ShouldBe("path");
        sibling.Properties["location"].ShouldBe("../SiblingKit");
        sibling.Properties["external_path"].ShouldBe("../SiblingKit");
        sibling.Properties["package_key"].ShouldBe("swiftpm:path:swiftrepo:../siblingkit");
        sibling.QualifiedName.ShouldBe("package:swiftpm:path:swiftrepo:../siblingkit@*");

        // The in-repo path dependency has no Package.swift in this manifest set: it stays a path node.
        var core = Dependency(result, "core", "declared");
        core.Properties["source"].ShouldBe("Packages/Core");
        core.Properties.ContainsKey("external_path").ShouldBeFalse();

        result.Nodes.ShouldNotContain(node => node.Name.StartsWith("out-", StringComparison.Ordinal));
        result.Nodes.Count(node => !(bool)node.Properties["is_definition"]).ShouldBe(12);
        result.Edges.Count(edge =>
                edge.Type == EdgeType.REFERENCES_PACKAGE && edge.SourceQN == definition.QualifiedName)
            .ShouldBe(12);
    }

    [Fact]
    public void Extract_InRepoPathDependency_PointsAtTheLocalPackageDefinition()
    {
        var result = Extract(new Dictionary<string, string>
        {
            [Full("Package.swift")] = PackageSwift,
            [Full("Packages/Core/Package.swift")] = CorePackageSwift
        });

        var coreDefinition = result.Nodes.Single(node =>
            node.Name == "Core" && (bool)node.Properties["is_definition"]);
        coreDefinition.FilePath.ShouldBe("Packages/Core/Package.swift");
        result.Nodes.ShouldNotContain(node =>
            !(bool)node.Properties["is_definition"] && (string)node.Properties["identity"] == "core");
        result.Edges.ShouldContain(edge =>
            edge.SourceQN == "package:swiftpm:local:swiftrepo:shapekit@*" &&
            edge.TargetQN == coreDefinition.QualifiedName &&
            edge.Type == EdgeType.REFERENCES_PACKAGE &&
            (string)edge.Properties!["source_kind"] == "path");
        result.ProcessedFiles.ShouldBe(["Package.swift", "Packages/Core/Package.swift"]);
    }

    [Fact]
    public void Extract_ResolvedV1_EmitsPinsWithLocationVersionAndRevision()
    {
        var result = Extract(new Dictionary<string, string>
        {
            [Full("Package.resolved")] = ResolvedV1
        });

        var parser = Dependency(result, "swift-argument-parser", "pinned");
        parser.Name.ShouldBe("swift-argument-parser");
        parser.FilePath.ShouldBe("Package.resolved");
        parser.QualifiedName.ShouldBe("package:swiftpm:scm:github.com/apple/swift-argument-parser@1.2.2");
        parser.Properties["package_name"].ShouldBe("SwiftArgumentParser");
        parser.Properties["location"].ShouldBe("https://github.com/apple/swift-argument-parser.git");
        parser.Properties["source"].ShouldBe("github.com/apple/swift-argument-parser");
        parser.Properties["package_key"].ShouldBe("swiftpm:scm:github.com/apple/swift-argument-parser");
        parser.Properties["version"].ShouldBe("1.2.2");
        parser.Properties["revision"].ShouldBe("fee6933f37fde9a5e12a1e4aeaa93fe60116ff2a");
        parser.Properties["source_kind"].ShouldBe("remote");
        parser.Properties["resolved_format_version"].ShouldBe(1);
        parser.Properties.ContainsKey("branch").ShouldBeFalse();

        var nio = Dependency(result, "swift-nio", "pinned");
        nio.QualifiedName.ShouldBe(
            "package:swiftpm:scm:github.com/apple/swift-nio@0123456789abcdef0123456789abcdef01234567");
        nio.Properties["version"].ShouldBe("");
        nio.Properties["branch"].ShouldBe("main");
        nio.Properties["revision"].ShouldBe("0123456789abcdef0123456789abcdef01234567");

        result.Edges.Where(edge => edge.Type == EdgeType.REFERENCES_PACKAGE)
            .Select(edge => (edge.SourceQN, edge.TargetQN))
            .ShouldBe([("SwiftRepo", parser.QualifiedName), ("SwiftRepo", nio.QualifiedName)], ignoreOrder: true);
        // Resolved pins alone do not classify a repository.
        result.Metadata.ShouldBeNull();
    }

    [Fact]
    public void Extract_ResolvedV2_EmitsSourceControlAndRegistryPins()
    {
        var result = Extract(new Dictionary<string, string>
        {
            [Full("Package.resolved")] = ResolvedV2
        });

        var log = Dependency(result, "swift-log", "pinned");
        log.QualifiedName.ShouldBe("package:swiftpm:scm:github.com/apple/swift-log@1.5.3");
        log.Properties["location"].ShouldBe("https://github.com/apple/swift-log");
        log.Properties["version"].ShouldBe("1.5.3");
        log.Properties["revision"].ShouldBe("532d8b529501fb73a2455b179e0bbb6d49b652ed");
        log.Properties["pin_kind"].ShouldBe("remoteSourceControl");
        log.Properties["resolved_format_version"].ShouldBe(2);

        var registry = Dependency(result, "linkedlist", "pinned");
        registry.QualifiedName.ShouldBe("package:swiftpm:registry:linkedlist@1.0.4");
        registry.Properties["source_kind"].ShouldBe("registry");
        registry.Properties["pin_kind"].ShouldBe("registry");
        registry.Properties["version"].ShouldBe("1.0.4");
        registry.Properties.ContainsKey("revision").ShouldBeFalse();
    }

    [Fact]
    public void Extract_ResolvedV3_EmitsRemoteAndLocalSourceControlPins()
    {
        var resolvedPath = "App.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved";
        var result = Extract(new Dictionary<string, string>
        {
            [Full(resolvedPath)] = ResolvedV3
        }, hasXcodeProject: true);

        result.Metadata.ShouldBe(new ProjectMetadata("Swift", "Xcode"));
        result.ProcessedFiles.ShouldBe([resolvedPath]);

        var sparkle = Dependency(result, "sparkle", "pinned");
        sparkle.FilePath.ShouldBe(resolvedPath);
        sparkle.QualifiedName.ShouldBe("package:swiftpm:scm:github.com/sparkle-project/sparkle@2.10.0");
        sparkle.Properties["location"].ShouldBe("https://github.com/sparkle-project/Sparkle");
        sparkle.Properties["version"].ShouldBe("2.10.0");
        sparkle.Properties["revision"].ShouldBe("eef1a539a373c1f1a320624b1130fc5de7b2e100");
        sparkle.Properties["resolved_format_version"].ShouldBe(3);

        var local = Dependency(result, "localkit", "pinned");
        local.Properties["source_kind"].ShouldBe("local");
        local.Properties["location"].ShouldBe("/Users/dev/LocalKit");
        local.Properties["branch"].ShouldBe("develop");
        local.QualifiedName.ShouldBe(
            "package:swiftpm:scm:/users/dev/localkit@1111111111111111111111111111111111111111");
    }

    [Fact]
    public void Extract_SamePinInSeveralResolvedFiles_IsOneNodeOwnedByTheFirstPathInOrdinalOrder()
    {
        var manifests = new Dictionary<string, string>
        {
            [Full("b.xcworkspace/xcshareddata/swiftpm/Package.resolved")] = ResolvedV3,
            [Full("a.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved")] = ResolvedV3,
            [Full("Package.resolved")] = ResolvedV2
        };

        var first = Extract(manifests, hasXcodeProject: true);
        var second = Extract(manifests.Reverse().ToDictionary(), hasXcodeProject: true);

        var sparkle = first.Nodes.Where(node => node.Name == "sparkle").ShouldHaveSingleItem();
        sparkle.FilePath.ShouldBe("a.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved");
        first.Edges.Count(edge => edge.TargetQN == sparkle.QualifiedName).ShouldBe(1);
        first.Nodes.Count.ShouldBe(4);
        second.Nodes.Select(node => (node.QualifiedName, node.FilePath))
            .ShouldBe(first.Nodes.Select(node => (node.QualifiedName, node.FilePath)));
        first.ProcessedFiles.ShouldBe([
            "Package.resolved",
            "a.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved",
            "b.xcworkspace/xcshareddata/swiftpm/Package.resolved"
        ]);
    }

    [Fact]
    public void Extract_MalformedManifest_IsSkippedAndTheRestStillExtracts()
    {
        var result = Extract(new Dictionary<string, string>
        {
            [Full("Broken/Package.resolved")] = "{ \"pins\": [ { \"identity\": ",
            [Full("Package.resolved")] = ResolvedV2
        });

        result.Succeeded.ShouldBeTrue();
        result.ProcessedFiles.ShouldBe(["Package.resolved"]);
        result.Nodes.Select(node => node.Name).ShouldBe(["swift-log", "linkedlist"], ignoreOrder: true);
    }

    [Fact]
    public void Extract_PackageSwift_IgnoresPackageTextInsideStringsAndReadsQualifiedCalls()
    {
        var result = Extract(new Dictionary<string, string>
        {
            [Full("Package.swift")] = """"
                import PackageDescription
                let note = "see .package(url: \"https://example.test/in-string\", from: \"1.0.0\") )"
                let doc = """
                    .package(url: "https://example.test/in-multiline", from: "1.0.0")
                    """
                var deps: [Package.Dependency] = [
                    Package.Dependency.package(url: "https://example.test/qualified.git", exact: "3.1.4"),
                ]
                let package = Package(name: "Strings", dependencies: deps)
                """"
        });

        var dependencies = result.Nodes.Where(node => !(bool)node.Properties["is_definition"]).ToList();
        dependencies.Select(node => node.QualifiedName)
            .ShouldBe(["package:swiftpm:scm:example.test/qualified@exact:3.1.4"]);
        result.Nodes.Single(node => (bool)node.Properties["is_definition"]).Name.ShouldBe("Strings");
    }

    [Fact]
    public void Extract_XcodeProjectWithoutManifests_ClassifiesSwiftXcode()
    {
        var result = Extract(new Dictionary<string, string>(), hasXcodeProject: true);

        result.Metadata.ShouldBe(new ProjectMetadata("Swift", "Xcode"));
        result.Nodes.ShouldBeEmpty();
    }

    [Fact]
    public void Extract_XcodeProjectWithPackageSwift_PrefersXcode()
    {
        var result = Extract(new Dictionary<string, string>
        {
            [Full("Package.swift")] = CorePackageSwift
        }, hasXcodeProject: true);

        result.Metadata.ShouldBe(new ProjectMetadata("Swift", "Xcode"));
    }

    private static GraphNode AssertDeclared(
        ExtractionResult result,
        string identity,
        string location,
        string source,
        string version,
        string requirement)
    {
        var node = Dependency(result, identity, "declared");
        node.FilePath.ShouldBe("Package.swift");
        node.Properties["ecosystem"].ShouldBe("swiftpm");
        node.Properties["location"].ShouldBe(location);
        node.Properties["source"].ShouldBe(source);
        node.Properties["package_key"].ShouldBe($"swiftpm:scm:{source}");
        node.Properties["version"].ShouldBe(version);
        node.Properties["version_requirement"].ShouldBe(requirement);
        node.QualifiedName.ShouldBe($"package:swiftpm:scm:{source}@{requirement}");
        node.Properties["source_kind"].ShouldBe("remote");
        node.Properties["manifest_path"].ShouldBe("Package.swift");
        return node;
    }
}
