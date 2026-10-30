using CodeGraph.Data;
using CodeGraph.Models.Messages;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Messaging;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class BatchAnalysisFailureTests
{
    private const string Valid = """{"projectSummary":"Useful project analysis","confidence":"high","nodes":[]}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderExceptions_PersistOriginalReason(bool downloading)
    {
        var provider = new StubProvider();
        if (downloading) provider.ResultsException = new HttpRequestException("download denied by provider");
        else provider.StatusException = new HttpRequestException("status lookup denied by provider");
        var (service, store, bus, _) = await CreateAsync(provider);

        await service.ProcessCompletedBatchesAsync();

        var batch = (await store.GetLatestBatchAsync("repo"))!;
        batch.Status.ShouldBe("failed");
        batch.CompletedCount.ShouldBe(0);
        batch.Error!.ShouldContain(downloading ? "download denied" : "status lookup denied");
        batch.CompletedAt.ShouldNotBeNull();
        bus.Messages.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("canceled")]
    [InlineData("cancelled")]
    [InlineData("expired")]
    [InlineData("errored")]
    public async Task TerminalProviderFailure_DoesNotPollForever_AndPreservesPartialOutput(string status)
    {
        var provider = new StubProvider
        {
            Status = new("batch", status, false, "provider validation error"),
            Results = [new("request-0", "succeeded", Valid, "model")]
        };
        var (service, store, bus, _) = await CreateAsync(provider, 2);

        await service.ProcessCompletedBatchesAsync();
        await service.ProcessCompletedBatchesAsync();

        var batch = (await store.GetLatestBatchAsync("repo"))!;
        batch.Status.ShouldBe("failed");
        batch.CompletedCount.ShouldBe(1);
        batch.Error!.ShouldContain(status);
        batch.Error!.ShouldContain("provider validation error");
        batch.Error!.ShouldContain("request-1");
        (await store.GetProjectAnalysesAsync("repo")).Count.ShouldBe(1);
        provider.StatusCalls.ShouldBe(1);
        bus.Messages.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not JSON")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"nodes\":[{\"nodeId\":0,\"description\":\"invalid\"}]}")]
    public async Task EmptyOrInvalidResult_IsNotCountedAsSuccessful(string? output)
    {
        var provider = new StubProvider { Results = [new("request-0", "succeeded", output, "model")] };
        var (service, store, bus, id) = await CreateAsync(provider);

        await service.ProcessCompletedBatchesAsync();

        var batch = (await store.GetLatestBatchAsync("repo"))!;
        batch.Status.ShouldBe("failed");
        batch.CompletedCount.ShouldBe(0);
        batch.Error!.ShouldContain("0/1 requests succeeded");
        batch.Error!.ShouldContain("request-0");
        (await store.GetBatchRequestsAsync(id)).Single().Status.ShouldBe("errored");
        bus.Messages.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedAndAllFailedRequests_KeepFailuresAndTruthfulCounts(bool mixed)
    {
        var provider = new StubProvider
        {
            Results = [
                new("request-0", mixed ? "succeeded" : "errored", mixed ? Valid : null, "model", mixed ? null : "quota exhausted"),
                new("request-1", "errored", null, null, "provider refused request")]
        };
        var (service, store, bus, id) = await CreateAsync(provider, 2);

        await service.ProcessCompletedBatchesAsync();

        var batch = (await store.GetLatestBatchAsync("repo"))!;
        batch.Status.ShouldBe("failed");
        batch.CompletedCount.ShouldBe(mixed ? 1 : 0);
        batch.Error!.ShouldContain("provider refused request");
        if (!mixed) batch.Error!.ShouldContain("quota exhausted");
        (await store.GetProjectAnalysesAsync("repo")).Count.ShouldBe(mixed ? 1 : 0);
        var requests = await store.GetBatchRequestsAsync(id);
        requests[0].Status.ShouldBe(mixed ? "succeeded" : "errored");
        requests[0].AttemptCount.ShouldBe(3); // Don't erase provider retry history.
        requests[1].Status.ShouldBe("errored");
        bus.Messages.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingResults_FailParentAndMissingRequest(bool partial)
    {
        var provider = new StubProvider
        {
            Results = partial ? [new("request-0", "succeeded", Valid, "model")] : []
        };
        var (service, store, _, id) = await CreateAsync(provider, 2);
        await service.ProcessCompletedBatchesAsync();

        var batch = (await store.GetLatestBatchAsync("repo"))!;
        batch.Status.ShouldBe("failed");
        batch.CompletedCount.ShouldBe(partial ? 1 : 0);
        batch.Error!.ShouldContain("request-1");
        batch.Error!.ShouldContain("no result");
        (await store.GetBatchRequestsAsync(id))[1].Status.ShouldBe("errored");
    }

    [Fact]
    public async Task UnexpectedAndDuplicateResults_FailWithoutInflatingSuccessCount()
    {
        var provider = new StubProvider
        {
            Results = [new("request-0", "succeeded", Valid, "model"), new("request-0", "succeeded", Valid, "model"),
                new("unknown", "errored", null, null, "provider malformed output")]
        };
        var (service, store, _, _) = await CreateAsync(provider);
        await service.ProcessCompletedBatchesAsync();
        var batch = (await store.GetLatestBatchAsync("repo"))!;
        batch.Status.ShouldBe("failed");
        batch.CompletedCount.ShouldBe(1);
        batch.Error!.ShouldContain("Duplicate");
        batch.Error!.ShouldContain("provider malformed output");
    }

    [Fact]
    public async Task InvalidJson_CanRetainSalvageableData_WithoutClaimingSuccess()
    {
        var provider = new StubProvider
        {
            Results = [new("request-0", "succeeded",
                """{"projectSummary":"Useful partial result","confidence":"high","nodes":[{"nodeId":1,"description":"Useful node"},{"nodeId":2,"description":"truncated""", "model")]
        };
        var (service, store, _, _) = await CreateAsync(provider);
        await service.ProcessCompletedBatchesAsync();
        var batch = (await store.GetLatestBatchAsync("repo"))!;
        batch.Status.ShouldBe("failed");
        batch.CompletedCount.ShouldBe(0);
        batch.Error!.ShouldContain("Invalid JSON");
        (await store.GetProjectAnalysesAsync("repo")).Single().Summary.ShouldBe("Useful partial result");
        (await store.GetNodeAnalysisAsync(1)).ShouldNotBeNull();
    }

    [Fact]
    public async Task AllRequestsSucceed_CompletesParentAndPublishesOnce()
    {
        var provider = new StubProvider { Results = [new("request-0", "succeeded", Valid, "model")] };
        var (service, store, bus, _) = await CreateAsync(provider);
        await service.ProcessCompletedBatchesAsync();
        await service.ProcessCompletedBatchesAsync();
        var batch = (await store.GetLatestBatchAsync("repo"))!;
        batch.Status.ShouldBe("completed");
        batch.CompletedCount.ShouldBe(1);
        batch.Error.ShouldBeNull();
        bus.Messages.OfType<ProjectAnalysisResultsProcessed>().Single().CompletedCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellation_IsPropagatedAndLeavesBatchPending(bool downloading)
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new StubProvider();
        Action cancel = () => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); };
        if (downloading) provider.OnResults = cancel;
        else provider.OnStatus = cancel;
        var (service, store, _, _) = await CreateAsync(provider);

        await Should.ThrowAsync<OperationCanceledException>(() => service.ProcessCompletedBatchesAsync(ct: cancellation.Token));

        var batch = (await store.GetLatestBatchAsync("repo"))!;
        batch.Status.ShouldBe("submitted");
        batch.Error.ShouldBeNull();
    }

    [Fact]
    public async Task LateProviderWrites_CannotOverwriteFailedParent()
    {
        var provider = new StubProvider { Results = [new("request-0", "errored", null, null, "original failure")] };
        var (service, store, _, id) = await CreateAsync(provider);
        await service.ProcessCompletedBatchesAsync();
        await store.UpdateBatchStatusAsync(id, "submitted", 1, null);
        await store.UpdateBatchStatusAsync(id, "completed", 1, DateTime.UtcNow);
        var batch = (await store.GetLatestBatchAsync("repo"))!;
        batch.Status.ShouldBe("failed");
        batch.CompletedCount.ShouldBe(0);
        batch.Error!.ShouldContain("original failure");
    }

    [Fact]
    public async Task ManyLargeProviderErrors_StillRecordTerminalFailureWithinTextColumnLimit()
    {
        const int count = 500;
        var provider = new StubProvider
        {
            Results = Enumerable.Range(0, count).Select(i => new AnalysisBatchItemResult(
                $"request-{i}", "errored", null, null, $"provider reason {i}: " + new string('界', 500))).ToList()
        };
        var (service, store, _, _) = await CreateAsync(provider, count);
        await service.ProcessCompletedBatchesAsync();
        var batch = (await store.GetLatestBatchAsync("repo"))!;
        batch.Status.ShouldBe("failed");
        batch.CompletedCount.ShouldBe(0);
        batch.Error!.ShouldStartWith("0/500 requests succeeded.");
        batch.Error.ShouldContain("provider reason 0");
        batch.Error.ShouldEndWith("[additional error details truncated]");
        batch.Error.Length.ShouldBeLessThanOrEqualTo(AnalysisBatchErrorDetails.MaxLength);
        System.Text.Encoding.UTF8.GetByteCount(batch.Error).ShouldBeLessThan(65_535);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task IncompleteLocalRequestPersistence_WaitsEvenForCompletedHostedBatch(int persistedCount)
    {
        var provider = new StubProvider
        {
            Results = [new("request-0", "succeeded", Valid, "model"), new("request-1", "succeeded", Valid, "model")]
        };
        var (service, store, bus, id) = await CreateAsync(provider, requestCount: 2, storedRequestCount: persistedCount);

        await service.ProcessCompletedBatchesAsync();

        var waiting = (await store.GetLatestBatchAsync("repo"))!;
        waiting.Status.ShouldBe("submitted");
        waiting.Error.ShouldBeNull();
        provider.StatusCalls.ShouldBe(0);
        bus.Messages.ShouldBeEmpty();

        await store.CreateBatchRequestsAsync(Enumerable.Range(persistedCount, 2 - persistedCount)
            .Select(i => new AnalysisBatchRequestEntity
            {
                BatchId = id, CustomId = $"request-{i}", NodeLabel = $"Project{i}", Status = "pending"
            }));
        await service.ProcessCompletedBatchesAsync();

        var completed = (await store.GetLatestBatchAsync("repo"))!;
        completed.Status.ShouldBe("completed");
        completed.CompletedCount.ShouldBe(2);
        bus.Messages.OfType<ProjectAnalysisResultsProcessed>().Single().CompletedCount.ShouldBe(2);
    }

    [Fact]
    public async Task ZeroDeclaredRequests_FailsRatherThanWaitingForSubmissionForever()
    {
        var (service, store, _, _) = await CreateAsync(new StubProvider(), requestCount: 0);
        await service.ProcessCompletedBatchesAsync();
        var batch = (await store.GetLatestBatchAsync("repo"))!;
        batch.Status.ShouldBe("failed");
        batch.CompletedCount.ShouldBe(0);
        batch.Error!.ShouldContain("0 stored requests");
    }

    private static async Task<(BatchAnalysisService Service, InMemoryGraphStore Store, RecordingBus Bus, long Id)> CreateAsync(
        StubProvider provider, int requestCount = 1, int? storedRequestCount = null)
    {
        var store = new InMemoryGraphStore();
        var id = await store.CreateAnalysisBatchAsync(new AnalysisBatchEntity
        {
            Repo = "repo", ProviderBatchId = "batch", ProviderName = "stub", Status = "submitted",
            RequestCount = requestCount, SubmittedAt = DateTime.UtcNow
        });
        await store.CreateBatchRequestsAsync(Enumerable.Range(0, storedRequestCount ?? requestCount).Select(i => new AnalysisBatchRequestEntity
        {
            BatchId = id, CustomId = $"request-{i}", NodeLabel = $"Project{i}", Status = "pending", AttemptCount = 3
        }));
        var bus = new RecordingBus();
        return (new BatchAnalysisService(store, new Registry(provider), bus, null!, Options.Create(new AnalysisOptions()),
            new LocalFileSystem(), NullLogger<BatchAnalysisService>.Instance), store, bus, id);
    }

    private sealed class Registry(IAnalysisModelProvider provider) : IAnalysisProviderRegistry
    {
        public IAnalysisModelProvider GetProvider(string? providerName = null) => provider;
    }

    private sealed class RecordingBus : IMessageBus
    {
        public List<object> Messages { get; } = [];
        public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class StubProvider : IAnalysisModelProvider
    {
        public string ProviderName => "stub";
        public AnalysisProviderCapabilities Capabilities => new(true, true, false, true);
        public AnalysisBatchStatusResult Status { get; init; } = new("batch", "completed", true);
        public IReadOnlyList<AnalysisBatchItemResult> Results { get; init; } = [];
        public Exception? StatusException { get; set; }
        public Exception? ResultsException { get; set; }
        public Action? OnStatus { get; set; }
        public Action? OnResults { get; set; }
        public int StatusCalls { get; private set; }
        public Task<AnalysisTextResponse> ExecuteAsync(AnalysisPrompt prompt, AnalysisRequestOptions request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AnalysisBatchSubmissionResult> SubmitBatchAsync(IReadOnlyList<AnalysisBatchRequestItem> items, AnalysisRequestOptions request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AnalysisBatchStatusResult> GetBatchStatusAsync(string batchId, CancellationToken ct = default)
        {
            StatusCalls++;
            OnStatus?.Invoke();
            if (StatusException is not null) throw StatusException;
            return Task.FromResult(Status);
        }
        public Task<IReadOnlyList<AnalysisBatchItemResult>> GetBatchResultsAsync(string batchId, IReadOnlyList<string>? requestIds = null, CancellationToken ct = default)
        {
            OnResults?.Invoke();
            if (ResultsException is not null) throw ResultsException;
            return Task.FromResult(Results);
        }
    }
}
