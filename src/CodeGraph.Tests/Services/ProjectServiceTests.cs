using CodeGraph.Data;
using CodeGraph.Extractors.CSharp;
using CodeGraph.Models;
using CodeGraph.Models.Messages;
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
    public async Task ProcessRepository_FolderProviderCarriesCanonicalIdentityIntoTrustPolicy()
    {
        var providerRoot = Path.Combine(Path.GetTempPath(), $"codegraph-provider-trust-{Guid.NewGuid():N}");
        var repoPath = Path.Combine(providerRoot, "MaliciousRestore");
        CopyFixture(repoPath);

        try
        {
            var store = new InMemoryGraphStore();
            var options = Options.Create(new IndexingOptions
            {
                MaxParallelFiles = 1,
                MaxParallelRepos = 1,
                TrustedDotnetRepositories = "folder:MaliciousRestore"
            });
            var pipeline = new IndexingPipeline(
                store,
                [new RoslynExtractor()],
                options,
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance,
                new SolutionAnalyzer(
                    NullLogger<SolutionAnalyzer>.Instance,
                    new LintResultCache(),
                    new DiagnosticDetailCache()));
            var provider = CreateFolderProvider(providerRoot);
            var service = new ProjectService(
                store,
                new RecordingBatchAnalysisService(store),
                new NoOpMessageBus(),
                provider,
                pipeline,
                options,
                NullLogger<ProjectService>.Instance);

            await service.ProcessRepository(new ProcessRepository
            {
                Name = "MaliciousRestore",
                RepoUrl = repoPath,
                ShouldIndex = true,
                ShouldAnalyze = false,
                SkipIfUpToDate = false
            });

            File.Exists(Path.Combine(repoPath, "restore-payload-executed.txt")).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(providerRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessRepository_RejectsSourceGroupThatDisagreesWithResolvedCheckout()
    {
        var providerRoot = Path.Combine(Path.GetTempPath(), $"codegraph-provider-mismatch-{Guid.NewGuid():N}");
        var repoPath = Path.Combine(providerRoot, "attacker", "MaliciousRestore");
        CopyFixture(repoPath);

        try
        {
            var store = new InMemoryGraphStore();
            var options = Options.Create(new IndexingOptions
            {
                MaxParallelFiles = 1,
                MaxParallelRepos = 1,
                TrustedDotnetRepositories = "folder:trusted/MaliciousRestore"
            });
            var pipeline = new IndexingPipeline(
                store,
                [new RoslynExtractor()],
                options,
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance,
                new SolutionAnalyzer(
                    NullLogger<SolutionAnalyzer>.Instance,
                    new LintResultCache(),
                    new DiagnosticDetailCache()));
            var service = new ProjectService(
                store,
                new RecordingBatchAnalysisService(store),
                new NoOpMessageBus(),
                CreateFolderProvider(providerRoot),
                pipeline,
                options,
                NullLogger<ProjectService>.Instance);

            var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
                service.ProcessRepository(new ProcessRepository
                {
                    Name = "MaliciousRestore",
                    RepoUrl = repoPath,
                    SourceGroup = "trusted",
                    ShouldIndex = true
                }));

            exception.Message.ShouldContain("inconsistent with provider-resolved identity");
            File.Exists(Path.Combine(repoPath, "restore-payload-executed.txt")).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(providerRoot, recursive: true);
        }
    }

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

    [Fact]
    public async Task ProcessRepository_SuspendsRoutineSyncOfACommitAfterRepeatedFailures()
    {
        var rootPath = CreateGitRepository();
        try
        {
            var store = new InMemoryGraphStore
            {
                IncrementalReplacementFailure = new TimeoutException("simulated graph write timeout")
            };
            var extractor = new CountingExtractor();
            var service = CreateService(store, extractor, rootPath, maxConsecutiveFailures: 2);
            var firstSha = GitHead(rootPath);
            var routine = new ProcessRepository
            {
                Name = "inference", RepoUrl = rootPath, ShouldIndex = true, SkipIfUpToDate = true
            };

            await Should.ThrowAsync<TimeoutException>(() => service.ProcessRepository(routine));
            var state = (await store.GetSyncStateAsync("inference"))!;
            state.Status.ShouldBe("error");
            state.FailedCommitSha.ShouldBe(firstSha);
            state.ConsecutiveFailures.ShouldBe(1);
            state.LastFailureAt.ShouldNotBeNull();
            state.ErrorMessage!.ShouldStartWith("[graph-write] System.TimeoutException: simulated graph write timeout");
            state.LastCommitSha.ShouldBeNull();

            await Should.ThrowAsync<TimeoutException>(() => service.ProcessRepository(routine));
            (await store.GetSyncStateAsync("inference"))!.ConsecutiveFailures.ShouldBe(2);
            extractor.Runs.ShouldBe(2);

            // Limit reached: routine syncs of the same commit no longer run the pipeline.
            await service.ProcessRepository(routine);
            extractor.Runs.ShouldBe(2);

            // A manual reindex still runs (and keeps counting while it fails).
            await Should.ThrowAsync<TimeoutException>(() => service.ProcessRepository(new ProcessRepository
            {
                Name = "inference", RepoUrl = rootPath, ShouldIndex = true, SkipIfUpToDate = false
            }));
            extractor.Runs.ShouldBe(3);
            (await store.GetSyncStateAsync("inference"))!.ConsecutiveFailures.ShouldBe(3);

            // A new commit is retried automatically, and success clears the failure record.
            store.IncrementalReplacementFailure = null;
            await File.WriteAllTextAsync(Path.Combine(rootPath, "second.py"), "class Second:\n    pass\n");
            Git(rootPath, "add", ".");
            Git(rootPath, "commit", "-q", "-m", "second");
            var secondSha = GitHead(rootPath);

            await service.ProcessRepository(routine);
            extractor.Runs.ShouldBeGreaterThan(3);
            state = (await store.GetSyncStateAsync("inference"))!;
            state.Status.ShouldBe("idle");
            state.LastCommitSha.ShouldBe(secondSha);
            state.FailedCommitSha.ShouldBeNull();
            state.ConsecutiveFailures.ShouldBe(0);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task ProcessRepository_RoutineSyncAtUnchangedHeadReindexesOnceAfterExtractionVersionBump()
    {
        var rootPath = CreateGitRepository();
        try
        {
            var store = new InMemoryGraphStore();
            var bus = new RecordingMessageBus();
            var extractor = new CountingExtractor();
            var routine = new ProcessRepository
            {
                Name = "inference",
                RepoUrl = rootPath,
                ShouldIndex = true,
                ShouldAnalyze = true,
                SkipIfUpToDate = true
            };

            await CreateService(store, extractor, rootPath, maxConsecutiveFailures: 3, bus)
                .ProcessRepository(routine);
            extractor.Runs.ShouldBe(1);
            var first = bus.Messages.OfType<RepositoryIndexingCompleted>().ShouldHaveSingleItem();
            first.ShouldAnalyze.ShouldBeTrue();
            first.ShouldComputeVitals.ShouldBeTrue();

            // Same HEAD, same extractor versions: the routine sync is skipped.
            await CreateService(store, extractor, rootPath, maxConsecutiveFailures: 3, bus)
                .ProcessRepository(routine);
            extractor.Runs.ShouldBe(1);
            bus.Messages.Count.ShouldBe(1);

            // An extractor upgrade is deployed: the unchanged HEAD is re-indexed without
            // repeating the commit's LLM analysis or health analysis.
            extractor.Version = "2";
            var upgraded = CreateService(store, extractor, rootPath, maxConsecutiveFailures: 3, bus);
            await upgraded.ProcessRepository(routine);
            extractor.Runs.ShouldBe(2);
            var refresh = bus.Messages.OfType<RepositoryIndexingCompleted>().Last();
            bus.Messages.Count.ShouldBe(2);
            refresh.ShouldAnalyze.ShouldBeFalse();
            refresh.ShouldComputeVitals.ShouldBeFalse();
            var state = (await store.GetSyncStateAsync("inference"))!;
            state.LastCommitSha.ShouldBe(GitHead(rootPath));
            state.ExtractionFingerprint.ShouldNotBeNull();

            // The refresh is recorded: later routine syncs skip again.
            await upgraded.ProcessRepository(routine);
            extractor.Runs.ShouldBe(2);
            bus.Messages.Count.ShouldBe(2);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task ProcessRepository_HostShutdownCancellationIsNotCountedAsACommitFailure()
    {
        var rootPath = CreateGitRepository();
        try
        {
            var store = new InMemoryGraphStore();
            var lifetime = new TestHostLifetime();
            using var cts = new CancellationTokenSource();
            // Graceful shutdown signals ApplicationStopping before it cancels running work.
            var extractor = new CountingExtractor { OnExtract = () => { lifetime.StopApplication(); cts.Cancel(); } };
            var service = CreateService(store, extractor, rootPath, maxConsecutiveFailures: 1, hostLifetime: lifetime);

            await Should.ThrowAsync<OperationCanceledException>(() => service.ProcessRepository(
                new ProcessRepository
                {
                    Name = "inference", RepoUrl = rootPath, ShouldIndex = true, SkipIfUpToDate = true
                },
                cts.Token));

            var state = await store.GetSyncStateAsync("inference");
            (state?.ConsecutiveFailures ?? 0).ShouldBe(0);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    /// <summary>
    /// sc-25698: a run cancelled for any reason other than host shutdown (lost durable-run lease,
    /// cancel request, formerly the broker's consumer_timeout) is a failure of the commit, so a
    /// repository whose run keeps being cancelled is suspended instead of looping forever.
    /// </summary>
    [Fact]
    public async Task ProcessRepository_NonShutdownCancellationCountsAndSuspendsAfterTheLimit()
    {
        var rootPath = CreateGitRepository();
        try
        {
            var store = new InMemoryGraphStore();
            var lifetime = new TestHostLifetime();
            CancellationTokenSource? cts = null;
            var extractor = new CountingExtractor { OnExtract = () => cts!.Cancel() };
            var service = CreateService(store, extractor, rootPath, maxConsecutiveFailures: 2, hostLifetime: lifetime);
            var routine = new ProcessRepository
            {
                Name = "inference", RepoUrl = rootPath, ShouldIndex = true, SkipIfUpToDate = true
            };

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                using (cts = new CancellationTokenSource())
                    await Should.ThrowAsync<OperationCanceledException>(() => service.ProcessRepository(routine, cts.Token));
                var state = (await store.GetSyncStateAsync("inference")).ShouldNotBeNull();
                state.ConsecutiveFailures.ShouldBe(attempt);
                state.ErrorMessage!.ShouldContain("canceled");
            }

            extractor.Runs.ShouldBe(2);
            // Limit reached: the next routine sync of the same commit skips instead of re-indexing.
            await service.ProcessRepository(routine);
            extractor.Runs.ShouldBe(2);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    private sealed class TestHostLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    private static ProjectService CreateService(
        InMemoryGraphStore store, ICodeExtractor extractor, string rootPath, int maxConsecutiveFailures,
        IMessageBus? messageBus = null, Microsoft.Extensions.Hosting.IHostApplicationLifetime? hostLifetime = null)
    {
        var options = Options.Create(new IndexingOptions
        {
            MaxParallelFiles = 1,
            MaxParallelRepos = 1,
            MaxConsecutiveIndexFailures = maxConsecutiveFailures
        });
        return new ProjectService(
            store,
            new RecordingBatchAnalysisService(store),
            messageBus ?? new NoOpMessageBus(),
            new FixedRepoProvider(rootPath),
            new IndexingPipeline(store, [extractor], options, new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance),
            options,
            NullLogger<ProjectService>.Instance,
            hostLifetime);
    }

    private static string CreateGitRepository()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"codegraph-index-failures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        File.WriteAllText(Path.Combine(rootPath, "first.py"), "class First:\n    pass\n");
        Git(rootPath, "init", "-q");
        Git(rootPath, "add", ".");
        Git(rootPath, "commit", "-q", "-m", "first");
        return rootPath;
    }

    private static string GitHead(string rootPath) => Git(rootPath, "rev-parse", "HEAD").Trim();

    private static string Git(string rootPath, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in new[] { "-c", "user.name=test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false" }.Concat(args))
            psi.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output;
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private sealed class CountingExtractor : ICodeExtractor
    {
        private int _runs;
        public int Runs => _runs;
        public Action? OnExtract { get; init; }
        public string Version { get; set; } = "1";

        public string GetExtractionVersion(string extension) => Version;

        public IReadOnlySet<string> SupportedExtensions { get; } =
            new HashSet<string>([".py"], StringComparer.OrdinalIgnoreCase);

        public Task<ExtractionResult> ExtractAsync(
            string filePath, string content, ExtractorContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _runs);
            OnExtract?.Invoke();
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
                        FilePath = Path.GetRelativePath(context.RootPath, filePath),
                        StartLine = 1,
                        EndLine = 1
                    }
                ]
            });
        }
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

    private static FolderRepoProvider CreateFolderProvider(string providerRoot) => new(
        Options.Create(new RepositorySourceOptions
        {
            Provider = RepositorySourceProvider.Folder,
            Folder = new FolderSourceOptions { RootPath = providerRoot }
        }),
        new NoOpExclusionService(),
        NullLogger<FolderRepoProvider>.Instance);

    private static void CopyFixture(string destination)
    {
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(Path.Combine(destination, ".git"));
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MaliciousRestore");
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }

    private sealed class FixedRepoProvider(string rootPath) : IRepoProvider
    {
        public Task<ResolvedRepository> ResolveRepositoryAsync(
            string repoName,
            string? localPath,
            string? repoUrl,
            CancellationToken ct = default) =>
            Task.FromResult(new ResolvedRepository(rootPath, $"folder:{repoName}", rootPath, null));

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

    private sealed class RecordingMessageBus : IMessageBus
    {
        public List<object> Messages { get; } = [];

        public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpMessageBus : IMessageBus
    {
        public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class =>
            Task.CompletedTask;
    }

    private sealed class NoOpExclusionService : IExclusionService
    {
        public Task<string?> GetExclusionTypeAsync(string repoName, string? sourceGroup) =>
            Task.FromResult<string?>(null);

        public Task<HashSet<string>> GetSecretFilePathsAsync(string project) =>
            Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public Task<IReadOnlyList<ExclusionRuleEntity>> ListRulesAsync() =>
            Task.FromResult<IReadOnlyList<ExclusionRuleEntity>>([]);

        public Task<ExclusionRuleEntity> CreateRuleAsync(
            string targetType, string targetValue, string exclusionType, string? reason, string createdBy) =>
            throw new NotSupportedException();

        public Task<ExclusionRuleEntity?> UpdateRuleAsync(long id, string exclusionType, string? reason) =>
            throw new NotSupportedException();

        public Task<bool> DeleteRuleAsync(long id) => throw new NotSupportedException();

        public Task SeedFromConfigAsync(IReadOnlyList<string> excludedGroups) => Task.CompletedTask;
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
