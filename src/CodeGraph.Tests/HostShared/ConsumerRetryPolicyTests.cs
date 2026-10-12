using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Host.Shared.Consumers;
using CodeGraph.Models.Exceptions;
using CodeGraph.Services.Configuration;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeGraph.Tests.HostShared;

/// <summary>
/// sc-25698: the production RabbitMQ has no rabbitmq_delayed_message_exchange plugin, so no host may
/// configure delayed redelivery or a delayed message scheduler (publishing to the x-delayed-message
/// exchange faults). Every endpoint retries in process instead and then faults to _error.
/// </summary>
public class ConsumerRetryPolicyTests
{
    private static readonly string[] ExpectedTransientIntervals = ["00:00:05", "00:00:15", "00:00:30", "00:01:00"];

    public static TheoryData<string> Hosts => ["Api", "Indexer", "Memory", "Metrics"];

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task HostBus_ConfiguresNoDelayedRedeliveryOrScheduler(string host)
    {
        var probe = await ProbeHostAsync(host);

        var filterTypes = probe.Descendants()
            .Select(node => node["filterType"]?.GetValue<string>())
            .OfType<string>()
            .ToList();
        filterTypes.ShouldContain("retry");
        filterTypes.ShouldNotContain(type =>
            type.Contains("delayed", StringComparison.OrdinalIgnoreCase)
            || type.Contains("scheduler", StringComparison.OrdinalIgnoreCase)
            || type.Contains("redelivery", StringComparison.OrdinalIgnoreCase));
        probe.Descendants()
            .Select(node => node["type"] as JsonValue)
            .OfType<JsonValue>()
            .Select(type => type.ToJsonString())
            .ShouldNotContain("\"redelivery\"");
    }

    [Theory]
    [InlineData("Api", new[]
    {
        "process-repository", "repository-indexing-completed", "analysis-batch-submitted",
        "project-analysis-results-processed", "analysis-synthesis-completed", "repository-removed",
        "store-memory-claims", "wiki-page-changed"
    })]
    [InlineData("Indexer", new[]
    {
        "process-repository", "repository-indexing-completed", "analysis-batch-submitted",
        "project-analysis-results-processed", "analysis-synthesis-completed", "repository-removed"
    })]
    [InlineData("Memory", new[] { "store-memory-claims" })]
    [InlineData("Metrics", new[] { "llm-usage-recorded", "mcp-tool-invocation-recorded" })]
    public async Task EveryHostEndpoint_RetriesTransientFailuresOnLongIntervalsThenOthersIncrementally(
        string host,
        string[] expectedQueues)
    {
        var probe = await ProbeHostAsync(host);

        var endpoints = probe.Descendants()
            .Where(node => node["receiveTransport"]?["queueName"] is not null)
            // Skip the bus's own temporary response endpoint, which hosts no consumer.
            .Where(node => node.Descendants().Any(child => child.ContainsKey("consumer")))
            .ToDictionary(node => node["receiveTransport"]!["queueName"]!.GetValue<string>());
        endpoints.Keys.ShouldBe(expectedQueues, ignoreOrder: true);

        foreach (var (queue, endpoint) in endpoints)
        {
            var retries = endpoint.Descendants()
                .Where(node => node["filterType"]?.GetValue<string>() == "retry")
                .Select(node => node["retry-consumeContext"]!.AsObject())
                .ToList();
            retries.Count.ShouldBe(2, queue);

            retries[0]["policy"]!.GetValue<string>().ShouldBe("Interval", queue);
            retries[0]["intervals"]!.AsArray().Select(i => i!.GetValue<string>())
                .ShouldBe(ExpectedTransientIntervals, queue);

            retries[1]["policy"]!.GetValue<string>().ShouldBe("Incremental", queue);
            retries[1]["limit"]!.GetValue<int>().ShouldBe(3, queue);
            retries[1]["initial"]!.GetValue<string>().ShouldBe("00:00:01", queue);
            retries[1]["increment"]!.GetValue<string>().ShouldBe("00:00:01", queue);
        }
    }

