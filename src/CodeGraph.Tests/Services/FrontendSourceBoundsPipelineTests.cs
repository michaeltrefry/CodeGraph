using System.Runtime.InteropServices;
using System.Reflection;
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

public sealed class FrontendSourceBoundsPipelineTests
{
    [Theory]
    [InlineData(false, false, "source")]
    [InlineData(false, true, "source")]
    [InlineData(true, false, "source")]
    [InlineData(true, true, "source")]
    [InlineData(false, false, "package")]
    [InlineData(true, true, "package")]
    [InlineData(false, true, "config")]
    [InlineData(true, true, "config")]
    public async Task OversizedProjectInput_RefusesBeforeAnalyzer_AndPreservesGraphAndHashes(bool incremental, bool tsconfig, string kind)
    {
        var root = Root();
        try
        {
            var source = tsconfig ? "app.tsx" : "app.jsx";
            File.WriteAllText(Path.Combine(root, source), "export function App(){return null}");
            File.WriteAllText(Path.Combine(root, "package.json"), "{}");
            if (tsconfig) File.WriteAllText(Path.Combine(root, "tsconfig.json"), "{}");
            var analyzer = new Analyzer(); var store = new InMemoryGraphStore();
            var pipeline = Pipeline(store, analyzer, new IndexingOptions { MaxFileSizeKb = 1 });
            await pipeline.IndexRepositoryAsync("UI", root);
            var nodes = store.Nodes.ToList(); var hashes = store.FileHashesOf("UI").ToDictionary();
            var calls = analyzer.Calls; var writes = store.ReplaceRepositoryFilesCallCount;
            await pipeline.IndexRepositoryAsync("UI", root);
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
            calls = analyzer.Calls;
            var refused = kind == "source" ? source : kind == "package" ? "package.json" : "tsconfig.json";
            File.AppendAllText(Path.Combine(root, refused), new string(' ', 9000));
            await Should.ThrowAsync<InvalidOperationException>(() => pipeline.IndexRepositoryAsync("UI", root, replaceExistingGraph: !incremental));
            analyzer.Calls.ShouldBe(calls);
            store.Nodes.ShouldBe(nodes); store.FileHashesOf("UI").ShouldBe(hashes);
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SharedAggregateAndExtensionLimits_RefuseWholeProject_AndParticipateInFingerprint()
    {
        var root = Root();
        try
        {
            File.WriteAllText(Path.Combine(root, "app.js"), "export const x=1;" + new string(' ', 700));
            File.WriteAllText(Path.Combine(root, "other.tsx"), "export const y=2;" + new string(' ', 700));
            var store = new InMemoryGraphStore(); var analyzer = new Analyzer();
            var allowed = new IndexingOptions { MaxFileSizeKb = 1, MaxFrontendAnalysisTextKb = 2 };
            var limited = new IndexingOptions { MaxFileSizeKb = 1, MaxFrontendAnalysisTextKb = 1 };
            var accepted = Pipeline(store, analyzer, allowed); var refused = Pipeline(store, analyzer, limited);
            accepted.ExtractionFingerprint.ShouldNotBe(refused.ExtractionFingerprint);
            await Should.ThrowAsync<InvalidOperationException>(() => refused.IndexRepositoryAsync("UI", root));
            analyzer.Calls.ShouldBe(0); store.FileHashesOf("UI").ShouldBeEmpty();
            await accepted.IndexRepositoryAsync("UI", root);
            analyzer.Limits.MaxAnalysisTextKb.ShouldBe(2);
            analyzer.Limits.MaxFileSizeKb.ShouldBe(1);
            var hashes = store.FileHashesOf("UI").ToDictionary(); var nodes = store.Nodes.ToList();
            await Should.ThrowAsync<InvalidOperationException>(() => refused.IndexRepositoryAsync("UI", root));
            store.Nodes.ShouldBe(nodes); store.FileHashesOf("UI").ShouldBe(hashes);
            var extension = new IndexingOptions { MaxFileSizeKb = 0, MaxFileSizeKbByExtension = new() { [".js"] = 1, [".tsx"] = 1 } };
            await Pipeline(store, analyzer, extension).IndexRepositoryAsync("UI", root);
            analyzer.Limits.MaxFileSizeKbByExtension[".tsx"].ShouldBe(1);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsafeFrontendSource_IsRejectedBeforeOrdinaryHashing_WithoutBlocking(bool fifo)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var root = Root();
        try
        {
            var source = Path.Combine(root, "app.js");
            if (fifo) MkFifo(source, Convert.ToUInt32("600", 8)).ShouldBe(0);
            else File.CreateSymbolicLink(source, "/dev/zero");
            var analyzer = new Analyzer(); var store = new InMemoryGraphStore();
            var task = Task.Run(() => Pipeline(store, analyzer, new()).IndexRepositoryAsync("UI", root));
            (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)))).ShouldBe(task);
            await Should.ThrowAsync<InvalidOperationException>(() => task);
            analyzer.Calls.ShouldBe(0); store.FileHashesOf("UI").ShouldBeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EmptySourceFlood_IsRefusedBeforeReadingAnySource()
    {
        var root = Root();
        try
        {
            var analyzer = new Analyzer(); var pipeline = Pipeline(new InMemoryGraphStore(), analyzer, new());
            var scope = Enumerable.Range(0, TypeScriptSourceLimits.MaxReadFiles + 1).Select(index => $"missing-{index}.js").ToList();
            var method = typeof(IndexingPipeline).GetMethod("ReadFrontendInputsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var task = (Task)method.Invoke(pipeline, [root, scope, CancellationToken.None])!;
            var error = await Should.ThrowAsync<InvalidOperationException>(() => task);
            error.InnerException.ShouldNotBeNull().Message.ShouldContain("file count");
            analyzer.Calls.ShouldBe(0);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemplateRefusal_IsDelegatedWithoutOrdinaryRead_AndPreservesGraphAndHashes(bool incremental)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var root = Root();
        try
        {
            File.WriteAllText(Path.Combine(root, "app.ts"), "export class App {}");
            File.WriteAllText(Path.Combine(root, "view.html"), new string('x', 9000));
            var analyzer = new Analyzer(); var store = new InMemoryGraphStore();
            var pipeline = Pipeline(store, analyzer, new() { MaxFileSizeKb = 1 });
            // Admission alone must not open an unreferenced HTML file, regardless of its size.
            await pipeline.IndexRepositoryAsync("UI", root);
            var nodes = store.Nodes.ToList(); var hashes = store.FileHashesOf("UI").ToDictionary();
            File.Delete(Path.Combine(root, "view.html"));
            MkFifo(Path.Combine(root, "view.html"), Convert.ToUInt32("600", 8)).ShouldBe(0);
            analyzer.Complete = false; // A bounded sidecar refuses a now-required unsafe template.
            var task = Task.Run(() => pipeline.IndexRepositoryAsync("UI", root,
                changedFilesOnly: ["view.html"], replaceExistingGraph: !incremental));
            (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)))).ShouldBe(task);
            await Should.ThrowAsync<InvalidOperationException>(() => task);
            store.Nodes.ShouldBe(nodes); store.FileHashesOf("UI").ShouldBe(hashes);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExcludedFrontendChangeList_DoesNotBypassAdmissionBeforeHashing()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var root = Root();
        try
        {
            File.WriteAllText(Path.Combine(root, "app.js"), "export const x=1;");
            Directory.CreateDirectory(Path.Combine(root, ".cache"));
            MkFifo(Path.Combine(root, ".cache", "ignored.js"), Convert.ToUInt32("600", 8)).ShouldBe(0);
            var store = new InMemoryGraphStore(); var analyzer = new Analyzer();
            var pipeline = Pipeline(store, analyzer, new());
            var task = Task.Run(() => pipeline.IndexRepositoryAsync("UI", root, changedFilesOnly: [".cache/ignored.js"]));
            (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)))).ShouldBe(task);
            await task;
            store.FileHashesOf("UI").ShouldNotContainKey(".cache/ignored.js");
        }
        finally { Directory.Delete(root, true); }
    }

    private static IndexingPipeline Pipeline(InMemoryGraphStore store, Analyzer analyzer, IndexingOptions options) =>
        new(store, [], Options.Create(options), new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance, typeScriptAnalyzer: analyzer);
    private static string Root() { var root = Path.Combine(Path.GetTempPath(), "codegraph-frontend-bounds-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)] private static extern int MkFifo(string path, uint mode);
    private sealed class Analyzer : ITypeScriptAnalyzer
    {
        public int Calls;
        public bool Complete = true;
        public TypeScriptSourceLimits Limits = new();
        public Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(string config, ExtractorContext context, IReadOnlyList<string> scope, CancellationToken ct = default)
        {
            Calls++; Limits = context.TypeScriptSourceLimits;
            var sources = scope.Where(path => Path.GetExtension(path) is ".js" or ".jsx" or ".ts" or ".tsx").ToList();
            return Task.FromResult<IReadOnlyList<ExtractionResult>>([new() { Complete = Complete, ProcessedFiles = sources, Nodes = sources.Select(path => new GraphNode
            { Repository = "UI", Label = NodeLabel.Function, Name = "App", QualifiedName = "UI:" + path + ":App", FilePath = path }).ToList() }]);
        }
    }
}
