using CodeGraph.Indexer.Client;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Indexer;

namespace CodeGraph.Api.Indexer;

public sealed class RemoteIndexerOperationsService(IIndexerClient indexerClient) : IIndexerOperationsService
{
    public Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(
        string username,
        ProcessRequest request,
        CancellationToken ct = default)
        => indexerClient.StartProcessRepositoriesAsync(username, request, ct);

    public Task<IndexerAcceptedResponse> StartReIndexAllAsync(string username, CancellationToken ct = default)
        => indexerClient.StartReIndexAllAsync(username, ct);

    public Task<IndexerAcceptedResponse> StartDiscoverAsync(
        string username,
        DiscoverRequest? request,
        CancellationToken ct = default)
        => indexerClient.StartDiscoverAsync(username, request, ct);

    public Task<IndexerAcceptedResponse> StartSyncSchemaAsync(
        string username,
        long sourceId,
        CancellationToken ct = default)
        => indexerClient.StartSyncSchemaAsync(username, sourceId, ct);

    public Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(string username, CancellationToken ct = default)
        => indexerClient.StartSyncAllSchemasAsync(username, ct);

    public Task<IndexerAcceptedResponse> StartLinkAsync(string username, CancellationToken ct = default)
        => indexerClient.StartLinkAsync(username, ct);

    public Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(string username, CancellationToken ct = default)
        => indexerClient.StartDetectCommunitiesAsync(username, ct);

    public Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(string username, CancellationToken ct = default)
        => indexerClient.StartLinkAndDetectAsync(username, ct);

    public Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(
        string username,
        string? repo = null,
        CancellationToken ct = default)
        => indexerClient.StartProcessBatchAnalysisAsync(username, repo, ct);

    public Task<IndexerRunResponse?> GetRunAsync(long runId, CancellationToken ct = default)
        => indexerClient.GetRunAsync("system", runId, ct);

    public Task<IReadOnlyList<IndexerRunResponse>> ListRunsAsync(
        string? status = null,
        string? operation = null,
        int take = 50,
        CancellationToken ct = default)
        => indexerClient.ListRunsAsync("system", status, operation, take, ct);
}
