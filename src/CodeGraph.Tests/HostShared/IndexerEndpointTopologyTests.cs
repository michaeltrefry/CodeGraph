using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Host.Shared.Consumers;
using CodeGraph.Services.Configuration;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.HostShared;

/// <summary>
/// sc-25698: a delivery RabbitMQ has handed out must be acked within consumer_timeout (30 min by
/// default) or the broker closes the channel and redelivers every unacked message. Endpoints whose
/// consume is long only take as many deliveries as they can start right away. (process-repository
/// no longer holds its delivery at all: see ProcessRepositoryConsumerTests.)
/// </summary>
public class IndexerEndpointTopologyTests
{
    [Fact]
    public async Task AnalysisBatchSubmittedEndpoint_PrefetchesOnlyItsSingleConcurrentMessage()
    {
        var endpoints = await ProbeIndexerEndpointsAsync(maxParallelRepos: 4);

        endpoints["analysis-batch-submitted"].Prefetch.ShouldBe(1);
        endpoints["analysis-batch-submitted"].Concurrency.ShouldBe(1);
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
}
