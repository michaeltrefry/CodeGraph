using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Host.Shared.Consumers;
using CodeGraph.Models.Messages;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.HostShared;

/// <summary>
/// sc-25698: a delivery RabbitMQ has handed out must be acked within consumer_timeout (30 min by
/// default) or the broker closes the channel and redelivers every unacked message. Long-running
/// indexer endpoints therefore only take as many deliveries as they can start right away, and never
/// redo a full index while holding one.
/// </summary>
public class IndexerEndpointTopologyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ProcessRepositoryEndpoint_PrefetchesOnlyWhatMaxParallelReposCanStart(int maxParallelRepos)
    {
        var endpoints = await ProbeIndexerEndpointsAsync(maxParallelRepos);

        endpoints["process-repository"].Prefetch.ShouldBe(maxParallelRepos);
        endpoints["process-repository"].Concurrency.ShouldBe(maxParallelRepos);
    }

    [Fact]
    public async Task AnalysisBatchSubmittedEndpoint_PrefetchesOnlyItsSingleConcurrentMessage()
    {
        var endpoints = await ProbeIndexerEndpointsAsync(maxParallelRepos: 4);

        endpoints["analysis-batch-submitted"].Prefetch.ShouldBe(1);
        endpoints["analysis-batch-submitted"].Concurrency.ShouldBe(1);
    }

    [Theory]
    [InlineData("graph-write", false, 1)]
    [InlineData("resolution", false, 1)]
    [InlineData("unknown", false, 1)]
    [InlineData("resolve-repository", false, 4)]
    [InlineData("check-up-to-date", false, 4)]
    // Transient failures take the long-interval policy (2 test intervals), but still only before indexing.
    [InlineData("graph-write", true, 1)]
    [InlineData("unknown", true, 1)]
    [InlineData("resolve-repository", true, 3)]
    [InlineData("check-up-to-date", true, 3)]
    public async Task ProcessRepositoryRetries_OnlyRetryPreIndexFailuresInPlace(
        string phase,
        bool transient,
        int expectedAttempts)
    {
        var service = new CountingFailingProjectService(phase, transient);
        await using var provider = new ServiceCollection()
            .AddSingleton<IProjectService>(service)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<ProcessRepositoryConsumer>();
                x.AddConfigureEndpointsCallback((_, endpoint) =>
                    ConsumerConfiguration.ConfigureRetries(
                        endpoint,
                        ConsumerRetryPolicyTests.FastOptions,
                        IndexingFailure.FailedBeforeIndexing,
                        [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)]));
            })
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new ProcessRepository { Name = "inference", ShouldIndex = true });
            (await harness.Published.Any<Fault<ProcessRepository>>()).ShouldBeTrue();
        }
        finally
        {
            await harness.Stop();
        }

        service.Attempts.ShouldBe(expectedAttempts);
    }

    private static async Task<Dictionary<string, (int? Prefetch, int? Concurrency)>> ProbeIndexerEndpointsAsync(
        int maxParallelRepos)
    {
        await using var provider = new ServiceCollection()
            .AddSingleton(Options.Create(new IndexingOptions { MaxParallelRepos = maxParallelRepos }))
            .AddMassTransit(x =>
            {
                SharedConsumerTopology.AddIndexerConsumers(x);
                x.UsingRabbitMq((context, cfg) =>
                    SharedConsumerTopology.ConfigureIndexerEndpoints(cfg, context, new ConsumerOptions()));
            })
            .BuildServiceProvider();

        // The probe describes the configured (not started) RabbitMQ receive endpoints.
        var probe = JsonSerializer.SerializeToNode(
            provider.GetRequiredService<IBusControl>().GetProbeResult().Results)!;
        var endpoints = new Dictionary<string, (int?, int?)>();
        Collect(probe, endpoints);
        return endpoints;

        static void Collect(JsonNode? node, Dictionary<string, (int?, int?)> endpoints)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj["queueName"]?.GetValue<string>() is { } queue && obj.ContainsKey("prefetchCount"))
                    {
                        endpoints[queue] = (
                            obj["prefetchCount"]?.GetValue<int>(),
                            obj["concurrentMessageLimit"]?.GetValue<int>());
                    }

                    foreach (var child in obj)
                        Collect(child.Value, endpoints);
                    break;
                case JsonArray array:
                    foreach (var child in array)
                        Collect(child, endpoints);
                    break;
            }
        }
    }

    private sealed class CountingFailingProjectService(string phase, bool transient) : IProjectService
    {
        private int _attempts;
        public int Attempts => _attempts;

        public Task<AnalysisBatchResponse?> ReAnalyzeRepository(string repo, CancellationToken cancellationToken = new()) =>
            throw new NotSupportedException();

        public Task ProcessRepository(ProcessRepository message, CancellationToken cancellationToken = new())
        {
            Interlocked.Increment(ref _attempts);
            Exception failure = transient
                ? new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)
                : new InvalidOperationException($"failed in {phase}");
            if (phase != "unknown")
                IndexingFailure.TagPhase(failure, phase);
            return Task.FromException(failure);
        }

        public Task<bool> DeleteRepositoryAsync(string repo) => throw new NotSupportedException();
    }
}
