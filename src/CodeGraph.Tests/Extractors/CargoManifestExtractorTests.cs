using CodeGraph.Models;
using CodeGraph.Services.Extractors;
using Shouldly;

namespace CodeGraph.Tests.Extractors;

public class CargoManifestExtractorTests
{
    [Fact]
    public void Extract_IndexesWorkspacePackagesAndFullDependencyIdentity()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"cargo-manifest-{Guid.NewGuid():N}");
        var rootManifest = Path.Combine(rootPath, "Cargo.toml");
        var appManifest = Path.Combine(rootPath, "crates", "app", "Cargo.toml");
        var providerManifest = Path.Combine(rootPath, "crates", "provider", "Cargo.toml");
        var manifests = new Dictionary<string, string>
        {
            [rootManifest] = """
                [workspace]
                members = ["crates/app", "crates/provider"]

                [workspace.package]
                version = "2.4.0"

                [workspace.dependencies]
                renamed-serde = { package = "serde", version = "1.0", optional = true }
                remote-api = { git = "https://example.test/remote-api.git", rev = "abc123" }
                """,
            [appManifest] = """
                [package]
                name = "app"
                version.workspace = true
                publish = false

                [dependencies]
                renamed-serde.workspace = true
                remote-api.workspace = true
                provider = { path = "../provider" }

                [target.'cfg(unix)'.dev-dependencies]
                tempfile = "3.12"
                """,
            [providerManifest] = """
                [package]
                name = "provider"
                version.workspace = true
                publish = false
                """
        };
        var context = new ExtractorContext
        {
            ProjectName = "RustWorkspace",
            RootPath = rootPath
        };

        var result = new CargoManifestExtractor().Extract(manifests, context);

        result.Metadata.ShouldBe(new ProjectMetadata("Rust", "Cargo"));

        var definitions = result.Nodes
            .Where(node => node.Label == NodeLabel.Package &&
                           (bool)node.Properties["is_definition"])
            .ToList();
        definitions.Select(node => node.Name).ShouldBe(["app", "provider"], ignoreOrder: true);
        definitions.ShouldAllBe(node => node.Properties["version"].ToString() == "2.4.0");

        var serde = result.Nodes.Single(node =>
            node.Label == NodeLabel.Package &&
            node.Name == "serde" &&
            !(bool)node.Properties["is_definition"]);
        serde.Properties["local_name"].ShouldBe("renamed-serde");
        serde.Properties["package_key"].ShouldBe("cargo:registry:crates.io:serde");
        serde.Properties["workspace_inherited"].ShouldBe(true);
        serde.Properties["optional"].ShouldBe(true);

        var remote = result.Nodes.Single(node => node.Name == "remote-api");
        remote.Properties["source_kind"].ShouldBe("git");
        remote.Properties["source"].ShouldBe("https://example.test/remote-api");
        remote.Properties["git_reference"].ShouldBe("rev:abc123");

        var providerDefinition = definitions.Single(node => node.Name == "provider");
        result.Edges.ShouldContain(edge =>
            edge.TargetQN == providerDefinition.QualifiedName &&
            edge.Type == EdgeType.REFERENCES_PACKAGE &&
            edge.Properties!["source_kind"].ToString() == "path");

        var targetDependency = result.Nodes.Single(node => node.Name == "tempfile");
        targetDependency.Properties["dependency_scope"].ShouldBe("dev-dependencies");
        targetDependency.Properties["target"].ShouldBe("cfg(unix)");
    }

    [Fact]
    public void Extract_DistinguishesRegistryGitAndWorkspaceOnlyDefinitions()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"cargo-identity-{Guid.NewGuid():N}");
        var manifests = new Dictionary<string, string>
        {
            [Path.Combine(rootPath, "Cargo.toml")] = """
                [package]
                name = "public-lib"
                version = "1.0.0"

                [dependencies]
                registry-lib = { version = "2", registry = "internal" }
                git-lib = { git = "https://example.test/git-lib", branch = "stable" }
                """
        };

        var result = new CargoManifestExtractor().Extract(manifests, new ExtractorContext
        {
            ProjectName = "PublicLib",
            RootPath = rootPath
        });

        var definition = result.Nodes.Single(node =>
            node.Name == "public-lib" && (bool)node.Properties["is_definition"]);
        definition.Properties["package_key"].ShouldBe("cargo:registry:crates.io:public-lib");

        var registry = result.Nodes.Single(node => node.Name == "registry-lib");
        registry.Properties["package_key"].ShouldBe("cargo:registry:internal:registry-lib");

        var git = result.Nodes.Single(node => node.Name == "git-lib");
        git.Properties["package_key"].ShouldBe(
            "cargo:git:https://example.test/git-lib:git-lib");
        git.Properties["git_reference"].ShouldBe("branch:stable");
    }
}