    [Fact]
    public void TransientRetrySchedule_WaitsOneToTwoMinutesPerDelivery()
    {
        var total = ConsumerConfiguration.TransientRetryIntervals.Aggregate(TimeSpan.Zero, (sum, i) => sum + i);

        // RabbitMQ's consumer_timeout is 30 minutes; the held delivery must ack long before that.
        total.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(1));
        total.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(2));
    }

    [Theory]
    [InlineData("socket", 3)]
    [InlineData("retryable-analysis", 3)]
    [InlineData("other", 4)]
    [InlineData("do-not-retry", 1)]
    public async Task StandardRetries_ApplyExactlyOnePolicyPerExceptionThenFault(string failure, int expectedAttempts)
    {
        var consumer = new FailingConsumerState(failure);
        await using var provider = new ServiceCollection()
            .AddSingleton(consumer)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<FailingConsumer>();
                x.AddConfigureEndpointsCallback((_, endpoint) =>
                    ConsumerConfiguration.ConfigureRetries(
                        endpoint,
                        FastOptions,
                        _ => true,
                        [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)]));
            })
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new RetryProbe());
            (await harness.Published.Any<Fault<RetryProbe>>()).ShouldBeTrue();
        }
        finally
        {
            await harness.Stop();
        }

        consumer.Attempts.ShouldBe(expectedAttempts);
    }

    [Fact]
    public void IsTransient_CoversInfrastructureFailuresOnly()
    {
        ConsumerConfiguration.IsTransient(new SocketException()).ShouldBeTrue();
        ConsumerConfiguration.IsTransient(new RetryableAnalysisException("rate limited")).ShouldBeTrue();
        ConsumerConfiguration.IsTransient(new Neo4j.Driver.ServiceUnavailableException("down")).ShouldBeTrue();
        ConsumerConfiguration.IsTransient(new InvalidOperationException("bug")).ShouldBeFalse();
        ConsumerConfiguration.IsTransient(new DoNotRetryException("permanent")).ShouldBeFalse();
    }

    internal static ConsumerOptions FastOptions => new()
    {
        ConsumerRetryLimit = 3,
        ConsumerRetryInitialInterval = 1,
        ConsumerRetryIntervalIncrement = 1
    };

    private static async Task<JsonObject> ProbeHostAsync(string host)
    {
        var services = new ServiceCollection();
        switch (host)
        {
            case "Api":
                CodeGraph.Api.Startup.ConfigureServices(services, Configuration(new()
                {
                    ["CodeGraph:AnalysisOptions:DefaultProvider"] = "anthropic",
                    ["CodeGraph:StorageOptions:Provider"] = "Neo4j",
                    ["CodeGraph:RepositorySource:Provider"] = "Folder",
                    ["CodeGraph:RepositorySource:Folder:RootPath"] = "/tmp"
                }));
                break;
            case "Indexer":
                CodeGraph.Indexer.Host.Startup.ConfigureServices(services, Configuration(new()
                {
                    ["CodeGraph:AnalysisOptions:DefaultProvider"] = "anthropic",
                    ["CodeGraph:RepositorySource:Provider"] = "Folder",
                    ["CodeGraph:RepositorySource:Folder:RootPath"] = "/tmp",
                    ["CodeGraph:StorageOptions:Provider"] = "MariaDb",
                    ["CodeGraph:StorageOptions:MariaDbConnectionString"] = MariaDbConnectionString,
                    ["CodeGraph:InternalServiceAuth:Enabled"] = "false"
                }));
                break;
            case "Memory":
                CodeGraph.Memory.Host.Startup.ConfigureServices(services, Configuration(MariaDbHostSettings));
                break;
            case "Metrics":
                CodeGraph.Metrics.Startup.ConfigureServices(services, Configuration(MariaDbHostSettings));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(host), host, null);
        }

        await using var provider = services.BuildServiceProvider();
        // The probe describes the configured (not started) RabbitMQ bus and its receive endpoints.
        return JsonSerializer.SerializeToNode(
            provider.GetRequiredService<IBusControl>().GetProbeResult().Results)!.AsObject();
    }

    private const string MariaDbConnectionString =
        "Server=localhost;Database=codegraph_tests;User ID=codegraph;Password=codegraph_test!;";

    private static Dictionary<string, string?> MariaDbHostSettings => new()
    {
        ["CodeGraph:StorageOptions:Provider"] = "MariaDb",
        ["CodeGraph:StorageOptions:MariaDbConnectionString"] = MariaDbConnectionString,
        ["CodeGraph:InternalServiceAuth:Enabled"] = "false"
    };

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    public record RetryProbe;

    public sealed class FailingConsumerState(string failure)
    {
        private int _attempts;
        public int Attempts => _attempts;

        public Exception NextFailure()
        {
            Interlocked.Increment(ref _attempts);
            return failure switch
            {
                "socket" => new SocketException((int)SocketError.ConnectionRefused),
                "retryable-analysis" => new RetryableAnalysisException("provider overloaded"),
                "do-not-retry" => new DoNotRetryException("permanent"),
                _ => new InvalidOperationException("bug")
            };
        }
    }

    public sealed class FailingConsumer(FailingConsumerState state) : IConsumer<RetryProbe>
    {
        public Task Consume(ConsumeContext<RetryProbe> context) => Task.FromException(state.NextFailure());
    }
}

internal static class JsonNodeTraversal
{
    public static IEnumerable<JsonObject> Descendants(this JsonNode root)
    {
        var stack = new Stack<JsonNode?>([root]);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case JsonObject obj:
                    yield return obj;
                    // Push in reverse so the walk visits children in document (pipe) order.
                    foreach (var child in obj.Reverse())
                        stack.Push(child.Value);
                    break;
                case JsonArray array:
                    foreach (var child in array.Reverse())
                        stack.Push(child);
                    break;
            }
        }
    }
}
