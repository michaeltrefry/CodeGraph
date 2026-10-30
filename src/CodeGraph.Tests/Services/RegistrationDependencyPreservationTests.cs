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

public sealed class RegistrationDependencyPreservationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AmbiguousEndpoint_OversizedOtherRegistration_PreservesAmbiguityAndHashes(bool incremental, bool jsonBacked)
    {
        var root = TemporaryRoot();
        try
        {
            const string registration = "use axum::{Router, routing::get}; fn handle() {} fn routes() { let r = Router::new().route(\"/same\", get(handle)); }";
            Write(root, "first.rs", registration);
            Write(root, "second.rs", registration);
            var store = new InMemoryGraphStore();
            var pipeline = Pipeline(store);
            await pipeline.IndexRepositoryAsync("App", root);
            var route = store.Nodes.Single(node => node.Label == NodeLabel.Route);
            route.Properties["registration_ambiguous"].ShouldBe(true);
            store.Edges.ShouldNotContain(edge => edge.SourceId == route.Id && edge.Type == EdgeType.HANDLES);
            var paths = new[] { "first.rs", "second.rs" };
            // Runtime output uses string[]; persisted JSON properties materialize as JsonElement.
            route.Properties["registration_sources"] = jsonBacked ? JsonSerializer.SerializeToElement(paths) : paths;
            var before = Snapshot(store);
            var hashes = store.FileHashesOf("App").ToDictionary();
            var writes = store.ReplaceRepositoryFilesCallCount;
            await pipeline.IndexRepositoryAsync("App", root);
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
            Snapshot(store).ShouldBe(before);

            Write(root, "second.rs", registration + new string(' ', 9000));
            (await Should.ThrowAsync<InvalidOperationException>(() => pipeline.IndexRepositoryAsync("App", root, replaceExistingGraph: !incremental)))
                .Message.ShouldContain("retaining prior graph and hashes");
            Snapshot(store).ShouldBe(before);
            store.FileHashesOf("App").ShouldBe(hashes);
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
            store.Nodes.Single(node => node.Label == NodeLabel.Route).Properties["registration_ambiguous"].ShouldBe(true);
            store.Edges.ShouldNotContain(edge => edge.SourceId == route.Id && edge.Type == EdgeType.HANDLES);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TauriEvent_OversizedConstantDependency_PreservesProducerSubscriberAndHashes(bool incremental)
    {
        var root = TemporaryRoot();
        try
        {
            Write(root, "package.json", "{}");
            Write(root, "listener.ts", "export function Screen() {} export function onProgress() {}");
            Write(root, "src-tauri/Cargo.toml", "[package]\nname=\"app\"\nversion=\"0.1.0\"");
            Write(root, "src-tauri/tauri.conf.json", """{"build":{"frontendDist":"../dist"}}""");
            Write(root, "src-tauri/src/lib.rs", "use tauri::{AppHandle, Emitter}; pub fn publish(app: AppHandle) { app.emit(crate::events::EVENT, ()); }");
            const string constants = "pub const EVENT: &str = \"progress\";";
            Write(root, "src-tauri/src/events.rs", constants);
            // An unrelated oversized Rust file outside the app remains eligible for ordinary skip.
            Write(root, "unrelated.rs", new string(' ', 9000));
            var store = new InMemoryGraphStore();
            var pipeline = Pipeline(store, new EventReferenceAnalyzer());
            await pipeline.IndexRepositoryAsync("App", root);
            var eventNode = store.Nodes.Single(node => node.Label == NodeLabel.Event && node.Name == "progress");
            var producer = store.Nodes.Single(node => node.Name == "publish" && node.Label == NodeLabel.Function);
            var subscriber = store.Nodes.Single(node => node.QualifiedName == "App:Screen");
            var callback = store.Nodes.Single(node => node.QualifiedName == "App:onProgress");
            store.Edges.ShouldContain(edge => edge.SourceId == producer.Id && edge.TargetId == eventNode.Id && edge.Type == EdgeType.PUBLISHES);
            store.Edges.ShouldContain(edge => edge.SourceId == subscriber.Id && edge.TargetId == eventNode.Id && edge.Type == EdgeType.SUBSCRIBES);
            store.Edges.ShouldContain(edge => edge.SourceId == eventNode.Id && edge.TargetId == callback.Id && edge.Type == EdgeType.HANDLES);
            // The dependency is neither a graph endpoint nor an extraction_source_file owner.
            var oldEdges = await store.FindEdgesByPropertyAsync("App", [EdgeType.PUBLISHES, EdgeType.SUBSCRIBES, EdgeType.HANDLES], "extraction_owner", ["tauri"]);
            oldEdges.ShouldNotContain(edge => edge.SourceFilePath == "src-tauri/src/events.rs" || edge.TargetFilePath == "src-tauri/src/events.rs"
                || Convert.ToString(edge.Properties.GetValueOrDefault("extraction_source_file")) == "src-tauri/src/events.rs");
            var before = Snapshot(store);
            var hashes = store.FileHashesOf("App").ToDictionary();
            var writes = store.ReplaceRepositoryFilesCallCount;
            await pipeline.IndexRepositoryAsync("App", root);
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
            Snapshot(store).ShouldBe(before);
            Write(root, "src-tauri/src/events.rs", constants + new string(' ', 9000));
            (await Should.ThrowAsync<InvalidOperationException>(() => pipeline.IndexRepositoryAsync("App", root, replaceExistingGraph: !incremental)))
                .Message.ShouldContain("prior graph and hashes are preserved");
            Snapshot(store).ShouldBe(before);
            store.FileHashesOf("App").ShouldBe(hashes);
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
        }
        finally { Directory.Delete(root, true); }
    }

    private static IndexingPipeline Pipeline(InMemoryGraphStore store, ITypeScriptAnalyzer? analyzer = null) => new(store,
        [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)], Options.Create(new IndexingOptions { MaxFileSizeKb = 1 }),
        new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: analyzer);
    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codegraph-registration-dependency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static void Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, content);
    }
    private static string Snapshot(InMemoryGraphStore store)
    {
        var byId = store.Nodes.ToDictionary(node => node.Id);
        return JsonSerializer.Serialize(new
        {
            Nodes = store.Nodes.OrderBy(node => node.QualifiedName).Select(node => new { node.QualifiedName, node.FilePath, Properties = node.Properties.OrderBy(pair => pair.Key) }),
            Edges = store.Edges.Select(edge => new { Source = byId[edge.SourceId].QualifiedName, Target = byId[edge.TargetId].QualifiedName, edge.Type, Properties = edge.Properties.OrderBy(pair => pair.Key) }).OrderBy(edge => edge.Source).ThenBy(edge => edge.Target).ThenBy(edge => edge.Type)
        });
    }
    private sealed class EventReferenceAnalyzer : ITypeScriptAnalyzer
    {
        public Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(string path, ExtractorContext context, IReadOnlyList<string> includedFiles, CancellationToken ct = default)
        {
            GraphNode Function(string name) => new() { Repository = "App", Label = NodeLabel.Function, Name = name, QualifiedName = "App:" + name, FilePath = "listener.ts" };
            var reference = Function("eventReference") with { Properties = new()
            {
                ["kind"] = "tauri_event_reference", ["tauri_name"] = "progress", ["tauri_api"] = "listen",
                ["tauri_caller"] = "App:Screen", ["tauri_callback"] = "App:onProgress", ["extraction_owner"] = "typescript"
            } };
            return Task.FromResult<IReadOnlyList<ExtractionResult>>([new() { Nodes = [Function("Screen"), Function("onProgress"), reference], ProcessedFiles = ["listener.ts"] }]);
        }
    }
}
