using CodeGraph.Data;
using CodeGraph.Models.Requests;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class AssistantRunServiceTests
{
    [Fact]
    public async Task CreateRunAsync_NormalizesUser_PersistsRequest_AndEnqueuesRun()
    {
        var store = new RecordingAssistantRunStore();
        var runner = new RecordingAssistantRunBackgroundRunner();
        var service = new AssistantRunService(
            store,
            assistant: null!,
            runner,
            new NoopAssistantDebugCapture(),
            NullLogger<AssistantRunService>.Instance);

        var result = await service.CreateRunAsync(
            new AskRequest(
                "How does search work?",
                Context: "repo:CodeGraph",
                History: [new ChatMessage("user", "Earlier question")],
                Provider: "anthropic",
                Model: "claude-test",
                ChatId: "chat-1"),
            "Michael",
            "idem-1");

        result.ReusedExisting.ShouldBeFalse();
        result.Run.ChatId.ShouldBe("chat-1");
        result.Run.Username.ShouldBe("michael");
        store.LastCreateRequest.ShouldNotBeNull();
        store.LastCreateRequest.IdempotencyKey.ShouldBe("idem-1");
        store.LastCreateRequest.ProviderRequested.ShouldBe("anthropic");
        store.LastCreateRequest.ModelRequested.ShouldBe("claude-test");
        store.LastCreateRequest.History.Single().Content.ShouldBe("Earlier question");
        runner.EnqueuedRunIds.ShouldBe([1L]);
    }

    [Fact]
    public async Task GetDebugExchangesAsync_ReturnsOwnedRunDebugData_AndAuditsView()
    {
        var store = new RecordingAssistantRunStore
        {
            ExistingRun = new AssistantRunEntity
            {
                Id = 7,
                ChatId = "chat-7",
                Username = "michael",
                Status = "completed",
                Question = "What happened?",
                CreatedAt = DateTime.UtcNow.AddMinutes(-3),
                CompletedAt = DateTime.UtcNow,
                LastSequence = 3
            }
        };
        store.DebugExchanges.Add(new AssistantDebugExchangeEntity
        {
            RunId = 7,
            ChatId = "chat-7",
            Username = "michael",
            ExchangeIndex = 0,
            TurnIndex = 1,
            Provider = "openai",
            Model = "gpt-test",
            RequestBodyJson = """{"messageCount":2}""",
            ResponseBodyJson = """{"choices":[]}""",
            RequestText = "user: What happened?",
            ResponseText = "I searched the graph.",
            ToolUsesJson = """[{"name":"search_graph"}]""",
            InputTokens = 10,
            OutputTokens = 20,
            TotalTokens = 30,
            CreatedAt = DateTime.UtcNow
        });

        var service = new AssistantRunService(
            store,
            assistant: null!,
            new RecordingAssistantRunBackgroundRunner(),
            new NoopAssistantDebugCapture(),
            NullLogger<AssistantRunService>.Instance);

        var response = await service.GetDebugExchangesAsync(7, "Michael", "127.0.0.1", "test-agent");

        response.ShouldNotBeNull();
        response.Run.Id.ShouldBe(7);
        response.Exchanges.Single().Provider.ShouldBe("openai");
        response.Exchanges.Single().TotalTokens.ShouldBe(30);
        store.TraceAudits.Single().ViewedByUsername.ShouldBe("michael");
        store.TraceAudits.Single().RemoteIp.ShouldBe("127.0.0.1");
    }

    private sealed class NoopAssistantDebugCapture : IAssistantDebugCapture
    {
        public IDisposable BeginRun(AssistantDebugRunContext context) => NullScope.Instance;
        public Task CaptureExchangeAsync(AssistantDebugExchangeCapture exchange, CancellationToken ct = default) => Task.CompletedTask;

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }

    private sealed class RecordingAssistantRunBackgroundRunner : IAssistantRunBackgroundRunner
    {
        public List<long> EnqueuedRunIds { get; } = [];

        public Task EnqueueAsync(long runId, CancellationToken ct = default)
        {
            EnqueuedRunIds.Add(runId);
            return Task.CompletedTask;
        }

        public Task<bool> CancelAsync(long runId, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class RecordingAssistantRunStore : IAssistantRunStore
    {
        public AssistantRunCreateRequest? LastCreateRequest { get; private set; }
        public AssistantRunEntity? ExistingRun { get; set; }
        public List<AssistantDebugExchangeEntity> DebugExchanges { get; } = [];
        public List<AssistantDebugTraceAuditEntity> TraceAudits { get; } = [];

        public Task<AssistantRunCreateResult> CreateAssistantRunAsync(
            AssistantRunCreateRequest request,
            CancellationToken ct = default)
        {
            LastCreateRequest = request;
            return Task.FromResult(new AssistantRunCreateResult(new AssistantRunEntity
            {
                Id = 1,
                ChatId = request.ChatId,
                Username = request.Username,
                Status = "queued",
                Question = request.Question,
                Context = request.Context,
                ProviderRequested = request.ProviderRequested,
                ModelRequested = request.ModelRequested,
                CreatedAt = request.CreatedAt
            }));
        }

        public Task UpdateAssistantRunStatusAsync(long runId, string status, string? finalAnswer = null, string? warningsJson = null, DateTime? completedAt = null, string? error = null, string? providerUsed = null, string? modelUsed = null) => Task.CompletedTask;
        public Task MarkAssistantRunCompletedAsync(long runId, string? finalAnswer = null, string? warningsJson = null, DateTime? completedAt = null, string? providerUsed = null, string? modelUsed = null) => Task.CompletedTask;
        public Task<AssistantRunEntity?> GetAssistantRunAsync(long runId) => Task.FromResult(ExistingRun?.Id == runId ? ExistingRun : null);
        public Task<IReadOnlyList<AssistantRunEntity>> GetAssistantRunsByStatusAsync(IReadOnlyList<string> statuses) => Task.FromResult<IReadOnlyList<AssistantRunEntity>>([]);
        public Task<AssistantRunEntity?> TryClaimAssistantRunAsync(long runId, string ownerId, DateTime leaseExpiresAt, CancellationToken ct = default) => Task.FromResult<AssistantRunEntity?>(null);
        public Task<AssistantRunLeaseRenewalResult> RenewAssistantRunLeaseAsync(long runId, string ownerId, DateTime leaseExpiresAt, CancellationToken ct = default) => Task.FromResult(new AssistantRunLeaseRenewalResult(false, false));
        public Task RequestAssistantRunCancellationAsync(long runId, string username, DateTime requestedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAssistantRunProgressAsync(long runId, AssistantRunProgressUpdate progress, CancellationToken ct = default) => Task.CompletedTask;
        public Task TransitionAssistantRunToTerminalAsync(long runId, AssistantRunTerminalUpdate update, CancellationToken ct = default) => Task.CompletedTask;
        public Task<AssistantRunEntity?> GetLatestAssistantRunAsync(string username, string chatId, CancellationToken ct = default) => Task.FromResult<AssistantRunEntity?>(null);
        public Task<IReadOnlyList<AssistantChatSummary>> GetAssistantChatSummariesAsync(string username, int take = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AssistantChatSummary>>([]);
        public Task<IReadOnlyList<AssistantChatMessageEntity>> GetAssistantChatMessagesAsync(string username, string chatId, long startMessageIndex = 0, long? endMessageIndex = null) => Task.FromResult<IReadOnlyList<AssistantChatMessageEntity>>([]);
        public Task AppendAssistantRunEventAsync(AssistantRunEventEntity evt) => Task.CompletedTask;
        public Task<IReadOnlyList<AssistantRunEventEntity>> GetAssistantRunEventsAsync(long runId, long afterSequence = 0, int? take = null) => Task.FromResult<IReadOnlyList<AssistantRunEventEntity>>([]);
        public Task AppendAssistantDebugExchangeAsync(AssistantDebugExchangeEntity exchange, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AssistantDebugExchangeEntity>> GetAssistantDebugExchangesAsync(long runId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AssistantDebugExchangeEntity>>(DebugExchanges.Where(exchange => exchange.RunId == runId).ToList());
        public Task AppendAssistantDebugTraceAuditAsync(AssistantDebugTraceAuditEntity audit, CancellationToken ct = default)
        {
            TraceAudits.Add(audit);
            return Task.CompletedTask;
        }
        public Task<AssistantRetentionCleanupResult> CleanupAssistantRetentionAsync(AssistantRetentionCleanupRequest request, CancellationToken ct = default) => Task.FromResult(new AssistantRetentionCleanupResult(0, 0, 0, 0, 0, 0));
    }
}
