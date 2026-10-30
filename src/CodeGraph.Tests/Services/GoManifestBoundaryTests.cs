using System.Runtime.InteropServices;
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

public sealed class GoManifestBoundaryTests
{
    private const string Routes = "package server\nimport (\"github.com/gin-gonic/gin\"; \"example.com/app/internal/admin\")\nfunc Setup(r *gin.Engine) { r.GET(\"/metrics\", admin.Handle) }";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OversizeOrEscapingManifest_PreservesCertifiedGraphAndHashes(bool incremental, bool symlink)
    {
        if (symlink && OperatingSystem.IsWindows()) return;
        var root = Root();
        var outside = Path.Combine(Path.GetTempPath(), "codegraph-outside-module-" + Guid.NewGuid().ToString("N"));
        try
        {
            Fixture(root);
            var store = new InMemoryGraphStore(); var pipeline = Pipeline(store);
            await pipeline.IndexRepositoryAsync("App", root);
            var route = store.Nodes.Single(node => node.Label == NodeLabel.Route);
            var target = store.Nodes.Single(node => node.FilePath == "internal/admin/handler.go" && node.Name == "Handle");
            store.Edges.ShouldContain(edge => edge.Type == EdgeType.HANDLES && edge.SourceId == route.Id && edge.TargetId == target.Id);
            var nodes = store.Nodes.ToList(); var edges = store.Edges.ToList(); var hashes = store.FileHashesOf("App").ToDictionary();
            var writes = store.ReplaceRepositoryFilesCallCount;
            await pipeline.IndexRepositoryAsync("App", root);
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
            if (symlink)
            {
                File.WriteAllText(outside, "module example.com/app\n");
                File.Delete(Path.Combine(root, "go.mod")); File.CreateSymbolicLink(Path.Combine(root, "go.mod"), outside);
            }
            else File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/app\n" + new string(' ', 9000));
            await Should.ThrowAsync<InvalidOperationException>(() => pipeline.IndexRepositoryAsync("App", root, replaceExistingGraph: !incremental));
            store.Nodes.ShouldBe(nodes); store.Edges.ShouldBe(edges); store.FileHashesOf("App").ShouldBe(hashes);
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
        }
        finally { Directory.Delete(root, true); File.Delete(outside); }
    }

