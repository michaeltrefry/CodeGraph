using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Messaging;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class ProjectServiceTests
{
    [Fact]
    public async Task ReAnalyzeRepository_ReplacesGraphAndCoalescesConcurrentRequests()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-reanalyze-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var stalePath = Path.Combine(rootPath, "app.py");
            await File.WriteAllTextAsync(stalePath, "class FastApiBackend:\n    pass\n");
            var store = new InMemoryGraphStore();
            var options = Options.Create(new IndexingOptions
            {
                MaxParallelFiles = 1,
                MaxParallelRepos = 2
            });
            var pipeline = new IndexingPipeline(
                store,
                [new TestExtractor()],
                options,
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);
            await pipeline.IndexProjectAsync("SceneWorks", rootPath, ct: CancellationToken.None);

            File.Delete(stalePath);
            await File.WriteAllTextAsync(Path.Combine(rootPath, "main.rs"), "pub fn axum_sidecar() {}\n");

            var analysis = new RecordingBatchAnalysisService(store);
            var service = new ProjectService(
                store,
                analysis,
                new NoOpMessageBus(),
                new FixedRepoProvider(rootPath),
                pipeline,
                options,
                NullLogger<ProjectService>.Instance);

            var responses = await Task.WhenAll(
                service.ReAnalyzeRepository("SceneWorks"),
                service.ReAnalyzeRepository("SceneWorks"));

            responses.ShouldAllBe(response => response != null);
            analysis.SubmissionCount.ShouldBe(1);
            analysis.NodesAtSubmission.ShouldNotBeNull();
            analysis.NodesAtSubmission.ShouldNotContain(node => node.FilePath == "app.py");
            analysis.NodesAtSubmission.ShouldContain(node => node.FilePath == "main.rs");
            (await store.GetFileHashesAsync("SceneWorks")).ShouldNotContainKey("app.py");
            (await store.GetFileHashesAsync("SceneWorks")).ShouldContainKey("main.rs");
            (await store.GetRepositoryByName("SceneWorks"))!.Language.ShouldBe("Rust");
            (await store.GetSyncStateAsync("SceneWorks"))!.Status.ShouldBe("idle");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectIndexingLock_SerializesAcrossStoreInstances()
    {
        var firstStore = new InMemoryGraphStore();
        var secondStore = new InMemoryGraphStore();
        var firstLock = await firstStore.AcquireProjectIndexingLockAsync("SharedRepo");

        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTask = Task.Run(async () =>
        {
            await using var secondLock = await secondStore.AcquireProjectIndexingLockAsync("SharedRepo");
            secondEntered.SetResult();
        });

        try
        {
            await Task.Delay(50);
            secondEntered.Task.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            await firstLock.DisposeAsync();
        }
        await secondTask;
        secondEntered.Task.IsCompletedSuccessfully.ShouldBeTrue();
    }

    private sealed class TestExtractor : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } =
            new HashSet<string>([".py", ".rs"], StringComparer.OrdinalIgnoreCase);

        public Task<ExtractionResult> ExtractAsync(
            string filePath,
            string content,
            ExtractorContext context,
            CancellationToken ct = default)
        {
            var relativePath = Path.GetRelativePath(context.RootPath, filePath);
            var name = Path.GetFileNameWithoutExtension(filePath);
            return Task.FromResult(new ExtractionResult
            {
                Nodes =
                [
                    new GraphNode
                    {
                        Project = context.ProjectName,
                        Label = NodeLabel.Class,
                        Name = name,
                        QualifiedName = $"{context.ProjectName}.{name}",
                        FilePath = relativePath,
                        StartLine = 1,
                        EndLine = 1
                    }
                ],
                Metadata = new ProjectMetadata(
                    Path.GetExtension(filePath).Equals(".rs", StringComparison.OrdinalIgnoreCase)
                        ? "Rust"
                        : "Python",
                    null)
            });
        }
    }

    private sealed class FixedRepoProvider(string rootPath) : IRepoProvider
    {
        public Task<string> EnsureLocalAsync(
            string repoName,
            string? localPath,
            string? repoUrl,
            CancellationToken ct = default) => Task.FromResult(rootPath);

        public Task<List<DiscoveredProject>> DiscoverProjectsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<DiscoveredProject>());

        public Task<List<DiscoveredProject>> SearchProjectsAsync(
            string searchTerm,
            CancellationToken ct = default) =>
            Task.FromResult(new List<DiscoveredProject>());
    }

    private sealed class NoOpMessageBus : IMessageBus
    {
        public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class =>
            Task.CompletedTask;
    }

    private sealed class RecordingBatchAnalysisService(InMemoryGraphStore store) : IBatchAnalysisService
    {
        public int SubmissionCount { get; private set; }
        public IReadOnlyList<NodeEntity>? NodesAtSubmission { get; private set; }

        public async Task SubmitAnalysisBatchAsync(
            string repoName,
            string? repoPath = null,
            bool includeAllSource = false,
            CancellationToken ct = default)
        {
            SubmissionCount++;
            NodesAtSubmission = await store.GetAllNodesByProjectAsync(repoName);
            await store.CreateAnalysisBatchAsync(new AnalysisBatchEntity
            {
                Repo = repoName,
                ProviderBatchId = $"test-{SubmissionCount}",
                Status = "submitted",
                IncludeAllSource = includeAllSource,
                RequestCount = 1,
                SubmittedAt = DateTime.UtcNow
            });
        }

        public Task ProcessCompletedBatchesAsync(string? repo = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ProcessCompletedBatchAsync(
            string repoName,
            string providerBatchId,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task SynthesizeRepoSummaryAsync(
            string repoName,
            string batchId,
            CancellationToken ct) => Task.CompletedTask;

        public Task WriteCodeGraphDocsAsync(string repoName, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
