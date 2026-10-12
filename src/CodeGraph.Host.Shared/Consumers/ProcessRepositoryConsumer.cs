using MassTransit;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Indexer;

namespace CodeGraph.Host.Shared.Consumers;

/// <summary>
/// Hands each ProcessRepository message to the durable indexer run queue and acks it at once.
/// Indexing never runs while the delivery is held: a full index of a large repository outlasts
/// RabbitMQ's consumer_timeout (30 min by default), after which the broker closes the channel,
/// cancels the consume and redelivers the message, in a loop (sc-25698).
/// </summary>
public class ProcessRepositoryConsumer(ProcessRepositoryHandoff handoff) : IConsumer<ProcessRepository>
{
    public Task Consume(ConsumeContext<ProcessRepository> context) =>
        handoff.HandOffAsync(context.Message, context.CancellationToken);
}
