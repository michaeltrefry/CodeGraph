using MassTransit;
using Microsoft.EntityFrameworkCore;
using TC.CodeGraphApi.Models.Exceptions;
using TC.Common.Configuration;

namespace TC.CodeGraphApi.Consumers;

public class ConsumerConfiguration
{
    public static void ConfigureRetries<TConsumer>(
        IInstanceConfigurator<TConsumer> config,
        ITcConfiguration<CodeGraphServiceSettings> settings) where TConsumer : class, IConsumer
    {
        config.UseMessageRetry(retry =>
        {
            retry.Incremental(
                    settings.Current.ConsumerOptions.ConsumerRetryLimit,
                    TimeSpan.FromMilliseconds(settings.Current.ConsumerOptions.ConsumerRetryInitialInterval),
                    TimeSpan.FromMilliseconds(settings.Current.ConsumerOptions.ConsumerRetryIntervalIncrement))
                .Ignore<Exception>(ex => ex is DoNotRetryException);
        });
        
        config.UseDelayedRedelivery(redeliver =>
        {
            // Redeliver only if we encounter consistent IO issues.
            redeliver
                .Intervals(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5))
                .Handle<Exception>(ex => ex is MySqlConnector.MySqlException
                                         || ex is System.Net.Sockets.SocketException
                                         || ex is DbUpdateException);
        });
    }
}
