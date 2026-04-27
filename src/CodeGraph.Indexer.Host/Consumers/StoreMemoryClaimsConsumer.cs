using MassTransit;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Memory;

namespace CodeGraph.Indexer.Host.Consumers;

public class StoreMemoryClaimsConsumer(
    MemoryService memoryService) : IConsumer<StoreMemoryClaims>
{
    public async Task Consume(ConsumeContext<StoreMemoryClaims> context)
    {
        var message = context.Message;
        if (!string.IsNullOrWhiteSpace(message.ReceiptId))
            await memoryService.MarkWriteReceiptProcessingAsync(message.ReceiptId);

        try
        {
            var result = await memoryService.StoreClaimsAsync(message.Extraction, message.Source);

            if (!string.IsNullOrWhiteSpace(message.ReceiptId))
                await memoryService.CompleteWriteReceiptAsync(message.ReceiptId, result);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(message.ReceiptId))
                await memoryService.FailWriteReceiptAsync(message.ReceiptId, ex.Message);

            throw;
        }
    }
}
