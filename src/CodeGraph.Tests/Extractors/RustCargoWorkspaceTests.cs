using System.Text.Json;
using CodeGraph.Extractors.Rust;
using Shouldly;

namespace CodeGraph.Tests.Extractors;

public sealed class RustCargoWorkspaceTests
{
    private const string Root = "/repo";

    [Fact]
    public void FormGroups_OrdersMembersByDirectoryThenNameAndChunks()
    {
        var workspace = RustCargoWorkspace.Parse(Metadata());

        var groups = workspace.FormGroups(new RustPathMapper(Root, Root), groupSize: 2);

        groups.Select(group => string.Join(",", group.Crates)).ShouldBe(["app,core", "util"]);
        groups.Select(group => string.Join(",", group.SourceDirectories))
            .ShouldBe(["crates/app,crates/core", "crates/util"]);
    }

    [Fact]
    public void BuildProjectJson_MakesOnlyGroupCratesMembersAndKeepsCargoIdentity()
    {
        var workspace = RustCargoWorkspace.Parse(Metadata());
        var group = workspace.FormGroups(new RustPathMapper(Root, Root), groupSize: 1)[0]; // app

        using var project = JsonDocument.Parse(
            workspace.BuildProjectJson(group, new RustPathMapper(Root, "/scratch/root"), "/sysroot", buildOutputs: null));

        var crates = project.RootElement.GetProperty("crates").EnumerateArray().ToList();
        project.RootElement.GetProperty("sysroot_src").GetString()
            .ShouldBe(Path.Combine("/sysroot", "lib", "rustlib", "src", "rust", "library"));
        crates.Where(crate => crate.GetProperty("is_workspace_member").GetBoolean())
            .Select(crate => crate.GetProperty("root_module").GetString())
            .ShouldBe(["/scratch/root/crates/app/src/lib.rs", "/scratch/root/crates/app/src/main.rs", "/scratch/root/crates/app/build.rs"]);
        // Other members and dependencies are library crates (lib target only).
        crates.Where(crate => !crate.GetProperty("is_workspace_member").GetBoolean())
            .Select(crate => crate.GetProperty("display_name").GetString())
            .ShouldBe(["core", "serde", "test-helpers", "util"], ignoreOrder: true);
        var serde = crates.Single(crate => crate.GetProperty("display_name").GetString() == "serde");
        serde.GetProperty("root_module").GetString().ShouldBe("/registry/serde-1.0.0/src/lib.rs");
        serde.GetProperty("version").GetString().ShouldBe("1.0.0");

        var lib = crates[0];
        lib.GetProperty("display_name").GetString().ShouldBe("app");
        lib.GetProperty("cfg").EnumerateArray().Select(cfg => cfg.GetString()).ShouldBe(["feature=\"default\""]);
        lib.GetProperty("env").GetProperty("CARGO_MANIFEST_DIR").GetString().ShouldBe("/scratch/root/crates/app");
        lib.GetProperty("env").GetProperty("CARGO_PKG_VERSION_MINOR").GetString().ShouldBe("2");
        // Like rust-analyzer's Cargo mode, a member lib sees its dev-dependencies: its
        // #[cfg(test)] modules (cfg(test) is on for workspace members) reference them.
        DependencyNames(crates, lib).ShouldBe(["core", "test_helpers"], ignoreOrder: true);

        // A library crate outside the group does not get its dev-dependencies.
        var core = crates.Single(crate => crate.GetProperty("display_name").GetString() == "core");
        DependencyNames(crates, core).ShouldBe(["util"]);

        var bin = crates[1];
        DependencyNames(crates, bin).ShouldBe(["app", "core", "test_helpers"], ignoreOrder: true);
        var buildScript = crates[2];
        DependencyNames(crates, buildScript).ShouldBe(["serde"]);
        buildScript.GetProperty("cfg").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void RustBuildOutputs_ReadsBuildScriptsAndProcMacroDylibsOnlyWhenEnabled()
    {
        const string messages = """
            not json
            {"reason":"build-script-executed","package_id":"p1","cfgs":["generated"],"env":[["KEY","VALUE"]],"out_dir":"/out/p1"}
            {"reason":"compiler-artifact","package_id":"p2","target":{"kind":["proc-macro"]},"filenames":["/t/libp2.dylib"]}
            """;
        var withoutMacros = new RustBuildOutputs(includeProcMacros: false);
        var withMacros = new RustBuildOutputs(includeProcMacros: true);
        withoutMacros.AddCargoMessages(messages);
        withMacros.AddCargoMessages(messages);

        var output = withoutMacros.For("p1").ShouldNotBeNull();
        output.Cfgs.ShouldBe(["generated"]);
        output.Env.ShouldBe([("KEY", "VALUE")]);
        output.OutDir.ShouldBe("/out/p1");
        withoutMacros.ProcMacroDylib("p2").ShouldBeNull();
        withMacros.ProcMacroDylib("p2").ShouldBe("/t/libp2.dylib");
    }

    private static IReadOnlyList<string> DependencyNames(List<JsonElement> crates, JsonElement crate) =>
        crate.GetProperty("deps").EnumerateArray()
            .Select(dep => dep.GetProperty("name").GetString()!)
            .ToList();

    private static string Metadata() => JsonSerializer.Serialize(new
    {
        packages = new object[]
        {
            Package("util", "/repo/crates/util", "0.1.0", Lib("util", "/repo/crates/util/src/lib.rs")),
            Package("app", "/repo/crates/app", "1.2.3",
                Lib("app", "/repo/crates/app/src/lib.rs"),
                Target("app", "bin", "/repo/crates/app/src/main.rs"),
                Target("build-script-build", "custom-build", "/repo/crates/app/build.rs")),
            Package("core", "/repo/crates/core", "0.1.0", Lib("core", "/repo/crates/core/src/lib.rs")),
            Package("serde", "/registry/serde-1.0.0", "1.0.0", Lib("serde", "/registry/serde-1.0.0/src/lib.rs"),
                source: "registry+https://github.com/rust-lang/crates.io-index"),
            Package("test-helpers", "/repo/crates/test-helpers", "0.1.0",
                Lib("test-helpers", "/repo/crates/test-helpers/src/lib.rs"))
        },
        workspace_members = new[] { Id("util"), Id("app"), Id("core") },
        workspace_root = Root,
        resolve = new
        {
            nodes = new object[]
            {
                Node("util"),
                Node("app", ["default"],
                    Dep("core", "core", null),
                    Dep("serde", "serde", "build"),
                    Dep("test_helpers", "test-helpers", "dev")),
                Node("core", null,
                    Dep("util", "util", null),
                    Dep("test_helpers", "test-helpers", "dev")),
                Node("serde"),
                Node("test-helpers")
            }
        }
    });

    private static string Id(string name) => $"id-{name}";

    private static object Package(string name, string directory, string version, params object[] targets) =>
        Package(name, directory, version, targets, source: null);

    private static object Package(string name, string directory, string version, object target, string? source) =>
        Package(name, directory, version, [target], source);

    private static object Package(string name, string directory, string version, object[] targets, string? source) =>
        new Dictionary<string, object?>
        {
            ["name"] = name,
            ["version"] = version,
            ["id"] = Id(name),
            ["source"] = source,
            ["manifest_path"] = $"{directory}/Cargo.toml",
            ["edition"] = "2021",
            ["targets"] = targets
        };

    private static object Lib(string name, string source) => Target(name, "lib", source);

    private static object Target(string name, string kind, string source) => new Dictionary<string, object>
    {
        ["name"] = name,
        ["kind"] = new[] { kind },
        ["src_path"] = source,
        ["edition"] = "2021"
    };

    private static object Node(string name, string[]? features = null, params object[] deps) => new
    {
        id = Id(name),
        deps,
        features = features ?? []
    };

    private static object Dep(string name, string package, string? kind) => new
    {
        name,
        pkg = Id(package),
        dep_kinds = new[] { new Dictionary<string, object?> { ["kind"] = kind } }
    };
}
