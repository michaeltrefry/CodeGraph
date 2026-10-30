using CodeGraph.Api.Indexer;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Indexer;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class LocalIndexerClientTests
{
    [Fact]
    public async Task DurableJobCalls_ForwardTheirSubmissionKeyToTheLocalOperations()
    {
        var operations = new RecordingOperations();
        var client = new LocalIndexerClient(operations);

        await client.StartReIndexAllAsync("codegraph-jobs", "job-key-1");
        await client.StartDiscoverAsync("codegraph-jobs", new DiscoverRequest(), "job-key-2");
        await client.StartProcessBatchAnalysisAsync("codegraph-jobs", "Repo", "job-key-3");
        await client.StartDreamProjectsAsync("codegraph-jobs", "job-key-4");
        await client.GetRunAsync("codegraph-jobs", 42);

        operations.Calls.ShouldBe([
            "reindex:job-key-1",
            "discover:job-key-2",
            "batch:Repo:job-key-3",
            "dream:job-key-4",
            "run:42"
        ]);
    }

    private sealed class RecordingOperations : IIndexerOperationsService
    {
        public List<string> Calls { get; } = [];

        private Task<IndexerAcceptedResponse> Record(string call)
        {
            Calls.Add(call);
            return Task.FromResult(new IndexerAcceptedResponse("accepted", RunId: 1));
        }

        public Task<IndexerAcceptedResponse> StartDreamProjectsAsync(string username, string? submissionKey, CancellationToken ct = default)
            => Record($"dream:{submissionKey}");

        public Task<IndexerAcceptedResponse> StartReIndexAllAsync(string username, string? submissionKey, CancellationToken ct = default)
            => Record($"reindex:{submissionKey}");
        public Task<IndexerAcceptedResponse> StartDiscoverAsync(string username, DiscoverRequest? request, string? submissionKey, CancellationToken ct = default)
            => Record($"discover:{submissionKey}");
        public Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(string username, string? repo, string? submissionKey, CancellationToken ct = default)
            => Record($"batch:{repo}:{submissionKey}");
        public Task<IndexerRunResponse?> GetRunAsync(long runId, CancellationToken ct = default)
        {
            Calls.Add($"run:{runId}");
            return Task.FromResult<IndexerRunResponse?>(null);
        }

        public Task<IndexerAcceptedResponse> StartReAnalyzeRepositoryAsync(string username, string repo, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(string username, ProcessRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartReIndexAllAsync(string username, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartDiscoverAsync(string username, DiscoverRequest? request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartSyncSchemaAsync(string username, long sourceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(string username, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartLinkAsync(string username, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(string username, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(string username, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(string username, string? repo = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerRunResponse?> CancelRunAsync(long runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IndexerRunResponse>> ListRunsAsync(string? status = null, string? operation = null, int take = 50, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
