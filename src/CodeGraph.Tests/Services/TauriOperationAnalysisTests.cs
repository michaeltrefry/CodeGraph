using System.Reflection;
using System.Text.RegularExpressions;
using CodeGraph.Models;
using CodeGraph.Data;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class TauriOperationAnalysisTests
{
    [Fact]
    public async Task CommandBinding_UsesExistingNativeFallbackExtractorFunctionIdentity()
    {
        var sources = Fixture();
        var extractor = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);
        var context = new ExtractorContext { RepositoryName = "App", RootPath = "/test" };
        var extracted = await extractor.ExtractAsync("/test/apps/desktop/src/lib.rs", sources["apps/desktop/src/lib.rs"], context);
        extracted.Succeeded.ShouldBeTrue();
        var actualFunction = extracted.Nodes.Single(node => node.Label == NodeLabel.Function && node.Name == "get_app_overview");
        var analysis = TauriOperationAnalysis.Analyze("App", sources, extracted.Nodes, [Reference("get_app_overview")]);
        analysis.Result.Complete.ShouldBeTrue(string.Join(';', analysis.Diagnostics));
        analysis.Result.Edges.ShouldContain(edge => edge.Type == EdgeType.CALLS && edge.TargetQN == actualFunction.QualifiedName);
        analysis.Result.Nodes.ShouldNotContain(node => node.QualifiedName == actualFunction.QualifiedName);
    }

    [Fact]
    public void SoundWorksWrapperReference_BindsImportedRegisteredDeclaration_NotSameNamedOtherApp()
    {
        var sources = Fixture();
        sources["apps/desktop/src/lib.rs"] = "use crate::commands::{get_app_overview as overview};\nfn run() { tauri::Builder::default().invoke_handler(tauri::generate_handler![overview]); }";
        sources["apps/desktop/src/commands.rs"] = "#[tauri::command]\npub fn get_app_overview() {}";
        sources["other/Cargo.toml"] = "[package]\nname=\"other\"";
        sources["other/src/lib.rs"] = "#[tauri::command]\npub fn get_app_overview() {}";
        var functions = Functions(sources);
        var reference = Reference("get_app_overview");
        var analysis = TauriOperationAnalysis.Analyze("App", sources, functions, [reference]);
        analysis.Result.Complete.ShouldBeTrue(string.Join(';', analysis.Diagnostics));
        var command = analysis.Result.Nodes.Single(node => node.Properties["kind"].ToString() == "tauri_command");
        command.FilePath.ShouldBe("apps/desktop/src/lib.rs");
        command.Properties["native_function"].ShouldBe("rust:apps/desktop/src/commands.rs:get_app_overview");
        analysis.Result.Edges.ShouldContain(edge => edge.SourceQN == reference.QualifiedName && edge.TargetQN == command.QualifiedName && edge.Type == EdgeType.CALLS);
        analysis.Result.Edges.ShouldNotContain(edge => edge.TargetQN.Contains("other/src"));
        analysis.Result.Nodes.ShouldNotContain(node => node.Label == NodeLabel.Route);
    }

    [Fact]
    public void LensWorksSameNames_StayWithinApp_AndRenamedPluginCommandsRetainNamespace()
    {
        var sources = Fixture();
        sources["apps/desktop/src/lib.rs"] = "mod commands { #[tauri::command(rename=\"catalog_command\")] pub fn internal() {} }\nfn run() { tauri::Builder::default().invoke_handler(tauri::generate_handler![commands::internal]); }";
        sources["lens/package.json"] = "{}";
        sources["lens/src-tauri/Cargo.toml"] = "[package]\nname=\"lens\"";
        sources["lens/src-tauri/tauri.conf.json"] = """{"build":{"frontendDist":"../dist"}}""";
        sources["lens/src-tauri/src/lib.rs"] = "use tauri::{command, generate_handler};\n#[command(rename=\"catalog_command\")] fn internal() {}\nfn plugin() { tauri::plugin::Builder::new(\"catalog\").invoke_handler(generate_handler![#![plugin(catalog)] internal]); }";
        var web = Reference("catalog_command");
        var lens = Reference("plugin:catalog|catalog_command", "lens/src/bridge.ts");
        var analysis = TauriOperationAnalysis.Analyze("App", sources, Functions(sources), [web, lens]);
        analysis.Result.Complete.ShouldBeTrue(string.Join(';', analysis.Diagnostics));
        analysis.Result.Nodes.Count(node => node.Properties["kind"].ToString() == "tauri_command").ShouldBe(2);
        var webTarget = analysis.Result.Edges.Single(edge => edge.SourceQN == web.QualifiedName).TargetQN;
        var lensTarget = analysis.Result.Edges.Single(edge => edge.SourceQN == lens.QualifiedName).TargetQN;
        webTarget.ShouldNotBe(lensTarget);
        analysis.Result.Nodes.Single(node => node.QualifiedName == lensTarget).Properties["app_root"].ShouldBe("lens/src-tauri");
    }

    [Fact]
    public void ChatWorksEvents_LinkRealProducerAndCallback_AndRetainDynamicUncertainty()
    {
        var sources = Fixture();
        sources["apps/desktop/src/lib.rs"] = """
            use tauri::{AppHandle, Emitter};
            const PROGRESS_EVENT: &str = "engine://stream";
            #[tauri::command]
            fn stream_completion(app: AppHandle, dynamic: String) {
              let fixture = r#"app.emit("fake_event", x)"#;
              // app.emit("comment_event", x);
              app.emit(PROGRESS_EVENT, 1);
              app.emit(dynamic, 2);
              other.emit("unrelated", 3);
            }
            fn run() { tauri::Builder::default().invoke_handler(tauri::generate_handler![stream_completion]); }
            """;
        var reference = Reference("engine://stream") with { Properties = new()
        {
            ["kind"] = "tauri_event_reference", ["tauri_name"] = "engine://stream", ["tauri_api"] = "listen",
            ["tauri_caller"] = "Screen", ["tauri_callback"] = "onStream"
        } };
        var analysis = TauriOperationAnalysis.Analyze("App", sources, Functions(sources), [reference]);
        analysis.Result.Complete.ShouldBeTrue(string.Join(';', analysis.Diagnostics));
        var eventNode = analysis.Result.Nodes.Single(node => node.Label == NodeLabel.Event);
        eventNode.Name.ShouldBe("engine://stream");
        analysis.Result.Edges.ShouldContain(edge => edge.SourceQN == "rust:apps/desktop/src/lib.rs:stream_completion" && edge.TargetQN == eventNode.QualifiedName && edge.Type == EdgeType.PUBLISHES);
        analysis.Result.Edges.ShouldContain(edge => edge.SourceQN == "Screen" && edge.TargetQN == eventNode.QualifiedName && edge.Type == EdgeType.SUBSCRIBES);
        analysis.Result.Edges.ShouldContain(edge => edge.SourceQN == eventNode.QualifiedName && edge.TargetQN == "onStream" && edge.Type == EdgeType.HANDLES);
        analysis.Diagnostics.ShouldContain(message => message.Contains("dynamic Rust event"));
    }

    [Fact]
    public void MissingOrMalformedRust_IsIncomplete_ButCompleteRegistrationRemovalDeletesBindings()
    {
        var sources = Fixture();
        var first = TauriOperationAnalysis.Analyze("App", sources, Functions(sources), [Reference("get_app_overview")]);
        first.Result.Complete.ShouldBeTrue();
        TauriOperationAnalysis.Analyze("App", sources, [], [Reference("get_app_overview")]).Result.Complete.ShouldBeFalse();
        var removed = new Dictionary<string, string>(sources) { ["apps/desktop/src/lib.rs"] = "#[tauri::command]\nfn get_app_overview() {}\nfn run() { tauri::Builder::default(); }" };
        var removedAnalysis = TauriOperationAnalysis.Analyze("App", removed, Functions(removed), [Reference("get_app_overview")]);
        removedAnalysis.Result.Complete.ShouldBeTrue();
        removedAnalysis.Result.Nodes.ShouldBeEmpty();
        removedAnalysis.Diagnostics.ShouldContain(message => message.Contains("not uniquely registered"));
        removed["apps/desktop/src/lib.rs"] = "fn unfinished() {";
        TauriOperationAnalysis.Analyze("App", removed, [], []).Result.Complete.ShouldBeFalse();
    }

    [Fact]
    public async Task NativeReconciliation_NoChurn_RegistrationRemovalPreservesRust_AndFailurePreservesPersistedGraph()
    {
        var sources = Fixture();
        var root = Path.Combine(Path.GetTempPath(), "codegraph-tauri-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Write(root, sources);
            var store = new InMemoryGraphStore();
            var pipeline = Pipeline(store);
            var buffer = new GraphBuffer();
            foreach (var node in Functions(sources).Concat([Reference("get_app_overview")])) buffer.AddNode(node);
            var first = await Prepare(pipeline, root, sources.Keys.ToList(), buffer, false);
            first.ShouldContain("apps/desktop/src/lib.rs");
            await store.ReplaceRepositoryGraphAsync("App", buffer.AllNodes.ToList(), buffer.AllPendingEdges.ToList(), new Dictionary<string, string>(), new RepositoryEntity { Name = "App" }, null);
            var persistedBefore = store.Nodes.Select(node => node.QualifiedName).Order().ToList();
            var same = new GraphBuffer(); same.AddNode(Reference("get_app_overview"));
            (await Prepare(pipeline, root, sources.Keys.ToList(), same, true)).ShouldBeEmpty();
            // Parse failure must throw before any graph write and leave valid prior bindings.
            File.WriteAllText(Path.Combine(root, "apps/desktop/src/lib.rs"), "fn unfinished() {");
            await Should.ThrowAsync<InvalidOperationException>(() => Prepare(pipeline, root, sources.Keys.ToList(), new GraphBuffer(), true));
            store.Nodes.Select(node => node.QualifiedName).Order().ShouldBe(persistedBefore);

            sources["apps/desktop/src/lib.rs"] = "#[tauri::command]\nfn get_app_overview() {}\nfn run() { tauri::Builder::default(); }";
            Write(root, sources);
            var next = new GraphBuffer(); next.AddNode(Reference("get_app_overview"));
            var changed = await Prepare(pipeline, root, sources.Keys.ToList(), next, true);
            changed.ShouldContain("apps/web/src/tauri.ts");
            changed.ShouldContain("apps/desktop/src/lib.rs");
            next.AllNodes.ShouldContain(node => node.QualifiedName == "rust:apps/desktop/src/lib.rs:get_app_overview");
            await store.ReplaceRepositoryFilesAsync("App", changed,
                next.AllNodes.Where(node => changed.Contains(node.FilePath)).ToList(), next.AllPendingEdges.ToList(), new Dictionary<string, string>());
            store.Nodes.Any(node => node.Properties.GetValueOrDefault("kind")?.ToString() == "tauri_command").ShouldBeFalse();
            store.Edges.Any(edge => edge.Properties.GetValueOrDefault("extraction_owner")?.ToString() == "tauri").ShouldBeFalse();
            store.Nodes.ShouldContain(node => node.QualifiedName == "rust:apps/desktop/src/lib.rs:get_app_overview");
            var fresh = TauriOperationAnalysis.Analyze("App", sources, Functions(sources), [Reference("get_app_overview")]);
            fresh.Result.Nodes.ShouldBeEmpty();
            fresh.Result.Edges.ShouldBeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    private static IndexingPipeline Pipeline(InMemoryGraphStore store) => new(store, [], Options.Create(new IndexingOptions()),
        new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance);
    private static async Task<IReadOnlyList<string>> Prepare(IndexingPipeline pipeline, string root, IReadOnlyList<string> scope, GraphBuffer buffer, bool incremental)
    {
        var method = typeof(IndexingPipeline).GetMethod("PrepareTauriBindingsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<IReadOnlyList<string>>)method.Invoke(pipeline,
            ["App", root, scope, buffer, new HashSet<string>(), new HashSet<string>(), incremental, CancellationToken.None])!;
    }
    private static void Write(string root, IReadOnlyDictionary<string, string> sources)
    {
        foreach (var (path, content) in sources) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!); File.WriteAllText(Path.Combine(root, path), content); }
    }
    private static Dictionary<string, string> Fixture() => new()
    {
        ["apps/web/package.json"] = "{}", ["apps/web/src/tauri.ts"] = "export function loadAppOverview() {}",
        ["apps/desktop/Cargo.toml"] = "[package]\nname=\"soundworks_desktop\"",
        ["apps/desktop/tauri.conf.json"] = """{"build":{"frontendDist":"../web/dist"}}""",
        ["apps/desktop/src/lib.rs"] = "#[tauri::command]\nfn get_app_overview() {}\nfn run() { tauri::Builder::default().invoke_handler(tauri::generate_handler![get_app_overview]); }"
    };
    private static GraphNode Reference(string name, string file = "apps/web/src/tauri.ts") => new()
    {
        Repository = "App", Label = NodeLabel.Function, Name = name, QualifiedName = "reference:" + file + ":" + name, FilePath = file,
        Properties = new() { ["kind"] = "tauri_command_reference", ["tauri_name"] = name }
    };
    private static List<GraphNode> Functions(IReadOnlyDictionary<string, string> sources) => sources.Where(pair => pair.Key.EndsWith(".rs"))
        .SelectMany(pair => Regex.Matches(pair.Value, @"\bfn\s+(\w+)\s*\(").Select(match => new GraphNode
        {
            Repository = "App", Label = NodeLabel.Function, Name = match.Groups[1].Value,
            QualifiedName = $"rust:{pair.Key}:{match.Groups[1].Value}", FilePath = pair.Key,
            StartLine = 1 + pair.Value[..match.Index].Count(character => character == '\n'), EndLine = 1 + pair.Value.Count(character => character == '\n')
        })).ToList();
}
