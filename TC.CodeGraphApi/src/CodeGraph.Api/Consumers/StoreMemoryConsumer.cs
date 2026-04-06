using MassTransit;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Memory;

namespace CodeGraph.Api.Consumers;

public class StoreMemoryConsumer(
    MemoryService memoryService,
    ILogger<StoreMemoryConsumer> logger) : IConsumer<StoreMemory>
{
    public async Task Consume(ConsumeContext<StoreMemory> context)
    {
        var message = context.Message;
        await memoryService.StoreStructuredAsync(message.Username, message.Extraction, message.Source);
    }
}
