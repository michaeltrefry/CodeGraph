using Microsoft.Extensions.Logging.Abstractions;
using TC.CodeGraphApi.Models.Requests;
using TC.CodeGraphApi.Models.Responses;
using TC.CodeGraphApi.Services.Analyzers;
using TC.CodeGraphJobs.Jobs;
using TC.Common.Http;
using TC.Common.TcServiceStack.Gateway.Abstractions;
using TC.Common.TcServiceStack.Queue.Abstractions;
using TC.JobUtilities;
using IReturns = TC.Common.TcServiceStack.Gateway.Abstractions.IReturns;

namespace TC.CodeGraphJobs.Tests.Jobs;

internal sealed class TestProcessRepositoriesJob(ITcServiceBus serviceBus)
    : ProcessRepositoriesJob(NullLogger<ProcessRepositoriesJob>.Instance, serviceBus, Guid.NewGuid())
{
    public Task InvokeAsync(StartJob startJob) => ExecuteAsync(startJob);
}

internal sealed class TestProcessBatchResultsJob(ITcServiceBus serviceBus, IBatchAnalysisService batchService)
    : ProcessBatchResultsJob(NullLogger<ProcessBatchResultsJob>.Instance, serviceBus, batchService, Guid.NewGuid())
{
    public Task InvokeAsync(StartJob startJob) => ExecuteAsync(startJob);
}

internal sealed class TestDiscoverRepositoriesJob(ITcGateway gateway, ITcServiceBus serviceBus)
    : DiscoverRepositoriesJob(NullLogger<DiscoverRepositoriesJob>.Instance, gateway, serviceBus, Guid.NewGuid())
{
    public Task InvokeAsync(StartJob startJob) => ExecuteAsync(startJob);
}

internal sealed class RecordingServiceBus : ITcServiceBus
{
    public List<object> PublishedMessages { get; } = [];
    public List<(TcQueueHosts VirtualHost, object Message)> PublishedToVirtualHost { get; } = [];
    public List<(TcQueueHosts QueueHost, string QueueName, object Command)> SentCommands { get; } = [];

    public Task<TcPublishResponse> Publish<T>(T message, bool sendNow = false) where T : class
    {
        PublishedMessages.Add(message);
        return Task.FromResult(new TcPublishResponse());
    }

    public Task<TcPublishResponse> PublishToVirtualHost<T>(T message, TcQueueHosts virtualHost, bool sendNow = false) where T : class
    {
        PublishedToVirtualHost.Add((virtualHost, message));
        return Task.FromResult(new TcPublishResponse());
    }

    public Task SendCommandToCustomQueue<T>(T command, TcQueueHosts queueHost, string queueName, bool sendNow = false) where T : class
    {
        SentCommands.Add((queueHost, queueName, command));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingBatchAnalysisService : IBatchAnalysisService
{
    public string? ProcessedRepo { get; private set; }
    public int ProcessCompletedCalls { get; private set; }

    public Task SubmitAnalysisBatchAsync(string repoName, string? repoPath = null, bool includeAllSource = false, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ProcessCompletedBatchesAsync(string? repo = null, CancellationToken ct = default)
    {
        ProcessedRepo = repo;
        ProcessCompletedCalls++;
        return Task.CompletedTask;
    }

    public Task SynthesizeRepoSummaryAsync(string repoName, string batchId, CancellationToken ct) => Task.CompletedTask;

    public Task WriteCodeGraphDocsAsync(string repoName, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeGatewayResponse<T>
{
    public required bool Success { get; init; }
    public T? Result { get; init; }
    public Exception? Exception { get; init; }
}

internal sealed class RecordingTcGateway : ITcGateway
{
    public DiscoverRequest? LastDiscoverRequest { get; private set; }
    public FakeGatewayResponse<DiscoverResponse> NextDiscoverResponse { get; set; } = new()
    {
        Success = true,
        Result = new DiscoverResponse(0, 0, 0, 0, 0, [])
    };

    public Common.Http.TcResponse Send(IReturns request)
        => throw new NotSupportedException();

    public TcResponse Send(IReturns request, Action<GatewayOptions> setupAction)
        => throw new NotSupportedException();

    public Task<TcResponse> SendAsync(IReturns request)
        => throw new NotSupportedException();

    public Task<TcResponse> SendAsync(IReturns request, Action<GatewayOptions> setupAction)
        => throw new NotSupportedException();

    public TcResponse<TResponse> Send<TResponse>(IReturns<TResponse> request) => throw new NotSupportedException();

    public TcResponse<TResponse> Send<TResponse>(IReturns<TResponse> request, Action<GatewayOptions> setupAction) => throw new NotSupportedException();

    public Task<TcResponse<TResponse>> SendAsync<TResponse>(IReturns<TResponse> request)
    {
        LastDiscoverRequest = request as DiscoverRequest;
        return Task.FromResult(CreateResponse<TResponse>());
    }

    public Task<TcResponse<TResponse>> SendAsync<TResponse>(IReturns<TResponse> request, Action<GatewayOptions> setupAction)
    {
        LastDiscoverRequest = request as DiscoverRequest;
        return Task.FromResult(CreateResponse<TResponse>());
    }

    public TcResponse SendToService(HttpMethod method, string serviceName, string path, IReturns request)
        => throw new NotSupportedException();

    public TcResponse SendToService(HttpMethod method, string serviceName, string path, IReturns request, Action<GatewayOptions> setupAction)
        => throw new NotSupportedException();

    public Task<TcResponse> SendToServiceAsync(HttpMethod method, string serviceName, string path, IReturns request)
        => throw new NotSupportedException();

    public Task<TcResponse> SendToServiceAsync(HttpMethod method, string serviceName, string path, IReturns request, Action<GatewayOptions> setupAction)
        => throw new NotSupportedException();

    public TcResponse<TResponse> SendToService<TResponse>(HttpMethod method, string serviceName, string path, IReturns<TResponse> request) => throw new NotSupportedException();

    public TcResponse<TResponse> SendToService<TResponse>(HttpMethod method, string serviceName, string path, IReturns<TResponse> request, Action<GatewayOptions> setupAction) => throw new NotSupportedException();

    public Task<TcResponse<TResponse>> SendToServiceAsync<TResponse>(HttpMethod method, string serviceName, string path, IReturns<TResponse> request) => throw new NotSupportedException();

    public Task<TcResponse<TResponse>> SendToServiceAsync<TResponse>(HttpMethod method, string serviceName, string path, IReturns<TResponse> request, Action<GatewayOptions> setupAction) => throw new NotSupportedException();

    private TcResponse<TResponse> CreateResponse<TResponse>()
    {
        if (typeof(TResponse) != typeof(DiscoverResponse))
            throw new NotSupportedException($"Unsupported response type {typeof(TResponse).FullName}");

        var response = NextDiscoverResponse.Success
            ? new TcResponse<TResponse>((TResponse)(object)(NextDiscoverResponse.Result ?? new DiscoverResponse(0, 0, 0, 0, 0, [])))
            : new TcResponse<TResponse>();

        response.Exception = NextDiscoverResponse.Exception;
        return response;
    }
}