    [Fact]
    public async Task ManifestOnlyChange_ReconcilesImportedHandler_AndUnchangedRunDoesNotWrite()
    {
        var root = Root();
        try
        {
            Fixture(root);
            var store = new InMemoryGraphStore(); var pipeline = Pipeline(store);
            await pipeline.IndexRepositoryAsync("App", root);
            store.Edges.ShouldContain(edge => edge.Type == EdgeType.HANDLES);
            File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/different\n");
            await pipeline.IndexRepositoryAsync("App", root, changedFilesOnly: ["go.mod"]);
            store.Edges.ShouldNotContain(edge => edge.Type == EdgeType.HANDLES);
            var route = store.Nodes.Single(node => node.Label == NodeLabel.Route);
            Convert.ToString(route.Properties["resolution_status"]).ShouldBe("unresolved_handler");
            var writes = store.ReplaceRepositoryFilesCallCount;
            await pipeline.IndexRepositoryAsync("App", root);
            store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DirectExtractor_UsesSuppliedManifest_AndNeverReadsDiskManifest()
    {
        var root = Root();
        try
        {
            File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/different\n");
            var context = new ExtractorContext { RepositoryName = "App", RootPath = root,
                GoModuleManifests = new Dictionary<string, string> { ["go.mod"] = "module example.com/app\n" } };
            var handler = Handler("internal/admin/handler.go");
            var collision = Handler("shadow/internal/admin/handler.go");
            var result = await Extractor().ExtractBackendEndpointsAsync(Path.Combine(root, "server/routes.go"), Routes, context, [handler, collision]);
            result.Succeeded.ShouldBeTrue(result.FailureReason);
            result.Edges.Single(edge => edge.Type == EdgeType.HANDLES).TargetQN.ShouldBe(handler.QualifiedName);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedManifest_PreservesCertifiedGraphAndHashes(bool incremental)
    {
        var root = Root();
        try
        {
            Fixture(root);
            var store = new InMemoryGraphStore(); var pipeline = Pipeline(store);
            await pipeline.IndexRepositoryAsync("App", root);
            var before = store.Edges.ToList(); var hashes = store.FileHashesOf("App").ToDictionary();
            File.WriteAllText(Path.Combine(root, "go.mod"), "go 1.24\n");
            await Should.ThrowAsync<InvalidOperationException>(() => pipeline.IndexRepositoryAsync("App", root, replaceExistingGraph: !incremental));
            store.Edges.ShouldBe(before); store.FileHashesOf("App").ShouldBe(hashes);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExcludedManifest_IsNotRead_AndMissingModuleDoesNotGuessImportedHandler()
    {
        var root = Root();
        try
        {
            Fixture(root);
            // Even a matching module on disk cannot prove an import when discovery excludes it.
            File.AppendAllText(Path.Combine(root, "go.mod"), new string(' ', 9000));
            var store = new InMemoryGraphStore();
            await Pipeline(store, new IndexingOptions { MaxFileSizeKb = 1, SkipPatterns = ["**/go.mod"] }).IndexRepositoryAsync("App", root);
            store.Edges.ShouldNotContain(edge => edge.Type == EdgeType.HANDLES);
            var context = new ExtractorContext { RepositoryName = "App", RootPath = root };
            var result = await Extractor().ExtractBackendEndpointsAsync(Path.Combine(root, "server/routes.go"), Routes, context, [Handler("internal/admin/handler.go")]);
            result.Succeeded.ShouldBeTrue(); result.Edges.ShouldBeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SharedReader_ManifestChargesAggregateOnce_AndCancellationPrecedesOpening()
    {
        var root = Root();
        try
        {
            var manifest = Path.Combine(root, "go.mod"); var source = Path.Combine(root, "source.go");
            File.WriteAllText(manifest, "module example.com/app\n" + new string(' ', 600));
            File.WriteAllText(source, new string(' ', 600));
            var reader = new RegistrationSourceReader(new LocalFileSystem(), new IndexingOptions { MaxFileSizeKb = 1, MaxRegistrationAnalysisTextKb = 1 }, root);
            var first = await reader.ReadAsync(manifest, default);
            (await reader.ReadAsync(Path.Combine(root, ".", "go.mod"), default)).ShouldBeSameAs(first);
            (await Should.ThrowAsync<InvalidOperationException>(() => reader.ReadAsync(source, default))).Message.ShouldContain("budget exhausted");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => reader.ReadAsync(manifest, cancelled.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonRegularManifest_IsRejectedWithoutBlocking(bool deviceSymlink)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        var root = Root();
        try
        {
            Fixture(root);
            var store = new InMemoryGraphStore(); var pipeline = Pipeline(store);
            await pipeline.IndexRepositoryAsync("App", root);
            var edges = store.Edges.ToList(); var hashes = store.FileHashesOf("App").ToDictionary();
            var manifest = Path.Combine(root, "go.mod"); File.Delete(manifest);
            if (deviceSymlink) File.CreateSymbolicLink(manifest, "/dev/zero");
            else MkFifo(manifest, Convert.ToUInt32("600", 8)).ShouldBe(0);
            var operation = Task.Run(() => pipeline.IndexRepositoryAsync("App", root, changedFilesOnly: ["go.mod"]));
            (await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(2)))).ShouldBe(operation,
                "manifest validation must never wait for a FIFO writer or read a device");
            await Should.ThrowAsync<InvalidOperationException>(() => operation);
            store.Edges.ShouldBe(edges); store.FileHashesOf("App").ShouldBe(hashes);
        }
        finally { Directory.Delete(root, true); }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);

    private static IndexingPipeline Pipeline(InMemoryGraphStore store, IndexingOptions? options = null) => new(store, [Extractor()], Options.Create(options ?? new IndexingOptions { MaxFileSizeKb = 1 }), new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance);
    private static TreeSitterExtractor Extractor() => new(NullLogger<TreeSitterExtractor>.Instance);
    private static GraphNode Handler(string path) => new() { Repository = "App", Label = NodeLabel.Function, Name = "Handle", QualifiedName = "App:" + path + ":Handle", FilePath = path };
    private static string Root() { var root = Path.Combine(Path.GetTempPath(), "codegraph-go-boundary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static void Fixture(string root)
    {
        File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/app\n");
        Directory.CreateDirectory(Path.Combine(root, "server")); File.WriteAllText(Path.Combine(root, "server/routes.go"), Routes);
        Directory.CreateDirectory(Path.Combine(root, "internal/admin")); File.WriteAllText(Path.Combine(root, "internal/admin/handler.go"), "package admin\nfunc Handle() {}\n");
        Directory.CreateDirectory(Path.Combine(root, "shadow/internal/admin")); File.WriteAllText(Path.Combine(root, "shadow/internal/admin/handler.go"), "package admin\nfunc Handle() {}\n");
    }
}
