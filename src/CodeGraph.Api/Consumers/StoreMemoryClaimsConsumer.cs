using MassTransit;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Memory;

namespace CodeGraph.Api.Consumers;

public class StoreMemoryClaimsConsumer(
    MemoryService memoryService) : IConsumer<StoreMemoryClaims>
{
    public async Task Consume(ConsumeContext<StoreMemoryClaims> context)
    {
        var message = context.Message;
        await memoryService.StoreClaimsAsync(message.Extraction, message.Source);
    }
}
