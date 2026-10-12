using MassTransit;
using CodeGraph.Models.Exceptions;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Host.Shared.Consumers;

/// <summary>
/// Consumer retry policy shared by every receive endpoint. All retries happen in process while the
/// delivery is held; nothing uses delayed redelivery or a delayed message scheduler, because the
/// production RabbitMQ has no rabbitmq_delayed_message_exchange plugin (sc-25698). A message whose
/// retries are exhausted faults to its <c>_error</c> queue.
/// </summary>
public static class ConsumerConfiguration
{
    /// <summary>
    /// Waits between attempts for transient infrastructure failures (<see cref="IsTransient"/>):
    /// 4 retries, 110 seconds of waiting in total, far below RabbitMQ's 30-minute consumer_timeout.
    /// </summary>
    public static IReadOnlyList<TimeSpan> TransientRetryIntervals { get; } =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    ];

    /// <summary>Failures worth waiting out: the database, graph store or analysis provider is briefly unavailable.</summary>
    public static bool IsTransient(Exception exception) =>
        exception is Neo4j.Driver.ServiceUnavailableException
            or MySqlConnector.MySqlException
            or System.Net.Sockets.SocketException
            or RetryableAnalysisException;

    public static void ConfigureStandardRetries(IReceiveEndpointConfigurator endpoint, ConsumerOptions options) =>
        ConfigureRetries(endpoint, options, _ => true);

    /// <summary>
    /// As <see cref="ConfigureStandardRetries"/>, but only exceptions matching
    /// <paramref name="retryInPlace"/> are retried at all. Use it where a retry would redo long work
    /// while the delivery stays unacked; anything else faults to <c>_error</c> on the first failure.
    /// </summary>
    public static void ConfigureRetries(
        IReceiveEndpointConfigurator endpoint,
        ConsumerOptions options,
        Func<Exception, bool> retryInPlace) =>
        ConfigureRetries(endpoint, options, retryInPlace, TransientRetryIntervals);

    /// <summary>
    /// Transient failures retry on <paramref name="transientIntervals"/>; any other failure (except
    /// <see cref="DoNotRetryException"/>) retries on the short incremental schedule from
    /// <paramref name="options"/>. The two policies never apply to the same exception.
    /// </summary>
    public static void ConfigureRetries(
        IReceiveEndpointConfigurator endpoint,
        ConsumerOptions options,
        Func<Exception, bool> retryInPlace,
        IReadOnlyList<TimeSpan> transientIntervals)
    {
        endpoint.UseMessageRetry(retry =>
        {
            retry.Intervals(transientIntervals.ToArray())
                .Handle<Exception>(ex => IsTransient(ex) && retryInPlace(ex));
        });

        endpoint.UseMessageRetry(retry =>
        {
            retry.Incremental(
                    options.ConsumerRetryLimit,
                    TimeSpan.FromMilliseconds(options.ConsumerRetryInitialInterval),
                    TimeSpan.FromMilliseconds(options.ConsumerRetryIntervalIncrement))
                .Ignore<Exception>(ex => ex is DoNotRetryException || IsTransient(ex) || !retryInPlace(ex));
        });
    }
}
