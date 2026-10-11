using MassTransit;
using CodeGraph.Models.Exceptions;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Host.Shared.Consumers;

public static class ConsumerConfiguration
{
    public static void ConfigureStandardRetries(IReceiveEndpointConfigurator endpoint, ConsumerOptions options) =>
        ConfigureRetries(endpoint, options, _ => true);

    /// <summary>
    /// As <see cref="ConfigureStandardRetries"/>, but in-memory (immediate) retries only apply to
    /// exceptions matching <paramref name="retryInPlace"/>. Use it where a retry would redo long work
    /// while the delivery stays unacked; delayed redelivery (a fresh delivery) is unchanged.
    /// </summary>
    public static void ConfigureRetries(
        IReceiveEndpointConfigurator endpoint,
        ConsumerOptions options,
        Func<Exception, bool> retryInPlace)
    {
        endpoint.UseMessageRetry(retry =>
        {
            retry.Incremental(
                    options.ConsumerRetryLimit,
                    TimeSpan.FromMilliseconds(options.ConsumerRetryInitialInterval),
                    TimeSpan.FromMilliseconds(options.ConsumerRetryIntervalIncrement))
                .Ignore<Exception>(ex => ex is DoNotRetryException || !retryInPlace(ex));
        });

        endpoint.UseDelayedRedelivery(redeliver =>
        {
            redeliver
                .Intervals(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5))
                .Handle<Exception>(ex => ex is Neo4j.Driver.ServiceUnavailableException
                                         || ex is MySqlConnector.MySqlException
                                         || ex is System.Net.Sockets.SocketException
                                         || ex is RetryableAnalysisException);
        });
    }
}
