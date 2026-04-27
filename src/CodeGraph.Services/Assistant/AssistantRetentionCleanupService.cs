using CodeGraph.Data;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Assistant;

public class AssistantRetentionCleanupService(
    IAssistantRunStore store,
    IOptions<AssistantRetentionOptions> optionsAccessor,
    ILogger<AssistantRetentionCleanupService> logger) : IAssistantRetentionCleanupService
{
    public async Task<AssistantRetentionCleanupResult> CleanupAsync(CancellationToken ct = default)
    {
        var options = optionsAccessor.Value;
        var now = DateTime.UtcNow;
        var request = new AssistantRetentionCleanupRequest(
            now,
            Cutoff(now, options.StaleActiveRunMinutes, TimeUnit.Minutes),
            Cutoff(now, options.TerminalRunRetentionDays, TimeUnit.Days),
            Cutoff(now, options.EventRetentionDays, TimeUnit.Days),
            Cutoff(now, options.ChatMessageRetentionDays, TimeUnit.Days),
            Cutoff(now, options.DebugExchangeRetentionDays, TimeUnit.Days),
            Cutoff(now, options.DebugTraceAuditRetentionDays, TimeUnit.Days),
            Math.Clamp(options.BatchSize, 1, 10_000));

        var result = await store.CleanupAssistantRetentionAsync(request, ct);
        logger.LogInformation(
            "Assistant retention cleanup affected {TotalRows} rows: staleRuns={StaleRuns}, runs={Runs}, events={Events}, chats={Chats}, debug={Debug}, audits={Audits}",
            result.TotalRowsAffected,
            result.StaleRunsFinalized,
            result.RunsDeleted,
            result.EventsDeleted,
            result.ChatMessagesDeleted,
            result.DebugExchangesDeleted,
            result.DebugTraceAuditsDeleted);

        return result;
    }

    private static DateTime? Cutoff(DateTime now, int value, TimeUnit unit)
    {
        if (value <= 0)
            return null;

        return unit == TimeUnit.Minutes
            ? now.AddMinutes(-value)
            : now.AddDays(-value);
    }

    private enum TimeUnit
    {
        Minutes,
        Days
    }
}
