using MassTransit;
using CodeGraph.Data;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Memory;

namespace CodeGraph.Host.Shared.Consumers;

public class StoreMemoryClaimsConsumer(
    MemoryService memoryService,
    IMemoryTenantContext tenantContext) : IConsumer<StoreMemoryClaims>
{
    public async Task Consume(ConsumeContext<StoreMemoryClaims> context)
    {
        var message = context.Message;
        using var tenantScope = tenantContext.Enter(message.Username);
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
