using CodeGraph.Data;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Work;

public interface IWorkMessageRetentionService
{
    Task<MessageRetentionResult> CleanupAsync(CancellationToken ct = default);
}

/// <summary>The scheduled retention of messages, deliveries and message events (sc-27887).</summary>
public sealed class WorkMessageRetentionService(
    IWorkStore store,
    IOptions<WorkMessageRetentionOptions> optionsAccessor,
    ILogger<WorkMessageRetentionService> logger) : IWorkMessageRetentionService
{
    public async Task<MessageRetentionResult> CleanupAsync(CancellationToken ct = default)
    {
        var options = optionsAccessor.Value;
        if (options.RetentionDays <= 0)
            return new MessageRetentionResult(0, 0, 0);

        var cutoff = DateTime.UtcNow.AddDays(-options.RetentionDays);
        var result = await store.PruneMessagesAsync(cutoff, Math.Clamp(options.BatchSize, 1, 10_000), ct);
        logger.LogInformation(
            "Work message retention deleted {Total} rows: messages={Messages}, deliveries={Deliveries}, events={Events}",
            result.TotalRowsDeleted, result.MessagesDeleted, result.DeliveriesDeleted, result.EventsDeleted);
        return result;
    }
}
