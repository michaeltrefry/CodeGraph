using CodeGraph.Services.Configuration;
using MassTransit;

namespace CodeGraph.Metrics.Consumers;

public static class ConsumerConfiguration
{
    /// <summary>Same in-process retry policy as every other host; see the shared ConsumerConfiguration.</summary>
    public static void ConfigureStandardRetries(IReceiveEndpointConfigurator endpoint, ConsumerOptions options) =>
        CodeGraph.Host.Shared.Consumers.ConsumerConfiguration.ConfigureStandardRetries(endpoint, options);
}
