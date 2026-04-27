using CodeGraph.Data;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class AssistantRetentionCleanupServiceTests
{
    [Fact]
    public async Task CleanupAsync_BuildsRetentionRequestFromOptions()
    {
        var store = new RecordingAssistantRunStore();
        var service = new AssistantRetentionCleanupService(
            store,
            Options.Create(new AssistantRetentionOptions
            {
                StaleActiveRunMinutes = 15,
                TerminalRunRetentionDays = 20,
                EventRetentionDays = 30,
                ChatMessageRetentionDays = 40,
                DebugExchangeRetentionDays = 50,
                DebugTraceAuditRetentionDays = 60,
                BatchSize = 20_000
            }),
            NullLogger<AssistantRetentionCleanupService>.Instance);

        var before = DateTime.UtcNow;
        var result = await service.CleanupAsync();
        var after = DateTime.UtcNow;

        result.TotalRowsAffected.ShouldBe(21);
        store.LastRequest.ShouldNotBeNull();
        store.LastRequest.BatchSize.ShouldBe(10_000);
        store.LastRequest.StaleActiveRunCutoffUtc!.Value.ShouldBeInRange(before.AddMinutes(-16), after.AddMinutes(-14));
        store.LastRequest.TerminalRunCutoffUtc!.Value.ShouldBeInRange(before.AddDays(-21), after.AddDays(-19));
        store.LastRequest.EventCutoffUtc!.Value.ShouldBeInRange(before.AddDays(-31), after.AddDays(-29));
        store.LastRequest.ChatMessageCutoffUtc!.Value.ShouldBeInRange(before.AddDays(-41), after.AddDays(-39));
        store.LastRequest.DebugExchangeCutoffUtc!.Value.ShouldBeInRange(before.AddDays(-51), after.AddDays(-49));
        store.LastRequest.DebugTraceAuditCutoffUtc!.Value.ShouldBeInRange(before.AddDays(-61), after.AddDays(-59));
    }

    [Fact]
    public async Task CleanupAsync_DisablesCutoffsWhenRetentionValuesAreZero()
    {
        var store = new RecordingAssistantRunStore();
        var service = new AssistantRetentionCleanupService(
            store,
            Options.Create(new AssistantRetentionOptions
            {
                StaleActiveRunMinutes = 0,
                TerminalRunRetentionDays = 0,
                EventRetentionDays = 0,
                ChatMessageRetentionDays = 0,
                DebugExchangeRetentionDays = 0,
                DebugTraceAuditRetentionDays = 0,
                BatchSize = -1
            }),
            NullLogger<AssistantRetentionCleanupService>.Instance);

        await service.CleanupAsync();

        store.LastRequest.ShouldNotBeNull();
        store.LastRequest.BatchSize.ShouldBe(1);
        store.LastRequest.StaleActiveRunCutoffUtc.ShouldBeNull();
        store.LastRequest.TerminalRunCutoffUtc.ShouldBeNull();
        store.LastRequest.EventCutoffUtc.ShouldBeNull();
        store.LastRequest.ChatMessageCutoffUtc.ShouldBeNull();
        store.LastRequest.DebugExchangeCutoffUtc.ShouldBeNull();
        store.LastRequest.DebugTraceAuditCutoffUtc.ShouldBeNull();
    }

    private sealed class RecordingAssistantRunStore : IAssistantRunStore
    {
        public AssistantRetentionCleanupRequest? LastRequest { get; private set; }

        public Task<AssistantRetentionCleanupResult> CleanupAssistantRetentionAsync(
            AssistantRetentionCleanupRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new AssistantRetentionCleanupResult(1, 2, 3, 4, 5, 6));
        }

        public Task<AssistantRunCreateResult> CreateAssistantRunAsync(AssistantRunCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAssistantRunStatusAsync(long runId, string status, string? finalAnswer = null, string? warningsJson = null, DateTime? completedAt = null, string? error = null, string? providerUsed = null, string? modelUsed = null) => throw new NotSupportedException();
        public Task MarkAssistantRunCompletedAsync(long runId, string? finalAnswer = null, string? warningsJson = null, DateTime? completedAt = null, string? providerUsed = null, string? modelUsed = null) => throw new NotSupportedException();
        public Task<AssistantRunEntity?> GetAssistantRunAsync(long runId) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssistantRunEntity>> GetAssistantRunsByStatusAsync(IReadOnlyList<string> statuses) => throw new NotSupportedException();
        public Task<AssistantRunEntity?> TryClaimAssistantRunAsync(long runId, string ownerId, DateTime leaseExpiresAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AssistantRunLeaseRenewalResult> RenewAssistantRunLeaseAsync(long runId, string ownerId, DateTime leaseExpiresAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RequestAssistantRunCancellationAsync(long runId, string username, DateTime requestedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveAssistantRunProgressAsync(long runId, AssistantRunProgressUpdate progress, CancellationToken ct = default) => throw new NotSupportedException();
        public Task TransitionAssistantRunToTerminalAsync(long runId, AssistantRunTerminalUpdate update, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AssistantRunEntity?> GetLatestAssistantRunAsync(string username, string chatId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssistantChatSummary>> GetAssistantChatSummariesAsync(string username, int take = 20, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssistantChatMessageEntity>> GetAssistantChatMessagesAsync(string username, string chatId, long startMessageIndex = 0, long? endMessageIndex = null) => throw new NotSupportedException();
        public Task AppendAssistantRunEventAsync(AssistantRunEventEntity evt) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssistantRunEventEntity>> GetAssistantRunEventsAsync(long runId, long afterSequence = 0, int? take = null) => throw new NotSupportedException();
        public Task AppendAssistantDebugExchangeAsync(AssistantDebugExchangeEntity exchange, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssistantDebugExchangeEntity>> GetAssistantDebugExchangesAsync(long runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AppendAssistantDebugTraceAuditAsync(AssistantDebugTraceAuditEntity audit, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
