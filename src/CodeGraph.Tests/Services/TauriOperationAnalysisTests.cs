using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeGraph.Models;
using CodeGraph.Data;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Pipeline;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class TauriOperationAnalysisTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScipPointSpan_CommandAndEventReuseTheNativeDeclaration(bool multilineName)
    {
        var sources = Fixture();
        sources["apps/desktop/src/lib.rs"] = "use tauri::{AppHandle, Emitter};\n#[tauri::command]\n"
            + (multilineName ? "fn\npublish" : "fn publish")
            + "(app: AppHandle) {\n  app.emit(\"progress\", ());\n}\nfn run() { tauri::Builder::default().invoke_handler(tauri::generate_handler![publish]); }";
        var nameLine = multilineName ? 4 : 3;
        var function = new GraphNode { Repository = "App", Label = NodeLabel.Function, Name = "publish", QualifiedName = "scip:crate/publish().",
            FilePath = "apps/desktop/src/lib.rs", StartLine = nameLine, EndLine = nameLine, Properties = new() { ["source"] = "scip" } };
        var analysis = TauriOperationAnalysis.Analyze("App", sources, [function], [Reference("publish")]);
        analysis.Result.Complete.ShouldBeTrue(string.Join(';', analysis.Diagnostics));
        var command = analysis.Result.Nodes.Single(node => node.Name == "publish");
        var eventNode = analysis.Result.Nodes.Single(node => node.Label == NodeLabel.Event && node.Name == "progress");
        analysis.Result.Edges.ShouldContain(edge => edge.SourceQN == command.QualifiedName && edge.TargetQN == function.QualifiedName && edge.Type == EdgeType.CALLS);
        analysis.Result.Edges.ShouldContain(edge => edge.SourceQN == function.QualifiedName && edge.TargetQN == eventNode.QualifiedName && edge.Type == EdgeType.PUBLISHES);
        analysis.Result.Nodes.ShouldNotContain(node => node.QualifiedName == function.QualifiedName);
    }

    [Fact]
    public async Task MultilineFunctionName_AlsoBindsTheFallbackDeclaration()
    {
        var sources = Fixture(); sources["apps/desktop/src/lib.rs"] = SemanticEventSource("progress");
        var extracted = await new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance).ExtractAsync(
            "/test/apps/desktop/src/lib.rs", sources["apps/desktop/src/lib.rs"], new ExtractorContext { RepositoryName = "App", RootPath = "/test" });
        var function = extracted.Nodes.Single(node => node.Label == NodeLabel.Function && node.Name == "publish");
        var analysis = TauriOperationAnalysis.Analyze("App", sources, extracted.Nodes, [Reference("publish")]);
        analysis.Result.Complete.ShouldBeTrue(string.Join(';', analysis.Diagnostics));
        analysis.Result.Edges.ShouldContain(edge => edge.SourceQN == function.QualifiedName && edge.Type == EdgeType.PUBLISHES);
        analysis.Result.Edges.ShouldContain(edge => edge.TargetQN == function.QualifiedName && edge.Type == EdgeType.CALLS);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EventProducerDeclaration_AmbiguityIsPreservedAndSameLineOtherNamesDoNotMatch(bool ambiguous)
    {
        var sources = Fixture();
        sources["apps/desktop/src/lib.rs"] = "use tauri::{AppHandle, Emitter}; fn publish(app: AppHandle) { app.emit(\"progress\", ()); } fn other() {}";
        var publish = SemanticPoint() with { StartLine = 1, EndLine = 1 };
        var other = publish with { Name = ambiguous ? "publish" : "other", QualifiedName = "scip:other()." };
        var analysis = TauriOperationAnalysis.Analyze("App", sources, [publish, other], []);
        analysis.Result.Complete.ShouldBe(!ambiguous, string.Join(';', analysis.Diagnostics));
        if (ambiguous)
        {
            analysis.Diagnostics.ShouldContain(message => message.Contains("event producer unavailable"));
            analysis.Result.Edges.ShouldNotContain(edge => edge.Type == EdgeType.PUBLISHES);
        }
        else analysis.Result.Edges.Single(edge => edge.Type == EdgeType.PUBLISHES).SourceQN.ShouldBe(publish.QualifiedName);
    }

    [Fact]
    public async Task SemanticPointSpanPipeline_PersistsEventRenamesAndRemovalWithoutChurn()
    {
        var root = Path.Combine(Path.GetTempPath(), "codegraph-tauri-semantic-event-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sources = Fixture(); var store = new InMemoryGraphStore();
            var pipeline = new IndexingPipeline(store, [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)], Options.Create(new IndexingOptions()), new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance, rustAnalyzer: new PointSpanRustAnalyzer());
            foreach (var name in new string?[] { "before", "after", null })
            {
                sources["apps/desktop/src/lib.rs"] = SemanticEventSource(name); Write(root, sources);
                await pipeline.IndexRepositoryAsync("App", root);
                var function = store.Nodes.Single(node => node.QualifiedName == SemanticPoint().QualifiedName);
                function.StartLine.ShouldBe(4); function.EndLine.ShouldBe(4);
                var command = store.Nodes.Single(node => node.Properties.GetValueOrDefault("kind")?.ToString() == "tauri_command");
                store.Edges.ShouldContain(edge => edge.SourceId == command.Id && edge.TargetId == function.Id && edge.Type == EdgeType.CALLS);
                var events = store.Nodes.Where(node => node.Properties.GetValueOrDefault("kind")?.ToString() == "tauri_event").ToList();
                events.Select(node => node.Name).ShouldBe(name is null ? [] : new[] { name });
                if (name is not null) store.Edges.ShouldContain(edge => edge.SourceId == function.Id && edge.TargetId == events[0].Id && edge.Type == EdgeType.PUBLISHES);
                var writes = store.ReplaceRepositoryFilesCallCount;
                await pipeline.IndexRepositoryAsync("App", root);
                store.ReplaceRepositoryFilesCallCount.ShouldBe(writes, JsonSerializer.Serialize(store.ReplacedFileSlices));
                store.FileHashesOf("App").ShouldContainKey("apps/desktop/src/lib.rs");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static string SemanticEventSource(string? name) => "use tauri::{AppHandle, Emitter};\n#[tauri::command]\nfn\npublish(app: AppHandle) {\n"
        + (name is null ? "" : $"  app.emit(\"{name}\", ());\n")
        + "}\nfn run() { tauri::Builder::default().invoke_handler(tauri::generate_handler![publish]); }";
    private static GraphNode SemanticPoint() => new() { Repository = "App", Label = NodeLabel.Function, Name = "publish", QualifiedName = "scip:crate/publish().",
        FilePath = "apps/desktop/src/lib.rs", StartLine = 4, EndLine = 4, Properties = new() { ["source"] = "scip" } };
    private sealed class PointSpanRustAnalyzer : IRustAnalyzer
    {
        public Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(string path, ExtractorContext context, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExtractionResult>>([new ExtractionResult { Nodes = [SemanticPoint()], ProcessedFiles = ["apps/desktop/src/lib.rs"] }]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommandRegistrationWork_GrowsWithIndexedInputRatherThanAllDeclarationPairs(bool sameNames)
    {
        long previous = 0;
        foreach (var count in new[] { 250, 500, 1000 })
        {
            var fixture = Commands(count, sameNames);
            var analysis = TauriOperationAnalysis.Analyze("App", fixture.Sources, fixture.Declarations, []);
            analysis.Result.Complete.ShouldBeTrue(string.Join(';', analysis.Diagnostics));
            analysis.Result.Nodes.Count.ShouldBe(count);
            analysis.Result.Edges.Count.ShouldBe(count);
            analysis.Result.Edges.Select(edge => edge.TargetQN).Order().ShouldBe(fixture.Declarations.Select(node => node.QualifiedName).Order());
            if (previous > 0) analysis.WorkUnits.ShouldBeLessThan(previous * 5 / 2);
            previous = analysis.WorkUnits;
            TauriOperationAnalysis.Analyze("App", fixture.Sources, fixture.Declarations, []).WorkUnits.ShouldBe(analysis.WorkUnits);
        }
    }

    [Fact]
    public void IndexedCommandAndDeclarationAmbiguity_IsPreserved()
    {
        var fixture = Commands(1);
        var declaration = fixture.Declarations.Single();
        var duplicate = declaration with { QualifiedName = "another-declaration" };
        TauriOperationAnalysis.Analyze("App", fixture.Sources, [declaration, duplicate], []).Result.Complete.ShouldBeFalse();
        fixture.Sources["apps/desktop/src/lib.rs"] = "#[tauri::command]\nfn cmd000000() {}\n#[tauri::command]\nfn cmd000000() {}\nfn run() { tauri::Builder::default().invoke_handler(tauri::generate_handler![cmd000000]); }";
        var analysis = TauriOperationAnalysis.Analyze("App", fixture.Sources, [declaration], []);
        analysis.Result.Complete.ShouldBeFalse();
        analysis.Diagnostics.ShouldContain(message => message.Contains("2 admitted command declarations"));
    }

    [Fact]
    public void UnrelatedRepositoryDeclarations_DoNotEnterNativeNameOrSpanIndexes()
    {
        var fixture = Commands(1);
        var baseline = TauriOperationAnalysis.Analyze("App", fixture.Sources, fixture.Declarations, []);
        var declarations = new List<GraphNode>(fixture.Declarations);
        var unrelatedName = new string('x', 100_000);
        for (var index = 0; index < 50_000; index++)
            declarations.Add(new GraphNode { Repository = "App", Label = NodeLabel.Function, Name = unrelatedName,
                QualifiedName = "unrelated:" + index, FilePath = "engines/worker/src/unrelated.rs", StartLine = index, EndLine = index });
        var analysis = TauriOperationAnalysis.Analyze("App", fixture.Sources, declarations, []);
        analysis.Result.Complete.ShouldBeTrue(string.Join(';', analysis.Diagnostics));
        analysis.Result.Nodes.Select(node => node.QualifiedName).ShouldBe(baseline.Result.Nodes.Select(node => node.QualifiedName));
        (analysis.WorkUnits - baseline.WorkUnits).ShouldBeLessThan(100_000);
    }

    [Fact]
    public void LongSharedModulesAndRepeatedEventScans_CannotBypassWorkBudget()
    {
        var fixture = Commands(200);
        var module = new string('m', 4096);
        fixture.Sources["apps/desktop/src/lib.rs"] = "mod " + module + " {\n" + fixture.Sources["apps/desktop/src/lib.rs"] + "\n}";
        var bounded = TauriOperationAnalysis.Analyze("App", fixture.Sources, fixture.Declarations, [], maxWork: 100_000);
        bounded.Result.Complete.ShouldBeFalse(); bounded.WorkUnits.ShouldBeLessThanOrEqualTo(100_000);
        bounded.Diagnostics.ShouldContain(message => message.Contains("work budget"));
        var complete = TauriOperationAnalysis.Analyze("App", fixture.Sources,
            fixture.Declarations.Select(node => node with { StartLine = node.StartLine + 1, EndLine = node.EndLine + 1 }).ToList(), []);
        complete.Result.Complete.ShouldBeTrue(string.Join(';', complete.Diagnostics));
        complete.Result.Nodes.Count.ShouldBe(200);

        var events = Fixture();
        events["apps/desktop/src/lib.rs"] = "use tauri::{AppHandle, Emitter};\nfn producer(app: AppHandle) {\n"
            + string.Concat(Enumerable.Repeat("app.emit(\"event\", ());\n", 1000)) + "}\n";
        var eventResult = TauriOperationAnalysis.Analyze("App", events, Functions(events), [], maxWork: 500_000);
        eventResult.Result.Complete.ShouldBeFalse(); eventResult.WorkUnits.ShouldBeLessThanOrEqualTo(500_000);
        eventResult.Diagnostics.ShouldContain(message => message.Contains("work budget"));
    }

    [Fact]
    public void WorkAndDepthExhaustion_AreIncompleteAndDiagnosticsRemainBounded()
    {
        var fixture = Commands(10);
        var budget = TauriOperationAnalysis.Analyze("App", fixture.Sources, fixture.Declarations, [], maxWork: 100);
        budget.Result.Complete.ShouldBeFalse(); budget.Result.Nodes.ShouldBeEmpty(); budget.WorkUnits.ShouldBeLessThanOrEqualTo(100);
        budget.Diagnostics.ShouldContain(message => message.Contains("work budget exhausted"));
        fixture.Sources["apps/desktop/src/lib.rs"] += "\n" + string.Concat(Enumerable.Repeat("mod nested {", 20)) + "fn helper() {}" + new string('}', 20);
        var depth = TauriOperationAnalysis.Analyze("App", fixture.Sources, fixture.Declarations, [], maxDepth: 4);
        depth.Result.Complete.ShouldBeFalse(); depth.Diagnostics.ShouldContain(message => message.Contains("depth limit"));
        var references = Enumerable.Range(0, 1000).Select(index => Reference("missing" + index)).ToList();
        var noisy = TauriOperationAnalysis.Analyze("App", fixture.Sources, fixture.Declarations, references);
        noisy.Result.Complete.ShouldBeTrue(string.Join(';', noisy.Diagnostics));
        noisy.Diagnostics.Count.ShouldBeLessThanOrEqualTo(100);
        noisy.Diagnostics.ShouldContain(message => message.StartsWith("tauriCoverage:"));
    }

    [Fact]
    public void Cancellation_IsObservedBeforeAndDuringAnalysis()
    {
        var fixture = Commands(100);
        using var preCancelled = new CancellationTokenSource(); preCancelled.Cancel();
        Should.Throw<OperationCanceledException>(() => TauriOperationAnalysis.Analyze("App", fixture.Sources, fixture.Declarations, [], preCancelled.Token));
        using var midCancelled = new CancellationTokenSource();
        var cancellingDeclarations = new CancellingDeclarations(fixture.Declarations, midCancelled);
        Should.Throw<OperationCanceledException>(() => TauriOperationAnalysis.Analyze("App", fixture.Sources, cancellingDeclarations, [], midCancelled.Token));
        cancellingDeclarations.Read.ShouldBeGreaterThan(0);
        cancellingDeclarations.Read.ShouldBeLessThan(fixture.Declarations.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RefusedAnalysis_PreservesPersistedGraphHashesAndUnchangedReruns(bool rebuild, bool depth)
    {
        var root = Path.Combine(Path.GetTempPath(), "codegraph-tauri-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sources = Fixture();
            sources["apps/desktop/src/lib.rs"] += string.Concat(Enumerable.Repeat("\nmod nested {", 8)) + "fn helper() {}" + new string('}', 8);
            Write(root, sources);
            var store = new InMemoryGraphStore();
            IndexingPipeline Create(IndexingOptions options) => new(store, [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(options), new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance);
            var pipeline = Create(new IndexingOptions());
            await pipeline.IndexRepositoryAsync("App", root);
            var before = JsonSerializer.Serialize(new { store.Nodes, store.Edges });
            var hashes = store.FileHashesOf("App").ToDictionary();
            var writes = store.ReplaceRepositoryFilesCallCount;
            await pipeline.IndexRepositoryAsync("App", root);
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
            var options = new IndexingOptions();
            if (depth) options.MaxTauriAnalysisDepth = 4; else options.MaxTauriAnalysisWork = 100;
            sources["apps/desktop/src/lib.rs"] += "\n// source changed, replacement must be withheld"; Write(root, sources);
            (await Should.ThrowAsync<InvalidOperationException>(() => Create(options).IndexRepositoryAsync("App", root, replaceExistingGraph: rebuild)))
                .Message.ShouldContain(depth ? "depth limit" : "work budget");
            JsonSerializer.Serialize(new { store.Nodes, store.Edges }).ShouldBe(before);
            store.FileHashesOf("App").ShouldBe(hashes);
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WorkPolicyParticipatesInRoutineSyncFingerprint()
    {
        IndexingPipeline Create(long work, int depth) => new(new InMemoryGraphStore(), [], Options.Create(new IndexingOptions
            { MaxTauriAnalysisWork = work, MaxTauriAnalysisDepth = depth }), new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance);
        Create(100, 4).ExtractionFingerprint.ShouldNotBe(Create(101, 4).ExtractionFingerprint);
        Create(100, 4).ExtractionFingerprint.ShouldNotBe(Create(100, 5).ExtractionFingerprint);
        ExtractionVersionCatalog.PipelineVersion.ShouldBeGreaterThanOrEqualTo(10);
    }

    private static (Dictionary<string, string> Sources, List<GraphNode> Declarations) Commands(int count, bool sameNames = false)
    {
        var sources = Fixture(); var declarations = new List<GraphNode>(); var content = new StringBuilder(); var registrations = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var name = sameNames ? "command" : $"cmd{index:D6}";
            var target = sameNames ? $"m{index:D6}::{name}" : name;
            if (sameNames) content.Append("mod m").Append(index.ToString("D6")).Append(" { #[tauri::command(rename=\"command")
                .Append(index.ToString("D6")).Append("\")] fn ").Append(name).Append("() {} }\n");
            else content.Append("#[tauri::command]\nfn ").Append(name).Append("() {}\n");
            registrations.Add(target);
            var line = sameNames ? index + 1 : index * 2 + 2;
            // Same Rust names need distinct exposed names too, so each remains a valid binding.
            declarations.Add(new GraphNode { Repository = "App", Label = NodeLabel.Function, Name = name, QualifiedName = "rust:" + target,
                FilePath = "apps/desktop/src/lib.rs", StartLine = line, EndLine = line });
        }
        content.Append("fn run() { tauri::Builder::default().invoke_handler(tauri::generate_handler![")
            .AppendJoin(',', registrations).Append("]); }");
        sources["apps/desktop/src/lib.rs"] = content.ToString(); return (sources, declarations);
    }

    private sealed class CancellingDeclarations(IReadOnlyList<GraphNode> declarations, CancellationTokenSource cancellation) : IReadOnlyList<GraphNode>
    {
        public int Read { get; private set; }
        public int Count => declarations.Count;
        public GraphNode this[int index] => declarations[index];
        public IEnumerator<GraphNode> GetEnumerator()
        {
            foreach (var declaration in declarations)
            {
                if (++Read == 10) cancellation.Cancel();
                yield return declaration;
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void RawByteStringsWithEmbeddedQuotes_DoNotExposeApostrophesInLaterStrings()
    {
        var sources = Fixture();
        sources["apps/desktop/src/lib.rs"] = """
            fn helper() {
                let xml = br#"<?xml version="1.0" encoding="UTF-8"?>"#;
                let error = "'nvidia-smi' is not recognized as an internal or external command";
                let escaped = "quoted \"'text'\"";
            }
            """ + "\n" + sources["apps/desktop/src/lib.rs"];
        var analysis = TauriOperationAnalysis.Analyze("App", sources, Functions(sources), [Reference("get_app_overview")]);
        analysis.Result.Complete.ShouldBeTrue(string.Join(';', analysis.Diagnostics));
        analysis.Result.Edges.ShouldContain(edge => edge.Type == EdgeType.CALLS && edge.TargetQN == "rust:apps/desktop/src/lib.rs:get_app_overview");
    }

    [Theory]
    [InlineData("'\\\"'")]
    [InlineData("'\\''")]
    [InlineData("'\\\\'")]
    [InlineData("'\\n'")]
    [InlineData("'\\r'")]
    [InlineData("'\\t'")]
    [InlineData("'\\0'")]
    [InlineData("'\\x22'")]
    [InlineData("'\\x27'")]
    [InlineData("'\\x5c'")]
    [InlineData("'\\u{22}'")]
    [InlineData("'\\u{1_f4a9}'")]
    [InlineData("'💖'")]
    [InlineData("'{'")]
    [InlineData("'}'")]
    [InlineData("')'")]
    [InlineData("b'\\\"'")]
    [InlineData("b'\\xFF'")]
    public async Task CharacterLiteralInUnrelatedHelper_PreservesLifetimesAndBindsRegisteredRustFunction(string literal)
    {
        var sources = Fixture();
        sources["apps/desktop/src/lib.rs"] = """
            fn helper<'a>(value: &'a str) -> &'a str {
                'outer: loop { break 'outer; }
                let borrowed: &'_ str = value;
                let raw = r#"quotes '\"' and brackets [({"#;
                let character =
            """ + literal + ";\nvalue\n}\n" + sources["apps/desktop/src/lib.rs"];
        var extractor = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);
        var extracted = await extractor.ExtractAsync("/test/apps/desktop/src/lib.rs", sources["apps/desktop/src/lib.rs"],
            new ExtractorContext { RepositoryName = "App", RootPath = "/test" });
        extracted.Succeeded.ShouldBeTrue();
        var actual = extracted.Nodes.Single(node => node.Name == "get_app_overview" && node.Label == NodeLabel.Function);
        var analysis = TauriOperationAnalysis.Analyze("App", sources, extracted.Nodes, [Reference("get_app_overview")]);
        analysis.Result.Complete.ShouldBeTrue(string.Join(';', analysis.Diagnostics));
        var command = analysis.Result.Nodes.Single(node => node.Properties.GetValueOrDefault("kind")?.ToString() == "tauri_command");
        analysis.Result.Edges.ShouldContain(edge => edge.SourceQN == command.QualifiedName && edge.TargetQN == actual.QualifiedName && edge.Type == EdgeType.CALLS);
    }

    [Theory]
    [InlineData("'\\q'")]
    [InlineData("'\\x2'")]
    [InlineData("'\\xGG'")]
    [InlineData("'\\xFF'")]
    [InlineData("b'\\u{22}'")]
    [InlineData("'\\u{}'")]
    [InlineData("'\\u{110000}'")]
    [InlineData("'\\u{d800}'")]
    [InlineData("'\\u{1234567}'")]
    [InlineData("'\\u{22'")]
    [InlineData("'\\\"")]
    [InlineData("''")]
    [InlineData("'ab'")]
    [InlineData("'\n'")]
    public void MalformedCharacterLiteral_IsExplicitlyIncomplete(string literal)
    {
        var sources = Fixture();
        sources["apps/desktop/src/lib.rs"] = "fn helper() { let bad = " + literal + "; }\n" + sources["apps/desktop/src/lib.rs"];
        var analysis = TauriOperationAnalysis.Analyze("App", sources, Functions(sources), [Reference("get_app_overview")]);
        analysis.Result.Complete.ShouldBeFalse();
        analysis.Diagnostics.ShouldContain(message => message.StartsWith("tauriIncomplete:") && message.Contains("character literal"));
    }

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

    [Fact]
    public async Task NativeEventRenameAndRemoval_PersistExactlyTheFreshEventGraph()
    {
        var sources = Fixture();
        string Producer(string? name) => "use tauri::{AppHandle, Emitter};\n#[tauri::command]\nfn get_app_overview(app: AppHandle) { "
            + (name is null ? "" : $"app.emit(\"{name}\", ());")
            + " }\nfn run() { tauri::Builder::default().invoke_handler(tauri::generate_handler![get_app_overview]); }";
        var root = Path.Combine(Path.GetTempPath(), "codegraph-tauri-event-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryGraphStore(); var pipeline = Pipeline(store);
            foreach (var name in new string?[] { "before", "after", null })
            {
                sources["apps/desktop/src/lib.rs"] = Producer(name); Write(root, sources);
                var functions = Functions(sources);
                var buffer = new GraphBuffer(); foreach (var node in functions) buffer.AddNode(node);
                var incremental = store.Nodes.Count > 0;
                var changed = await Prepare(pipeline, root, sources.Keys.ToList(), buffer, incremental);
                if (!incremental)
                    await store.ReplaceRepositoryGraphAsync("App", buffer.AllNodes.ToList(), buffer.AllPendingEdges.ToList(), new Dictionary<string, string>(), new RepositoryEntity { Name = "App" }, null);
                else
                    await store.ReplaceRepositoryFilesAsync("App", changed, buffer.AllNodes.Where(node => changed.Contains(node.FilePath)).ToList(), buffer.AllPendingEdges.ToList(), new Dictionary<string, string>());
                var fresh = TauriOperationAnalysis.Analyze("App", sources, functions, []);
                fresh.Result.Complete.ShouldBeTrue();
                var currentOwnNodes = store.Nodes.Where(node => node.Properties.GetValueOrDefault("extraction_owner")?.ToString() == "tauri").Select(node => node.QualifiedName).Order();
                currentOwnNodes.ShouldBe(fresh.Result.Nodes.Select(node => node.QualifiedName).Order());
                var byId = store.Nodes.ToDictionary(node => node.Id);
                var currentEdges = store.Edges.Where(edge => edge.Properties.GetValueOrDefault("extraction_owner")?.ToString() == "tauri")
                    .Select(edge => $"{byId[edge.SourceId].QualifiedName}|{edge.Type}|{byId[edge.TargetId].QualifiedName}").Order();
                currentEdges.ShouldBe(fresh.Result.Edges.Select(edge => $"{edge.SourceQN}|{edge.Type}|{edge.TargetQN}").Order());
                var same = new GraphBuffer(); foreach (var node in functions) same.AddNode(node);
                (await Prepare(pipeline, root, sources.Keys.ToList(), same, true)).ShouldBeEmpty();
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static IndexingPipeline Pipeline(InMemoryGraphStore store) => new(store, [], Options.Create(new IndexingOptions()),
        new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance);
    private static async Task<IReadOnlyList<string>> Prepare(IndexingPipeline pipeline, string root, IReadOnlyList<string> scope, GraphBuffer buffer, bool incremental)
    {
        var method = typeof(IndexingPipeline).GetMethod("PrepareTauriBindingsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<IReadOnlyList<string>>)method.Invoke(pipeline,
            ["App", root, scope, buffer, new HashSet<string>(), new HashSet<string>(), incremental, new RegistrationSourceReader(new LocalFileSystem(), new IndexingOptions()), CancellationToken.None])!;
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
