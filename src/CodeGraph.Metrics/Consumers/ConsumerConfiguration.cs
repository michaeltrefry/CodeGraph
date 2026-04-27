using CodeGraph.Models.Exceptions;
using CodeGraph.Services.Configuration;
using MassTransit;

namespace CodeGraph.Metrics.Consumers;

public static class ConsumerConfiguration
{
    public static void ConfigureStandardRetries(IReceiveEndpointConfigurator endpoint, ConsumerOptions options)
    {
        endpoint.UseMessageRetry(retry =>
        {
            retry.Incremental(
                    options.ConsumerRetryLimit,
                    TimeSpan.FromMilliseconds(options.ConsumerRetryInitialInterval),
                    TimeSpan.FromMilliseconds(options.ConsumerRetryIntervalIncrement))
                .Ignore<Exception>(ex => ex is DoNotRetryException);
        });

        endpoint.UseDelayedRedelivery(redeliver =>
        {
            redeliver
                .Intervals(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5))
                .Handle<Exception>(ex => ex is MySqlConnector.MySqlException
                                         || ex is System.Net.Sockets.SocketException);
        });
    }
}
