using System.Net;
using CodeGraph.Indexer.Client;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Indexer;
using CodeGraph.Services.Overlays;

namespace CodeGraph.Api.Indexer;

public sealed class RemoteIndexerOperationsService(IIndexerClient indexerClient) : IIndexerOperationsService
{
    public Task<IndexerAcceptedResponse> StartReAnalyzeRepositoryAsync(
        string username,
        string repo,
        CancellationToken ct = default)
        => indexerClient.StartReAnalyzeRepositoryAsync(username, repo, submissionKey: null, ct);

    public Task<IndexerAcceptedResponse> StartReAnalyzeRepositoryAsync(
        string username,
        string repo,
        string? submissionKey,
        CancellationToken ct = default)
        => indexerClient.StartReAnalyzeRepositoryAsync(username, repo, submissionKey, ct);

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

    public Task<BranchOverlayResponse> StartBranchOverlayAsync(
        string username,
        BranchOverlayRequest request,
        CancellationToken ct = default)
        => request.ReuseLive
            ? StartReusableBranchOverlayAsync(username, request, ct)
            : indexerClient.StartBranchOverlayAsync(username, request, ct);

    /// <summary>
    /// Reuse requests come from MCP tools, which answer typed errors from the same exceptions the
    /// standalone service throws, so the indexer host's error responses are mapped back to them.
    /// (Plain REST requests keep the pass-through error handling of IndexerClientExceptionMiddleware.)
    /// </summary>
    private async Task<BranchOverlayResponse> StartReusableBranchOverlayAsync(
        string username,
        BranchOverlayRequest request,
        CancellationToken ct)
    {
        try
        {
            return await indexerClient.StartBranchOverlayAsync(username, request, ct);
        }
        catch (IndexerClientException ex)
        {
            throw ex.StatusCode switch
            {
                HttpStatusCode.NotFound => new KeyNotFoundException(ex.Message, ex),
                HttpStatusCode.BadRequest => new ArgumentException(ex.Message, ex),
                HttpStatusCode.NotImplemented => new NotSupportedException(ex.Message, ex),
                HttpStatusCode.ServiceUnavailable => new BranchOverlayTransientException(ex.Message, ex),
                _ => ex
            };
        }
    }

    public Task<BranchOverlayResponse?> GetBranchOverlayAsync(long overlayId, CancellationToken ct = default)
        => indexerClient.GetBranchOverlayAsync("system", overlayId, ct);

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
