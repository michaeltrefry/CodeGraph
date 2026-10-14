using System.Collections.Concurrent;
using CodeGraph.Data;
using CodeGraph.Host.Shared.Consumers;
using CodeGraph.Models;
using CodeGraph.Models.Messages;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeGraph.Tests.HostShared;

/// <summary>sc-25698 post-index consumer faults seen in production.</summary>
public class PostIndexConsumerTests
{
    private const string Repo = "codeflow-cli";

    /// <summary>
    /// A step that fails while its DbContext has an operation in flight (production: an
    /// OutOfMemoryException mid-query) leaves that context unusable. With every step sharing the
    /// message scope's context, the security scan and analysis submission then failed with
    /// "A second operation was started on this context instance".
    /// </summary>
    [Fact]
    public async Task RepositoryIndexingCompleted_StepFailureDoesNotPoisonLaterSteps()
    {
        var failures = new ConcurrentQueue<string>();
        var submitted = new ConcurrentQueue<string>();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .Configure<IndexingOptions>(_ => { })
            .AddScoped<IGraphStore>(_ =>
            {
                var store = new PoisonableGraphStore(failures);
                store.AddNode(new GraphNode
                {
                    Project = Repo, Label = NodeLabel.Class, Name = "Cli", QualifiedName = "Cli"
                });
                return store;
            })
            .AddTransient<CrossRepoLinker>()
            .AddScoped<IVitalsAnalyzer, ContextPoisoningVitalsAnalyzer>()
            .AddScoped<ISecurityAnalyzer, CleanSecurityAnalyzer>()
            .AddSingleton(submitted)
            .AddScoped<IBatchAnalysisService, RecordingBatchService>()
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<RepositoryIndexingCompletedConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new RepositoryIndexingCompleted
            {
                Name = Repo, RepoPath = "/repos/codeflow-cli", ShouldComputeVitals = true, ShouldAnalyze = true
            });
            (await harness.GetConsumerHarness<RepositoryIndexingCompletedConsumer>().Consumed
                .Any<RepositoryIndexingCompleted>()).ShouldBeTrue();
        }
        finally
        {
            await harness.Stop();
        }

        failures.ShouldBeEmpty();
        submitted.ShouldBe([Repo]);
    }

    /// <summary>
    /// Production RabbitMQ has no delayed-message exchange plugin, so rescheduling the check with
    /// SchedulePublish faulted (406 unknown exchange type 'x-delayed-message'). A still-running
    /// batch is left 'submitted' for the periodic ProcessBatchAnalysis sweep instead.
    /// </summary>
    [Fact]
    public async Task AnalysisBatchSubmitted_StillRunningBatch_CompletesWithoutSchedulingAnotherCheck()
    {
        var store = new InMemoryGraphStore();
        await store.CreateAnalysisBatchAsync(new AnalysisBatchEntity
        {
            Repo = Repo, ProviderBatchId = "batch-1", ProviderName = "anthropic", Status = "submitted",
            RequestCount = 1, SubmittedAt = DateTime.UtcNow
        });
        await using var provider = new ServiceCollection()
            .AddSingleton<IGraphStore>(store)
            .AddSingleton(new ConcurrentQueue<string>())
            .AddSingleton<IBatchAnalysisService, RecordingBatchService>()
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<AnalysisBatchSubmittedConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new AnalysisBatchSubmitted { RepoName = Repo, ProviderBatchId = "batch-1", RequestCount = 1 });

            var consumed = harness.GetConsumerHarness<AnalysisBatchSubmittedConsumer>().Consumed;
            (await consumed.Any<AnalysisBatchSubmitted>()).ShouldBeTrue();
            consumed.Select<AnalysisBatchSubmitted>().ShouldAllBe(context => context.Exception == null);
            (await harness.Published.SelectAsync<AnalysisBatchSubmitted>().Count()).ShouldBe(1);
            (await harness.Sent.Any<AnalysisBatchSubmitted>()).ShouldBeFalse();
            (await harness.Published.Any<Fault<AnalysisBatchSubmitted>>()).ShouldBeFalse();
        }
        finally
        {
            await harness.Stop();
        }

        (await store.GetPendingBatchesAsync(Repo)).ShouldHaveSingleItem().ProviderBatchId.ShouldBe("batch-1");
    }

    /// <summary>
    /// Indexing with shouldAnalyze=false still computes vitals metrics and the security scan, but
    /// never runs a model-backed step (health analysis or batch submission).
    /// </summary>
    [Theory]
    [InlineData(false, new[] { "metrics" }, new string[0])]
    [InlineData(true, new[] { "metrics", "health" }, new[] { Repo })]
    public async Task RepositoryIndexingCompleted_ModelBackedStepsFollowShouldAnalyze(
        bool shouldAnalyze, string[] expectedVitalsCalls, string[] expectedSubmissions)
    {
        var vitalsCalls = new ConcurrentQueue<string>();
        var submitted = new ConcurrentQueue<string>();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .Configure<IndexingOptions>(_ => { })
            .AddScoped<IGraphStore>(_ =>
            {
                var store = new InMemoryGraphStore();
                store.AddNode(new GraphNode
                {
                    Project = Repo, Label = NodeLabel.Class, Name = "Cli", QualifiedName = "Cli"
                });
                return store;
            })
            .AddTransient<CrossRepoLinker>()
            .AddScoped<IVitalsAnalyzer>(_ => new RecordingVitalsAnalyzer(vitalsCalls))
            .AddScoped<ISecurityAnalyzer, CleanSecurityAnalyzer>()
            .AddSingleton(submitted)
            .AddScoped<IBatchAnalysisService, RecordingBatchService>()
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<RepositoryIndexingCompletedConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new RepositoryIndexingCompleted
            {
                Name = Repo, RepoPath = "/repos/codeflow-cli", ShouldComputeVitals = true, ShouldAnalyze = shouldAnalyze
            });
            (await harness.GetConsumerHarness<RepositoryIndexingCompletedConsumer>().Consumed
                .Any<RepositoryIndexingCompleted>()).ShouldBeTrue();
        }
        finally
        {
            await harness.Stop();
        }

        vitalsCalls.ShouldBe(expectedVitalsCalls);
        submitted.ShouldBe(expectedSubmissions);
    }

    private sealed class RecordingVitalsAnalyzer(ConcurrentQueue<string> calls) : IVitalsAnalyzer
    {
        public Task ComputeMetricsAsync(string projectName, string repoPath, CancellationToken ct = default)
        {
            calls.Enqueue("metrics");
            return Task.CompletedTask;
        }

        public Task AnalyzeHealthAsync(string projectName, CancellationToken ct = default)
        {
            calls.Enqueue("health");
            return Task.CompletedTask;
        }
    }

    private sealed class PoisonableGraphStore(ConcurrentQueue<string> failures) : InMemoryGraphStore
    {
        public bool Poisoned { get; set; }

        public override Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetProjectHealthSummariesAsync(string project)
        {
            ThrowIfPoisoned(nameof(GetProjectHealthSummariesAsync));
            return base.GetProjectHealthSummariesAsync(project);
        }

        public override Task<Dictionary<string, int>> GetNodeCountsByLabelForProjectAsync(string project)
        {
            ThrowIfPoisoned(nameof(GetNodeCountsByLabelForProjectAsync));
            return base.GetNodeCountsByLabelForProjectAsync(project);
        }

        private void ThrowIfPoisoned(string operation)
        {
            if (!Poisoned)
                return;
            failures.Enqueue(operation);
            throw new InvalidOperationException(
                "A second operation was started on this context instance before a previous operation completed.");
        }
    }

    private sealed class ContextPoisoningVitalsAnalyzer(IGraphStore store) : IVitalsAnalyzer
    {
        public Task ComputeMetricsAsync(string projectName, string repoPath, CancellationToken ct = default)
        {
            ((PoisonableGraphStore)store).Poisoned = true;
            throw new OutOfMemoryException("simulated: allocation failed while a query was in flight");
        }

        public Task AnalyzeHealthAsync(string projectName, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CleanSecurityAnalyzer : ISecurityAnalyzer
    {
        public Task<SecurityScanResult> ScanAsync(string projectName, string repoPath, CancellationToken ct = default) =>
            Task.FromResult(new SecurityScanResult(10, 0, 0, 0, 0, []));
    }

    private sealed class RecordingBatchService(ConcurrentQueue<string> submitted) : IBatchAnalysisService
    {
        public Task SubmitAnalysisBatchAsync(string repoName, string? repoPath = null, bool includeAllSource = false,
            CancellationToken ct = default)
        {
            submitted.Enqueue(repoName);
            return Task.CompletedTask;
        }

        public Task ProcessCompletedBatchesAsync(string? repo = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task ProcessCompletedBatchAsync(string repoName, string providerBatchId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SynthesizeRepoSummaryAsync(string repoName, string batchId, CancellationToken ct) => Task.CompletedTask;

        public Task WriteCodeGraphDocsAsync(string repoName, CancellationToken ct) => Task.CompletedTask;
    }
}
