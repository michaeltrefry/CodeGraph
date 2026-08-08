using CodeGraph.Indexer.Client;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Indexer;

namespace CodeGraph.Api.Indexer;

public sealed class RemoteIndexerOperationsService(IIndexerClient indexerClient) : IIndexerOperationsService
{
    public Task<AnalysisBatchResponse?> ReAnalyzeRepositoryAsync(
        string username,
        string repo,
        CancellationToken ct = default)
        => indexerClient.ReAnalyzeRepositoryAsync(username, repo, ct);

    public Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(string username, ProcessRequest request, CancellationToken ct = default)
        => StartProcessRepositoriesAsync(username, request, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartReIndexAllAsync(string username, CancellationToken ct = default)
        => StartReIndexAllAsync(username, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartDiscoverAsync(string username, DiscoverRequest? request, CancellationToken ct = default)
        => StartDiscoverAsync(username, request, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartSyncSchemaAsync(string username, long sourceId, CancellationToken ct = default)
        => StartSyncSchemaAsync(username, sourceId, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(string username, CancellationToken ct = default)
        => StartSyncAllSchemasAsync(username, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartLinkAsync(string username, CancellationToken ct = default)
        => StartLinkAsync(username, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(string username, CancellationToken ct = default)
        => StartDetectCommunitiesAsync(username, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(string username, CancellationToken ct = default)
        => StartLinkAndDetectAsync(username, submissionKey: null, ct);
    public Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(string username, string? repo = null, CancellationToken ct = default)
        => StartProcessBatchAnalysisAsync(username, repo, submissionKey: null, ct);

    public Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(
        string username,
        ProcessRequest request,
        string? submissionKey,
        CancellationToken ct = default)
        => indexerClient.StartProcessRepositoriesAsync(username, request, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartReIndexAllAsync(string username, string? submissionKey, CancellationToken ct = default)
        => indexerClient.StartReIndexAllAsync(username, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartDiscoverAsync(
        string username,
        DiscoverRequest? request,
        string? submissionKey,
        CancellationToken ct = default)
        => indexerClient.StartDiscoverAsync(username, request, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartSyncSchemaAsync(
        string username,
        long sourceId,
        string? submissionKey,
        CancellationToken ct = default)
        => indexerClient.StartSyncSchemaAsync(username, sourceId, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(string username, string? submissionKey, CancellationToken ct = default)
        => indexerClient.StartSyncAllSchemasAsync(username, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartLinkAsync(string username, string? submissionKey, CancellationToken ct = default)
        => indexerClient.StartLinkAsync(username, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(string username, string? submissionKey, CancellationToken ct = default)
        => indexerClient.StartDetectCommunitiesAsync(username, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(string username, string? submissionKey, CancellationToken ct = default)
        => indexerClient.StartLinkAndDetectAsync(username, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(
        string username,
        string? repo,
        string? submissionKey,
        CancellationToken ct = default)
        => indexerClient.StartProcessBatchAnalysisAsync(username, repo, submissionKey, ct);

    public Task<IndexerRunResponse?> GetRunAsync(long runId, CancellationToken ct = default)
        => indexerClient.GetRunAsync("system", runId, ct);

    public Task<IndexerRunResponse?> CancelRunAsync(long runId, CancellationToken ct = default)
        => indexerClient.CancelRunAsync("system", runId, ct);

    public Task<IReadOnlyList<IndexerRunResponse>> ListRunsAsync(
        string? status = null,
        string? operation = null,
        int take = 50,
        CancellationToken ct = default)
        => indexerClient.ListRunsAsync("system", status, operation, take, ct);
}
