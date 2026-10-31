using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class TypeScriptSemanticRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-ui-semantic-{Guid.NewGuid():N}");
    private readonly InMemoryGraphStore _store = new();
    private readonly FixtureAnalyzer _analyzer = new();
    private readonly CountingFallback _fallback = new();
    public TypeScriptSemanticRefreshTests()
    {
        Directory.CreateDirectory(_root);
        Write("screen.jsx", "export function Screen() { return null; }");
        Write("helpers.js", "export function load() {}");
    }
    public void Dispose() => Directory.Delete(_root, true);
    private void Write(string path, string contents) => File.WriteAllText(Path.Combine(_root, path), contents);
    private Task Index(IReadOnlyList<string>? changed = null) => new IndexingPipeline(_store, [_fallback],
        Options.Create(new IndexingOptions { MaxParallelFiles = 1 }), new LocalFileSystem(),
        NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: _analyzer)
        .IndexRepositoryAsync("UI", _root, changedFilesOnly: changed);
    private string[] Snapshot(InMemoryGraphStore? store = null)
    {
        store ??= _store;
        return store.Nodes.Where(node => node.Properties.ContainsKey("extraction_owner"))
            .Select(node => Canonical(JsonSerializer.SerializeToNode(new {
                kind = "node", node.Label, node.Name, node.QualifiedName, node.FilePath,
                node.StartLine, node.EndLine, node.Properties })))
            .Concat(store.Edges.Where(edge => edge.Properties.ContainsKey("extraction_owner"))
                .Select(edge => Canonical(JsonSerializer.SerializeToNode(new {
                    kind = "edge", source = store.Nodes.Single(node => node.Id == edge.SourceId).QualifiedName,
                    target = store.Nodes.Single(node => node.Id == edge.TargetId).QualifiedName,
                    edge.Type, edge.Properties }))))
            .Order(StringComparer.Ordinal).ToArray();
    }
    private static string Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(',', obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonical(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(',', array.Select(Canonical)) + "]",
        _ => node?.ToJsonString() ?? "null"
    };

    [Fact]
    public async Task AnalyzerSourceDiscovery_DoesNotDependOnPerFileExtensionRegistration()
    {
        await new IndexingPipeline(_store, [], Options.Create(new IndexingOptions()), new LocalFileSystem(),
            NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: _analyzer).IndexRepositoryAsync("UI", _root);
        (await _store.GetFileHashRecordsAsync("UI")).Keys.Order().ShouldBe(["helpers.js", "screen.jsx"]);
        _store.Nodes.ShouldContain(node => node.Label == NodeLabel.File && node.FilePath == "screen.jsx");
    }

    [Fact]
    public async Task DefaultConfiguration_JsAndJsxAreProcessedOnceAndVersioned()
    {
        await Index();
        _analyzer.Config.ShouldBe(Path.Combine(_root, "tsconfig.json"));
        _analyzer.Scope.ShouldBe(["helpers.js", "screen.jsx"]);
        _fallback.Calls.ShouldBe(0);
        var hashes = await _store.GetFileHashRecordsAsync("UI");
        hashes.Keys.Order().ShouldBe(["helpers.js", "screen.jsx"]);
        hashes.Values.ShouldAllBe(record => record.ExtractionVersion!.Contains("FixtureAnalyzer=4"));
    }

    [Fact]
    public async Task DerivedRetargetRemovalAndPropertyOnlyChanges_MatchFreshPersistedGraphWithoutChurn()
    {
        await Index();
        _analyzer.Target = "alternate";
        _analyzer.Mode = "nested-provider";
        _analyzer.Line = 8;
        // The only changed bytes are in HTML. The owning JSX and JS files remain unchanged.
        Write("view.html", "<child></child>");
        await Index(["view.html"]);
        _store.ReplacedFileSlices.Last().ShouldContain("screen.jsx");
        _store.ReplacedFileSlices.Last().ShouldContain("helpers.js");
        var expected = Snapshot();
        var hashes = await _store.GetFileHashRecordsAsync("UI");
        hashes.Keys.ShouldContain("screen.jsx");
        hashes.Keys.ShouldContain("helpers.js");
        var writes = _store.ReplaceRepositoryFilesCallCount;
        _analyzer.UseJsonProperties = true; // round-trip representation/property ordering is immaterial
        await Index();
        _store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
        Snapshot().ShouldBe(expected);
        var fresh = new InMemoryGraphStore();
        await new IndexingPipeline(fresh, [_fallback], Options.Create(new IndexingOptions()), new LocalFileSystem(),
            NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: _analyzer).IndexRepositoryAsync("UI", _root);
        Snapshot().ShouldBe(Snapshot(fresh));
        _analyzer.Target = null;
        await Index();
        _store.Edges.ShouldNotContain(e => e.Type == EdgeType.CALLS);
        writes = _store.ReplaceRepositoryFilesCallCount;
        await Index();
        _store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrPartialAnalysis_PreservesPersistedGraphAndHashes(bool throws)
    {
        await Index();
        var facts = Snapshot();
        var hashes = await _store.GetFileHashRecordsAsync("UI");
        Write("helpers.js", "export function changed() {}");
        _analyzer.Fail = true;
        _analyzer.Throw = throws;
        await Should.ThrowAsync<InvalidOperationException>(() => Index());
        Snapshot().ShouldBe(facts);
        (await _store.GetFileHashRecordsAsync("UI")).ShouldBe(hashes);
        _fallback.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task TargetSliceReplacement_PreservesIncomingEdgesFromUnchangedSources()
    {
        await Index();
        var screen = _store.Nodes.Single(n => n.QualifiedName == "Screen");
        var external = await _store.UpsertNodeAsync(new GraphNode { Repository = "UI", Label = NodeLabel.Function,
            Name = "outer", QualifiedName = "outer", FilePath = "outer.cs" });
        await _store.InsertEdgeAsync(new GraphEdge { Repository = "UI", SourceId = external, TargetId = screen.Id, Type = EdgeType.CALLS });
        _analyzer.Mode = "changed";
        await Index();
        var updated = _store.Nodes.Single(n => n.QualifiedName == "Screen");
        _store.Edges.ShouldContain(e => e.SourceId == external && e.TargetId == updated.Id && e.Type == EdgeType.CALLS);
    }

    [Fact]
    public async Task SuccessfulPartialResult_DoesNotDeleteUnprocessedSlicesOrAdvanceHashes()
    {
        await Index();
        var facts = Snapshot();
        var hashes = await _store.GetFileHashRecordsAsync("UI");
        _analyzer.OmitSource = true;
        Write("helpers.js", "export function updated() {}");
        await Should.ThrowAsync<InvalidOperationException>(() => Index());
        Snapshot().ShouldBe(facts);
        (await _store.GetFileHashRecordsAsync("UI")).ShouldBe(hashes);
        _fallback.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task TopLevelFileRender_RetargetsWhenImportedExportChangesAndThenStaysStable()
    {
        _analyzer.FileRender = true;
        await Index();
        _analyzer.Target = "renamedDefault";
        Write("helpers.js", "export default function renamedDefault() {}");
        await Index(["helpers.js"]);
        var file = _store.Nodes.Single(node => node.QualifiedName == "UI:screen.jsx");
        var render = _store.Edges.Single(edge => edge.SourceId == file.Id && edge.Type == EdgeType.RENDERS);
        _store.Nodes.Single(node => node.Id == render.TargetId).QualifiedName.ShouldBe("renamedDefault");
        _store.ReplacedFileSlices.Last().ShouldContain("screen.jsx");
        var writes = _store.ReplaceRepositoryFilesCallCount;
        await Index();
        _store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
    }

    [Fact]
    public async Task RootPackageSemanticMetadata_RefreshesAndStableRerunDoesNotReplaceIt()
    {
        _analyzer.RootModule = true;
        await Index();
        _analyzer.Mode = "updated-root";
        await Index();
        _store.Nodes.Single(node => node.QualifiedName == "typescript-package:UI:root").Properties["mode"].ShouldBe("updated-root");
        var writes = _store.ReplaceRepositoryFilesCallCount;
        await Index();
        _store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
    }

    [Fact]
    public async Task JsconfigIsUsedAndConfigurationOnlyChangesRecomputeCurrentSourceFacts()
    {
        Write("jsconfig.json", "{}");
        await Index();
        _analyzer.Config.ShouldBe(Path.Combine(_root, "jsconfig.json"));
        _analyzer.Mode = "configured-alias";
        Write("jsconfig.json", "{\"compilerOptions\":{\"baseUrl\":\".\"}}");
        await Index(["jsconfig.json"]);
        _store.Nodes.Single(node => node.QualifiedName == "Screen").Properties["mode"].ShouldBe("configured-alias");
        _store.ReplacedFileSlices.Last().ShouldContain("screen.jsx");
        _fallback.Calls.ShouldBe(0);
    }

    private sealed class FixtureAnalyzer : ITypeScriptAnalyzer
    {
        public string ExtractionVersion => "4";
        public string? Target = "load";
        public string Mode = "default";
        public int Line = 1;
        public bool UseJsonProperties, Fail, Throw, OmitSource, FileRender, RootModule;
        public string? Config;
        public IReadOnlyList<string> Scope = [];
        public Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(string tsconfigPath, ExtractorContext context,
            IReadOnlyList<string> includedFiles, CancellationToken ct = default)
        {
            Config = tsconfigPath; Scope = includedFiles;
            if (Throw) throw new IOException("sidecar unavailable");
            if (Fail) return Task.FromResult<IReadOnlyList<ExtractionResult>>([ExtractionResult.Failure("partial syntax analysis")]);
            Dictionary<string, object> Props() => UseJsonProperties
                ? new() { ["flags"] = JsonSerializer.Deserialize<JsonElement>("[\"a\",\"b\"]"), ["mode"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(Mode)) }
                : new() { ["mode"] = Mode, ["flags"] = new[] { "a", "b" } };
            GraphNode Node(string name, string path) => new() { Repository = context.RepositoryName, Label = NodeLabel.Function,
                Name = name, QualifiedName = name, FilePath = path, StartLine = Line, Properties = Props() };
            var nodes = new List<GraphNode> { Node("Screen", "screen.jsx"), Node(Target ?? "alternate", "helpers.js") };
            var edges = Target is null ? new List<PendingEdge>() : [new PendingEdge(FileRender ? "UI:screen.jsx" : "Screen", Target,
                FileRender ? EdgeType.RENDERS : EdgeType.CALLS, new() { ["confidence"] = 1.0 })];
            if (RootModule)
            {
                nodes.Add(new GraphNode { Repository = context.RepositoryName, Label = NodeLabel.Module,
                    Name = "root", QualifiedName = "typescript-package:UI:root", Properties = new() {
                        ["module_kind"] = "typescript_workspace_package", ["mode"] = Mode } });
                edges.Add(new PendingEdge("UI", "typescript-package:UI:root", EdgeType.CONTAINS_PROJECT));
            }
            return Task.FromResult<IReadOnlyList<ExtractionResult>>([new() {
                Complete = !OmitSource,
                Nodes = nodes,
                Edges = edges,
                ProcessedFiles = OmitSource ? ["screen.jsx"] : ["screen.jsx", "helpers.js"]
            }]);
        }
    }
    private sealed class CountingFallback : ICodeExtractor
    {
        public int Calls;
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".ts", ".tsx", ".js", ".jsx" };
        public Task<ExtractionResult> ExtractAsync(string filePath, string content, ExtractorContext context, CancellationToken ct = default)
        { Calls++; return Task.FromResult(new ExtractionResult()); }
    }
}
