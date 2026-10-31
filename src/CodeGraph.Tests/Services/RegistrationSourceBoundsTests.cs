using System.IO.Hashing;
using System.Reflection;
using System.Text;
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

public class RegistrationSourceBoundsTests
{
    [Fact]
    public async Task MetadataOversize_DoesNotReadBody_AndPerExtensionLimitApplies()
    {
        var files = new TrackingFiles();
        var path = files.Add("huge.rs", new string('x', 9000));
        var reader = new RegistrationSourceReader(files, new IndexingOptions { MaxFileSizeKb = 1 });
        (await reader.ReadAsync(path, default)).ShouldBeNull();
        files.BytesRead.ShouldBe(0);
        var config = files.Add("tauri.conf.json", new string('x', 2000));
        var configured = new RegistrationSourceReader(files, new IndexingOptions
        { MaxFileSizeKb = 1, MaxFileSizeKbByExtension = new() { [".json"] = 2 } });
        (await configured.ReadAsync(config, default))!.Content.Length.ShouldBe(2000);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("utf32")]
    public async Task DecodedLimitAcceptsUnicode_AndHashCoversOriginalBytes(string encoding)
    {
        Encoding codec = encoding switch { "utf16" => Encoding.Unicode, "utf32" => Encoding.UTF32, _ => new UTF8Encoding(true) };
        var content = new string('界', 1000);
        var bytes = codec.GetPreamble().Concat(codec.GetBytes(content)).ToArray();
        var files = new TrackingFiles();
        var path = files.Add("unicode.rs", bytes);
        var result = await new RegistrationSourceReader(files, new IndexingOptions { MaxFileSizeKb = 1 }).ReadAsync(path, default);
        result!.Content.ShouldBe(content);
        result.Hash.ShouldBe(Convert.ToHexString(XxHash3.Hash(bytes)));
    }

    [Fact]
    public async Task OversizedPrefixesConsumeAggregateBudget_AndCachedPathsAreNotChargedAgain()
    {
        var files = new TrackingFiles();
        var first = files.Add("first.rs", new string('x', 1200));
        var second = files.Add("second.rs", new string('x', 1200));
        var reader = new RegistrationSourceReader(files, new IndexingOptions { MaxFileSizeKb = 1, MaxRegistrationAnalysisTextKb = 2 });
        (await reader.ReadAsync(first, default)).ShouldBeNull();
        (await reader.ReadAsync(Path.Combine(Path.GetDirectoryName(first)!, ".", Path.GetFileName(first)), default)).ShouldBeNull();
        files.OpenCount.ShouldBe(1);
        (await Should.ThrowAsync<InvalidOperationException>(() => reader.ReadAsync(second, default))).Message.ShouldContain("budget exhausted");
    }

