using CodeGraph.Services.Configuration;
using MassTransit;

namespace CodeGraph.Host.Shared.Consumers;

/// <summary>
/// Owns the consumer types and endpoint policies shared by integrated and split hosts.
/// Each host still decides explicitly which topology slices it owns.
/// </summary>
public static class SharedConsumerTopology
{
    public static IReadOnlyList<Type> IndexerConsumerTypes { get; } =
    [
        typeof(ProcessRepositoryConsumer),
        typeof(RepositoryIndexingCompletedConsumer),
        typeof(AnalysisBatchSubmittedConsumer),
        typeof(ProjectAnalysisResultsProcessedConsumer),
        typeof(AnalysisSynthesisCompletedConsumer),
        typeof(RepositoryRemovedConsumer)
    ];

    public static IReadOnlyList<string> IndexerEndpointNames { get; } =
    [
        "process-repository",
        "repository-indexing-completed",
        "analysis-batch-submitted",
        "project-analysis-results-processed",
        "analysis-synthesis-completed",
        "repository-removed"
    ];

    public static void AddIndexerConsumers(IBusRegistrationConfigurator registration)
    {
        registration.AddConsumer<ProcessRepositoryConsumer>();
        registration.AddConsumer<RepositoryIndexingCompletedConsumer>();
        registration.AddConsumer<AnalysisBatchSubmittedConsumer>();
        registration.AddConsumer<ProjectAnalysisResultsProcessedConsumer>();
        registration.AddConsumer<AnalysisSynthesisCompletedConsumer>();
        registration.AddConsumer<RepositoryRemovedConsumer>();
    }

    public static void AddMemoryConsumer(IBusRegistrationConfigurator registration) =>
        registration.AddConsumer<StoreMemoryClaimsConsumer>();

    public static void ConfigureIndexerEndpoints(
        IRabbitMqBusFactoryConfigurator bus,
        IBusRegistrationContext context,
        ConsumerOptions options)
    {
        bus.ReceiveEndpoint("process-repository", endpoint =>
        {
            // The consumer only records a durable indexer run and acks; the index itself runs in
            // IndexerRunBackgroundRunner (MaxParallelRepos workers) under a heartbeated lease, so
            // no delivery is held for the length of an index (RabbitMQ consumer_timeout, sc-25698).
            ConsumerConfiguration.ConfigureStandardRetries(endpoint, options);
            endpoint.ConfigureConsumer<ProcessRepositoryConsumer>(context);
        });
        bus.ReceiveEndpoint("repository-indexing-completed", endpoint =>
        {
            ConsumerConfiguration.ConfigureStandardRetries(endpoint, options);
            endpoint.ConfigureConsumer<RepositoryIndexingCompletedConsumer>(context);
        });
        bus.ReceiveEndpoint("analysis-batch-submitted", endpoint =>
        {
            // Batch result processing runs one at a time; do not hold further deliveries unacked behind it.
            ConfigureDeliveryLimit(endpoint, 1);
            ConsumerConfiguration.ConfigureStandardRetries(endpoint, options);
            endpoint.ConfigureConsumer<AnalysisBatchSubmittedConsumer>(context);
        });
        bus.ReceiveEndpoint("project-analysis-results-processed", endpoint =>
        {
            ConsumerConfiguration.ConfigureStandardRetries(endpoint, options);
            endpoint.ConfigureConsumer<ProjectAnalysisResultsProcessedConsumer>(context);
        });
        bus.ReceiveEndpoint("analysis-synthesis-completed", endpoint =>
        {
            ConsumerConfiguration.ConfigureStandardRetries(endpoint, options);
            endpoint.ConfigureConsumer<AnalysisSynthesisCompletedConsumer>(context);
        });
        bus.ReceiveEndpoint("repository-removed", endpoint =>
        {
            ConsumerConfiguration.ConfigureStandardRetries(endpoint, options);
            endpoint.ConfigureConsumer<RepositoryRemovedConsumer>(context);
        });
    }

    private static void ConfigureDeliveryLimit(IRabbitMqReceiveEndpointConfigurator endpoint, int limit)
    {
        endpoint.PrefetchCount = limit;
        endpoint.ConcurrentMessageLimit = limit;
    }

    public static void ConfigureMemoryEndpoint(
        IRabbitMqBusFactoryConfigurator bus,
        IBusRegistrationContext context,
        ConsumerOptions options) =>
        bus.ReceiveEndpoint("store-memory-claims", endpoint =>
        {
            ConsumerConfiguration.ConfigureStandardRetries(endpoint, options);
            endpoint.ConfigureConsumer<StoreMemoryClaimsConsumer>(context);
        });
}
