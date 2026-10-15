using CodeGraph.Data;
using CodeGraph.Extractors.CSharp;
using CodeGraph.Host.Shared.Consumers;
using CodeGraph.Models;
using CodeGraph.Models.Messages;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Messaging;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
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
            var bus = new RecordingMessageBus();
            var service = new ProjectService(
                store,
                analysis,
                bus,
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

            // The batch was submitted above, so the post-index step must not submit another one,
            // but it still runs vitals and the model-backed health analysis.
            var completed = bus.Messages.OfType<RepositoryIndexingCompleted>().ShouldHaveSingleItem();
            completed.ShouldAnalyze.ShouldBeFalse();
            completed.ShouldComputeVitals.ShouldBeTrue();
            completed.ShouldAnalyzeHealth.ShouldBeTrue();
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
            first.ShouldAnalyzeHealth.ShouldBeFalse();
            // The post-index consumer records the vitals version it computed with.
            await store.SetVitalsVersionAsync("inference", VitalsAnalyzer.Version);

            // Same HEAD, same extractor and vitals versions: the routine sync is skipped.
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
            refresh.ShouldAnalyzeHealth.ShouldBeFalse();
            var state = (await store.GetSyncStateAsync("inference"))!;
            state.LastCommitSha.ShouldBe(GitHead(rootPath));
            state.ExtractionFingerprint.ShouldNotBeNull();

            // The refresh is recorded: later routine syncs skip again.
            await upgraded.ProcessRepository(routine);
            extractor.Runs.ShouldBe(2);
            bus.Messages.Count.ShouldBe(2);

            // An extractor upgrade lands together with a vitals upgrade: the extraction refresh
            // also recomputes vitals (still without analysis or health analysis).
            await store.SetVitalsVersionAsync("inference", VitalsAnalyzer.Version - 1);
            extractor.Version = "3";
            await CreateService(store, extractor, rootPath, maxConsecutiveFailures: 3, bus).ProcessRepository(routine);
            extractor.Runs.ShouldBe(3);
            var combined = bus.Messages.OfType<RepositoryIndexingCompleted>().Last();
            bus.Messages.Count.ShouldBe(3);
            combined.ShouldComputeVitals.ShouldBeTrue();
            combined.VitalsRefreshOnly.ShouldBeFalse();
            combined.ShouldAnalyze.ShouldBeFalse();
            combined.ShouldAnalyzeHealth.ShouldBeFalse();
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    /// <summary>
    /// sc-26138: vitals coverage grew (Swift/Rust) but repositories whose HEAD did not move kept
    /// their old vitals, because nothing recomputed vitals at an unchanged HEAD. A routine sync now
    /// recomputes vitals only (no extraction, no analysis) when the stored vitals version is old;
    /// through the post-index consumer the Swift file gets metrics and the version is recorded.
    /// </summary>
    [Fact]
    public async Task ProcessRepository_RoutineSyncAtUnchangedHeadRecomputesOnlyVitalsAfterVitalsVersionBump()
    {
        var rootPath = CreateGitRepository();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "App.swift"),
                "struct App {\n    func run(_ x: Int) -> Int {\n        if x > 0 { return x }\n        return 0\n    }\n}\n");
            Git(rootPath, "add", ".");
            Git(rootPath, "commit", "-q", "-m", "swift");

            var store = new InMemoryGraphStore();
            var bus = new RecordingMessageBus();
            var extractor = new CountingExtractor();
            var analysis = new RecordingBatchAnalysisService(store);
            var routine = new ProcessRepository
            {
                Name = "inference",
                RepoUrl = rootPath,
                ShouldIndex = true,
                ShouldAnalyze = true,
                SkipIfUpToDate = true
            };

            await CreateService(store, extractor, rootPath, maxConsecutiveFailures: 3, bus, analysis: analysis)
                .ProcessRepository(routine);
            extractor.Runs.ShouldBe(1);
            // Vitals were last computed by an analyzer without Swift coverage.
            await store.SetVitalsVersionAsync("inference", VitalsAnalyzer.Version - 1);

            await CreateService(store, extractor, rootPath, maxConsecutiveFailures: 3, bus, analysis: analysis)
                .ProcessRepository(routine);
            extractor.Runs.ShouldBe(1);
            analysis.SubmissionCount.ShouldBe(0);
            bus.Messages.Count.ShouldBe(2);
            var refresh = bus.Messages[1].ShouldBeOfType<RepositoryIndexingCompleted>();
            refresh.VitalsRefreshOnly.ShouldBeTrue();
            refresh.ShouldComputeVitals.ShouldBeTrue();
            refresh.ShouldAnalyze.ShouldBeFalse();
            refresh.ShouldAnalyzeHealth.ShouldBeFalse();
            refresh.CommitSha.ShouldBe(GitHead(rootPath));

            // The post-index consumer computes vitals with the real analyzer and records the version,
            // without linking, community detection, model-backed health analysis or a batch submission.
            var counters = await RunPostIndexConsumerAsync(store, refresh, analysis, () => new VitalsAnalyzer(
                store,
                new ThrowingAnalysisProviderRegistry(),
                Options.Create(new AnalysisOptions()),
                new LocalFileSystem(),
                new NoLintRunner(),
                new LintResultCache(),
                new DiagnosticDetailCache(),
                NullLogger<VitalsAnalyzer>.Instance));

            counters.LinkerResolutions.ShouldBe(0);
            counters.CommunityDetections.ShouldBe(0);
            analysis.SubmissionCount.ShouldBe(0);
            (await store.GetFileMetricsAsync("inference")).Single(m => m.FilePath == "App.swift")
                .FunctionCount.ShouldBeGreaterThan(0);
            (await store.GetSyncStateAsync("inference"))!.VitalsVersion.ShouldBe(VitalsAnalyzer.Version);

            // Same vitals version: routine syncs skip again.
            await CreateService(store, extractor, rootPath, maxConsecutiveFailures: 3, bus, analysis: analysis)
                .ProcessRepository(routine);
            extractor.Runs.ShouldBe(1);
            bus.Messages.Count.ShouldBe(2);
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    /// <summary>
    /// sc-26138: a vitals computation that keeps failing must neither drift the stored health (the
    /// security blend compounded over the previous, already blended summaries on every retry) nor
    /// be retried by every routine sync: the vitals version is recorded only after success, and
    /// vitals-only refreshes stop at the failure limit until HEAD changes.
    /// </summary>
    [Fact]
    public async Task ProcessRepository_FailingVitalsRefreshKeepsHealthAndStopsAtTheFailureLimit()
    {
        var rootPath = CreateGitRepository();
        try
        {
            var store = new InMemoryGraphStore();
            var bus = new RecordingMessageBus();
            var extractor = new CountingExtractor();
            var analysis = new RecordingBatchAnalysisService(store);
            var routine = new ProcessRepository
            {
                Name = "inference", RepoUrl = rootPath, ShouldIndex = true, SkipIfUpToDate = true
            };
            ProjectService Service() => CreateService(store, extractor, rootPath, maxConsecutiveFailures: 2, bus, analysis: analysis);

            await Service().ProcessRepository(routine);
            await store.SetVitalsVersionAsync("inference", VitalsAnalyzer.Version - 1);
            await store.UpsertProjectHealthSummaryAsync(new ProjectHealthSummaryEntity
            {
                Project = "inference", OverallHealth = 6.0, ComputedAt = DateTime.UtcNow.AddDays(-1)
            });

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                await Service().ProcessRepository(routine);
                var refresh = bus.Messages.Last().ShouldBeOfType<RepositoryIndexingCompleted>();
                refresh.VitalsRefreshOnly.ShouldBeTrue();
                await RunPostIndexConsumerAsync(store, refresh, analysis, () => new FailingVitalsAnalyzer(), maxConsecutiveFailures: 2);

                var state = (await store.GetSyncStateAsync("inference"))!;
                state.VitalsVersion.ShouldBe(VitalsAnalyzer.Version - 1);
                state.VitalsConsecutiveFailures.ShouldBe(attempt);
                (await store.GetProjectHealthSummariesAsync("inference")).ShouldHaveSingleItem()
                    .OverallHealth.ShouldBe(6.0);
            }

            bus.Messages.Count.ShouldBe(3);
            // Limit reached: routine syncs of the same commit stop publishing vitals-only refreshes.
            await Service().ProcessRepository(routine);
            bus.Messages.Count.ShouldBe(3);
            extractor.Runs.ShouldBe(1);

            // A new commit is indexed and computes vitals again.
            await File.WriteAllTextAsync(Path.Combine(rootPath, "second.py"), "class Second:\n    pass\n");
            Git(rootPath, "add", ".");
            Git(rootPath, "commit", "-q", "-m", "second");
            await Service().ProcessRepository(routine);
            bus.Messages.Count.ShouldBe(4);
            var reindex = bus.Messages.Last().ShouldBeOfType<RepositoryIndexingCompleted>();
            reindex.ShouldComputeVitals.ShouldBeTrue();
            reindex.VitalsRefreshOnly.ShouldBeFalse();
            reindex.CommitSha.ShouldBe(GitHead(rootPath));
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    /// <summary>
    /// The security score is blended only into health summaries this run's vitals computation
    /// wrote: a failed vitals run, or a summary vitals no longer write, keeps its stored health.
    /// </summary>
    [Theory]
    [InlineData(false, "fail")]
    [InlineData(true, "fail")]
    [InlineData(false, "partial")]
    [InlineData(true, "partial")]
    [InlineData(false, "succeed")]
    [InlineData(true, "succeed")]
    public async Task PostIndexConsumer_BlendsSecurityOnlyIntoSummariesWrittenByThisRun(bool vitalsRefreshOnly, string vitals)
    {
        var store = new InMemoryGraphStore();
        await store.UpsertSyncStateAsync(new SyncStateEntity { Project = "inference", LastCommitSha = "abc", Status = "idle" });
        await store.UpsertProjectHealthSummaryAsync(new ProjectHealthSummaryEntity
        {
            Project = "inference", DotnetProject = "Old", OverallHealth = 6.0, ComputedAt = DateTime.UtcNow.AddDays(-1)
        });
        var message = new RepositoryIndexingCompleted
        {
            Name = "inference", RepoPath = "/repos/inference", CommitSha = "abc", ShouldComputeVitals = true,
            VitalsRefreshOnly = vitalsRefreshOnly
        };

        var counters = await RunPostIndexConsumerAsync(store, message, new RecordingBatchAnalysisService(store),
            () => vitals switch
            {
                "succeed" => new SummaryWritingVitalsAnalyzer(store, fail: false),
                // Wrote a fresh summary, then failed (as a computation failing part-way through).
                "partial" => new SummaryWritingVitalsAnalyzer(store, fail: true),
                _ => new FailingVitalsAnalyzer()
            });

        var summaries = await store.GetProjectHealthSummariesAsync("inference");
        summaries.Single(summary => summary.DotnetProject == "Old").OverallHealth.ShouldBe(6.0);
        var state = (await store.GetSyncStateAsync("inference"))!;
        if (vitals == "succeed")
        {
            // 0.8 * 4.0 + 0.2 * 10 (CountingSecurityAnalyzer) = 5.2, blended exactly once.
            summaries.Single(summary => summary.DotnetProject is null).OverallHealth.ShouldBe(5.2);
            state.VitalsVersion.ShouldBe(VitalsAnalyzer.Version);
            state.VitalsConsecutiveFailures.ShouldBe(0);
            counters.SecurityScans.ShouldBe(1);
        }
        else
        {
            if (vitals == "partial")
                summaries.Single(summary => summary.DotnetProject is null).OverallHealth.ShouldBe(4.0);
            else
                summaries.Count.ShouldBe(1);
            state.VitalsVersion.ShouldBeNull();
            state.VitalsConsecutiveFailures.ShouldBe(1);
            state.VitalsFailedCommitSha.ShouldBe("abc");
            // A failed vitals-only refresh has nothing to re-blend; an index run still scans.
            counters.SecurityScans.ShouldBe(vitalsRefreshOnly ? 0 : 1);
        }
    }

    /// <summary>
    /// The vitals failure limit applies only to the failing commit at the failing vitals version:
    /// a record for another commit or an older vitals version does not stop a vitals refresh.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ProcessRepository_VitalsFailureLimitIsPerCommitAndVitalsVersion(bool otherCommit, bool olderVersion)
    {
        var rootPath = CreateGitRepository();
        try
        {
            var store = new InMemoryGraphStore();
            var bus = new RecordingMessageBus();
            var extractor = new CountingExtractor();
            var routine = new ProcessRepository
            {
                Name = "inference", RepoUrl = rootPath, ShouldIndex = true, SkipIfUpToDate = true
            };
            await CreateService(store, extractor, rootPath, maxConsecutiveFailures: 2, bus).ProcessRepository(routine);
            var failedSha = otherCommit ? new string('0', 40) : GitHead(rootPath);
            var failedVersion = olderVersion ? VitalsAnalyzer.Version - 1 : VitalsAnalyzer.Version;
            for (var i = 0; i < 2; i++)
                await store.RecordVitalsFailureAsync("inference", failedSha, failedVersion);

            await CreateService(store, extractor, rootPath, maxConsecutiveFailures: 2, bus).ProcessRepository(routine);

            bus.Messages.Count.ShouldBe(2);
            bus.Messages[1].ShouldBeOfType<RepositoryIndexingCompleted>().VitalsRefreshOnly.ShouldBeTrue();
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    private sealed record PostIndexCounters(int LinkerResolutions, int CommunityDetections, int SecurityScans);

    /// <summary>
    /// Runs the post-index consumer on <paramref name="message"/> with community detection enabled,
    /// counting cross-repo linker resolutions and community detections (both throw).
    /// </summary>
    private static async Task<PostIndexCounters> RunPostIndexConsumerAsync(
        InMemoryGraphStore store,
        RepositoryIndexingCompleted message,
        IBatchAnalysisService analysis,
        Func<IVitalsAnalyzer> vitals,
        int maxConsecutiveFailures = 3)
    {
        var linkerResolutions = 0;
        var communities = new CountingCommunityDetection();
        var security = new CountingSecurityAnalyzer();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .Configure<IndexingOptions>(options =>
            {
                options.DetectCommunitiesAfterIndexing = true;
                options.MaxConsecutiveIndexFailures = maxConsecutiveFailures;
            })
            .AddSingleton<IGraphStore>(store)
            .AddTransient<CrossRepoLinker>(_ =>
            {
                Interlocked.Increment(ref linkerResolutions);
                throw new InvalidOperationException("linking must not run for a vitals-only refresh");
            })
            .AddSingleton<ICommunityDetectionService>(communities)
            .AddScoped(_ => vitals())
            .AddSingleton<ISecurityAnalyzer>(security)
            .AddSingleton(analysis)
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<RepositoryIndexingCompletedConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(message);
            (await harness.GetConsumerHarness<RepositoryIndexingCompletedConsumer>().Consumed
                .Any<RepositoryIndexingCompleted>()).ShouldBeTrue();
        }
        finally
        {
            await harness.Stop();
        }

        return new PostIndexCounters(linkerResolutions, communities.Calls, security.Scans);
    }

    private sealed class CountingCommunityDetection : ICommunityDetectionService
    {
        private int _calls;
        public int Calls => _calls;

        public Task DetectCommunitiesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("community detection must not run for a vitals-only refresh");
        }

        public Task<ClusterOverviewResponse> GetClusterOverviewAsync() => throw new NotSupportedException();
        public Task<ClusterDetailResponse?> GetClusterDetailAsync(int clusterId) => throw new NotSupportedException();
        public Task<ClusterGraphResponse> GetClusterGraphAsync() => throw new NotSupportedException();
    }

    private sealed class FailingVitalsAnalyzer : IVitalsAnalyzer
    {
        public Task ComputeMetricsAsync(string projectName, string repoPath, CancellationToken ct = default) =>
            throw new IOException("simulated: git history could not be read");

        public Task AnalyzeHealthAsync(string projectName, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Writes one fresh repository-level summary (health 4.0), like a real vitals run.</summary>
    private sealed class SummaryWritingVitalsAnalyzer(IGraphStore store, bool fail) : IVitalsAnalyzer
    {
        public async Task ComputeMetricsAsync(string projectName, string repoPath, CancellationToken ct = default)
        {
            await store.UpsertProjectHealthSummaryAsync(new ProjectHealthSummaryEntity
            {
                Project = projectName, OverallHealth = 4.0, ComputedAt = DateTime.UtcNow
            });
            if (fail)
                throw new IOException("simulated: failed after writing the repository summary");
        }

        public Task AnalyzeHealthAsync(string projectName, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A sync that does not want vitals never triggers a vitals-only refresh.</summary>
    [Fact]
    public async Task ProcessRepository_StaleVitalsVersionIsIgnoredWhenVitalsAreNotRequested()
    {
        var rootPath = CreateGitRepository();
        try
        {
            var store = new InMemoryGraphStore();
            var bus = new RecordingMessageBus();
            var extractor = new CountingExtractor();
            var routine = new ProcessRepository
            {
                Name = "inference", RepoUrl = rootPath, ShouldIndex = true, SkipIfUpToDate = true,
                ShouldComputeVitals = false
            };

            await CreateService(store, extractor, rootPath, maxConsecutiveFailures: 3, bus).ProcessRepository(routine);
            await CreateService(store, extractor, rootPath, maxConsecutiveFailures: 3, bus).ProcessRepository(routine);

            extractor.Runs.ShouldBe(1);
            bus.Messages.Count.ShouldBe(1);
            (await store.GetSyncStateAsync("inference"))!.VitalsVersion.ShouldBeNull();
        }
        finally
        {
            DeleteDirectory(rootPath);
        }
    }

    private sealed class ThrowingAnalysisProviderRegistry : IAnalysisProviderRegistry
    {
        public IAnalysisModelProvider GetProvider(string? providerName = null) =>
            throw new InvalidOperationException("a vitals-only refresh must not call the analysis model");
    }

    private sealed class NoLintRunner : ILintRunner
    {
        public Task<IReadOnlyDictionary<string, LintResult>> LintProjectAsync(string repoPath, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, LintResult>>(new Dictionary<string, LintResult>());
    }

    private sealed class CountingSecurityAnalyzer : ISecurityAnalyzer
    {
        private int _scans;
        public int Scans => _scans;

        public Task<SecurityScanResult> ScanAsync(string projectName, string repoPath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _scans);
            return Task.FromResult(new SecurityScanResult(10, 0, 0, 0, 0, []));
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
        IMessageBus? messageBus = null, Microsoft.Extensions.Hosting.IHostApplicationLifetime? hostLifetime = null,
        IBatchAnalysisService? analysis = null)
    {
        var options = Options.Create(new IndexingOptions
        {
            MaxParallelFiles = 1,
            MaxParallelRepos = 1,
            MaxConsecutiveIndexFailures = maxConsecutiveFailures
        });
        return new ProjectService(
            store,
            analysis ?? new RecordingBatchAnalysisService(store),
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
