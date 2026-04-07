using Microsoft.Extensions.Logging.Abstractions;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Messaging;
using CodeGraph.Jobs.Jobs;

namespace CodeGraph.Jobs.Tests.Jobs;

internal sealed class TestProcessRepositoriesJob(IMessageBus messageBus)
    : ProcessRepositoriesJob(messageBus, NullLogger<ProcessRepositoriesJob>.Instance)
{
    public Task InvokeAsync(StartJob startJob) => ExecuteAsync(startJob);
}

internal sealed class TestProcessBatchResultsJob(IBatchAnalysisService batchService)
    : ProcessBatchResultsJob(batchService)
{
    public Task InvokeAsync(StartJob startJob) => ExecuteAsync(startJob);
}

internal sealed class TestDiscoverRepositoriesJob(IAdminService adminService)
    : DiscoverRepositoriesJob(adminService, NullLogger<DiscoverRepositoriesJob>.Instance)
{
    public Task InvokeAsync(StartJob startJob) => ExecuteAsync(startJob);
}

internal sealed class RecordingMessageBus : IMessageBus
{
    public List<object> PublishedMessages { get; } = [];

    public Task PublishAsync<T>(T message, CancellationToken ct = default) where T : class
    {
        PublishedMessages.Add(message);
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

internal sealed class RecordingAdminService : IAdminService
{
    public DiscoverRequest? LastDiscoverRequest { get; private set; }
    public DiscoverResponse NextDiscoverResponse { get; set; } = new(0, 0, 0, 0, 0, []);

    public Task<DiscoverResponse> DiscoverAsync(DiscoverRequest? request)
    {
        LastDiscoverRequest = request;
        return Task.FromResult(NextDiscoverResponse);
    }

    public Task<ProcessReposResponse> ProcessRepositoriesAsync(ProcessRequest request) => throw new NotSupportedException();
    public Task<ProcessReposResponse> ReIndexAllAsync() => throw new NotSupportedException();
    public Task LinkAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task DetectCommunitiesAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task LinkAndDetectAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task ProcessBatchAnalysisAsync(string? repo) => throw new NotSupportedException();
}
