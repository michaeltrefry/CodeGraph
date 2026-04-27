using CodeGraph.Models.Memory;
using CodeGraph.Services.Memory;
using CodeGraph.Services.Messaging;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Memory.Host.Controllers;

[ApiController]
[Route("health/memory-write")]
public class MemoryWriteHealthController(MemoryService memoryService, IMessageBus messageBus) : ControllerBase
{
    private const string ProbeSource = "memory_health_probe";
    private const int MaxAttempts = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var submission = await memoryService.QueueClaimsAsync(
            CreateProbeExtraction(),
            ProbeSource,
            "typed",
            messageBus,
            ct);

        MemoryWriteReceipt? receipt = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            receipt = await memoryService.GetWriteReceiptAsync(submission.ReceiptId);
            if (receipt?.Status is MemoryWriteReceiptStatus.Completed)
            {
                return Ok(new
                {
                    status = "healthy",
                    receiptId = submission.ReceiptId,
                    writeStatus = receipt,
                });
            }

            if (receipt?.Status is MemoryWriteReceiptStatus.Failed)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    status = "unhealthy",
                    receiptId = submission.ReceiptId,
                    writeStatus = receipt,
                });
            }

            await Task.Delay(PollInterval, ct);
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            status = "timed_out",
            receiptId = submission.ReceiptId,
            writeStatus = receipt,
        });
    }

    private static MemoryClaimExtractionResult CreateProbeExtraction()
    {
        return new MemoryClaimExtractionResult
        {
            Entities =
            [
                new MemoryExtractedEntity
                {
                    Id = "memory_host_health_probe",
                    Label = "Memory Host Health Probe",
                    Type = "probe",
                    Summary = "Health probe for the memory write pipeline",
                    Source = ProbeSource,
                }
            ]
        };
    }
}
