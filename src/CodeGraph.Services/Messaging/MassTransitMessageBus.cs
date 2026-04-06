using MassTransit;

namespace CodeGraph.Services.Messaging;

public class MassTransitMessageBus(IPublishEndpoint publishEndpoint) : IMessageBus
{
    public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
        => publishEndpoint.Publish(message, ct);
}