    [Fact]
    public async Task AcceptedSourcesShareBudgetAcrossPasses_AndCancellationPrecedesOpening()
    {
        var files = new TrackingFiles();
        var first = files.Add("first.rs", new string('x', 700));
        var second = files.Add("Cargo.toml", new string('x', 700));
        var reader = new RegistrationSourceReader(files, new IndexingOptions { MaxFileSizeKb = 1, MaxRegistrationAnalysisTextKb = 1 });
        (await reader.ReadAsync(first, default))!.Content.Length.ShouldBe(700);
        (await reader.ReadAsync(first, default))!.Content.Length.ShouldBe(700);
        files.OpenCount.ShouldBe(1);
        await Should.ThrowAsync<InvalidOperationException>(() => reader.ReadAsync(second, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var opened = files.OpenCount;
        await Should.ThrowAsync<OperationCanceledException>(() => reader.ReadAsync(first, cancellation.Token));
        files.OpenCount.ShouldBe(opened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndpointOversize_NeverParses_NewSourcesSkip_ExistingHandlerFactsAndHashesSurvive(bool incremental)
    {
        var root = Path.Combine(Path.GetTempPath(), "codegraph-bounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var full = Path.Combine(root, "app.rs"); File.WriteAllText(full, new string('x', 9000));
            var files = new TrackingFiles(); files.Add(full, new string('x', 9000));
            var extractor = new CountingEndpointExtractor();
            var store = new InMemoryGraphStore();
            var options = new IndexingOptions { MaxFileSizeKb = 1 };
            var pipeline = new IndexingPipeline(store, [extractor], Options.Create(options), files, NullLogger<IndexingPipeline>.Instance);
            (await Endpoints(pipeline, root, new RegistrationSourceReader(files, options), false)).ShouldBeEmpty();
            extractor.ParseCount.ShouldBe(0); files.BytesRead.ShouldBe(0);
            var handler = new GraphNode { Repository = "App", Label = NodeLabel.Function, Name = "handle", QualifiedName = "App:handle", FilePath = "app.rs" };
            var route = new GraphNode { Repository = "App", Label = NodeLabel.Route, Name = "/x", QualifiedName = "route:App:GET:/x", FilePath = "routes.rs", Properties = new() { ["semantic_owner"] = "http_endpoints" } };
            await store.ReplaceRepositoryGraphAsync("App", [handler, route], [new PendingEdge(route.QualifiedName, handler.QualifiedName, EdgeType.HANDLES, new())], new Dictionary<string, string> { ["app.rs"] = "certified" }, new() { Name = "App" }, null);
            await Should.ThrowAsync<InvalidOperationException>(() => Endpoints(pipeline, root, new RegistrationSourceReader(files, options), incremental));
            extractor.ParseCount.ShouldBe(0); files.BytesRead.ShouldBe(0);
            store.Nodes.Count.ShouldBe(2); store.Edges.Count.ShouldBe(1);
            store.FileHashesOf("App")["app.rs"].ShouldBe("certified");
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("tauri.conf.json")]
    [InlineData("Cargo.toml")]
    public async Task RequiredTauriConfiguration_IsBoundedBeforeAnalysis(string oversized)
    {
        var files = new TrackingFiles(); var root = Path.Combine(Path.GetTempPath(), "codegraph-bounds-tauri");
        files.Add(Path.Combine(root, "tauri.conf.json"), oversized == "tauri.conf.json" ? new string('x', 9000) : "{}");
        files.Add(Path.Combine(root, "Cargo.toml"), oversized == "Cargo.toml" ? new string('x', 9000) : "[package]\nname=\"app\"");
        files.Add(Path.Combine(root, "package.json"), new string('x', 100_000));
        var options = new IndexingOptions { MaxFileSizeKb = 1 };
        var pipeline = new IndexingPipeline(new InMemoryGraphStore(), [], Options.Create(options), files, NullLogger<IndexingPipeline>.Instance);
        var method = typeof(IndexingPipeline).GetMethod("PrepareTauriBindingsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<IReadOnlyList<string>>)method.Invoke(pipeline, ["App", root, new[] { "tauri.conf.json", "Cargo.toml", "package.json" }, new GraphBuffer(), new HashSet<string>(), new HashSet<string>(), false, new RegistrationSourceReader(files, options), CancellationToken.None])!;
        (await Should.ThrowAsync<InvalidOperationException>(() => task)).Message.ShouldContain("exceeds its text limit");
        files.Opened.ShouldNotContain(Path.Combine(root, "package.json"));
        files.BytesRead.ShouldBeLessThan(100);
    }

    [Fact]
    public void AggregateBudgetParticipatesInRoutineSyncFingerprint()
    {
        IndexingPipeline Pipeline(int budget) => new(new InMemoryGraphStore(), [], Options.Create(new IndexingOptions { MaxRegistrationAnalysisTextKb = budget }), new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance);
        Pipeline(1).ExtractionFingerprint.ShouldNotBe(Pipeline(2).ExtractionFingerprint);
    }

    [Fact]
    public void AnalyzerCaseSelection_IsSpecificToTypeScript()
    {
        var catalog = new ExtractionVersionCatalog([], typeScriptAnalyzer: new EmptyTypeScript());
        catalog.ForPath("App.TSX").ShouldBe(catalog.ForPath("App.tsx"));
        catalog.ForPath("App.TSX").ShouldContain("EmptyTypeScript=1");
    }

    private static Task<IReadOnlyList<string>> Endpoints(IndexingPipeline pipeline, string root, RegistrationSourceReader reader, bool incremental) =>
        (Task<IReadOnlyList<string>>)typeof(IndexingPipeline).GetMethod("PrepareBackendEndpointsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pipeline,
            ["App", root, new ExtractorContext { RepositoryName = "App", RootPath = root }, new GraphBuffer(), new HashSet<string>(), Array.Empty<string>(), incremental, reader, CancellationToken.None])!;

    private sealed class EmptyTypeScript : ITypeScriptAnalyzer
    {
        public Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(string path, ExtractorContext context, IReadOnlyList<string> includedFiles, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ExtractionResult>>([]);
    }
    private sealed class CountingEndpointExtractor : ICodeExtractor, IBackendEndpointExtractor
    {
        public int ParseCount;
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".rs" };
        public bool SupportsBackendEndpoints(string extension) => true;
        public bool MayContainBackendEndpoints(string extension, string content) => true;
        public Task<ExtractionResult> ExtractAsync(string path, string content, ExtractorContext context, CancellationToken ct = default) => Task.FromResult(new ExtractionResult());
        public Task<ExtractionResult> ExtractBackendEndpointsAsync(string path, string content, ExtractorContext context, IReadOnlyCollection<GraphNode> nodes, CancellationToken ct = default)
        { ParseCount++; return Task.FromResult(new ExtractionResult()); }
    }
    private sealed class TrackingFiles : IFileSystem
    {
        private readonly Dictionary<string, byte[]> _bodies = new();
        public int BytesRead;
        public int OpenCount => Opened.Count;
        public List<string> Opened { get; } = [];
        public string Add(string path, string content) => Add(path, Encoding.UTF8.GetBytes(content));
        public string Add(string path, byte[] bytes) { path = Path.GetFullPath(path); _bodies[path] = bytes; return path; }
        public Stream OpenRead(string path, string? repositoryRoot = null) { Opened.Add(path); return new CountingStream(_bodies[path], count => BytesRead += count); }
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool FileExists(string path) => _bodies.ContainsKey(Path.GetFullPath(path));
        public IEnumerable<string> EnumerateFiles(string root, string pattern, SearchOption option) => Directory.EnumerateFiles(root, pattern, option);
        public string GetRelativePath(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');
        public string ReadAllText(string path) => throw new InvalidOperationException("Unbounded read");
        public Task<string> ReadAllTextAsync(string path, CancellationToken ct = default) => throw new InvalidOperationException("Unbounded read");
        public string[] ReadAllLines(string path) => throw new InvalidOperationException("Unbounded read");
        public Task<string[]> ReadAllLinesAsync(string path, CancellationToken ct = default) => throw new InvalidOperationException("Unbounded read");
        public byte[] ReadAllBytes(string path) => throw new InvalidOperationException("Unbounded read");
        private sealed class CountingStream(byte[] bytes, Action<int> counted) : MemoryStream(bytes)
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            { var result = base.ReadAsync(buffer, ct); counted(result.Result); return result; }
        }
    }
}
