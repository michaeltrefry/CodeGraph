using CodeGraph.Indexer.Client;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Indexer;

namespace CodeGraph.Api.Indexer;

/// <summary>
/// <see cref="IIndexerClient"/> for an API host that runs the indexer in-process
/// (<c>Indexer:BaseUrl</c> empty): the scheduled jobs and their dispatcher queue work through the
/// local <see cref="IIndexerOperationsService"/> instead of calling a remote indexer host.
/// </summary>
public sealed class LocalIndexerClient(IIndexerOperationsService operations) : IIndexerClient
{
    public Task<IndexerAcceptedResponse> StartReAnalyzeRepositoryAsync(
        string username, string repo, string? submissionKey = null, CancellationToken ct = default)
        => operations.StartReAnalyzeRepositoryAsync(username, repo, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(
        string username, ProcessRequest request, CancellationToken ct = default)
        => operations.StartProcessRepositoriesAsync(username, request, ct);

    public Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(
        string username, ProcessRequest request, string? submissionKey, CancellationToken ct = default)
        => operations.StartProcessRepositoriesAsync(username, request, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartReIndexAllAsync(string username, CancellationToken ct = default)
        => operations.StartReIndexAllAsync(username, ct);

    public Task<IndexerAcceptedResponse> StartReIndexAllAsync(
        string username, string? submissionKey, CancellationToken ct = default)
        => operations.StartReIndexAllAsync(username, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartDiscoverAsync(
        string username, DiscoverRequest? request = null, CancellationToken ct = default)
        => operations.StartDiscoverAsync(username, request, ct);

    public Task<IndexerAcceptedResponse> StartDiscoverAsync(
        string username, DiscoverRequest? request, string? submissionKey, CancellationToken ct = default)
        => operations.StartDiscoverAsync(username, request, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartSyncSchemaAsync(string username, long sourceId, CancellationToken ct = default)
        => operations.StartSyncSchemaAsync(username, sourceId, ct);

    public Task<IndexerAcceptedResponse> StartSyncSchemaAsync(
        string username, long sourceId, string? submissionKey, CancellationToken ct = default)
        => operations.StartSyncSchemaAsync(username, sourceId, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(string username, CancellationToken ct = default)
        => operations.StartSyncAllSchemasAsync(username, ct);

    public Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(
        string username, string? submissionKey, CancellationToken ct = default)
        => operations.StartSyncAllSchemasAsync(username, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartLinkAsync(string username, CancellationToken ct = default)
        => operations.StartLinkAsync(username, ct);

    public Task<IndexerAcceptedResponse> StartLinkAsync(
        string username, string? submissionKey, CancellationToken ct = default)
        => operations.StartLinkAsync(username, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(string username, CancellationToken ct = default)
        => operations.StartDetectCommunitiesAsync(username, ct);

    public Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(
        string username, string? submissionKey, CancellationToken ct = default)
        => operations.StartDetectCommunitiesAsync(username, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(string username, CancellationToken ct = default)
        => operations.StartLinkAndDetectAsync(username, ct);

    public Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(
        string username, string? submissionKey, CancellationToken ct = default)
        => operations.StartLinkAndDetectAsync(username, submissionKey, ct);

    public Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(
        string username, string? repo = null, CancellationToken ct = default)
        => operations.StartProcessBatchAnalysisAsync(username, repo, ct);

    public Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(
        string username, string? repo, string? submissionKey, CancellationToken ct = default)
        => operations.StartProcessBatchAnalysisAsync(username, repo, submissionKey, ct);

    public Task<BranchOverlayResponse> StartBranchOverlayAsync(
        string username, BranchOverlayRequest request, CancellationToken ct = default)
        => operations.StartBranchOverlayAsync(username, request, ct);

    public Task<BranchOverlayResponse?> GetBranchOverlayAsync(
        string username, long overlayId, CancellationToken ct = default)
        => operations.GetBranchOverlayAsync(overlayId, ct);

    public Task<IndexerRunResponse?> GetRunAsync(string username, long runId, CancellationToken ct = default)
        => operations.GetRunAsync(runId, ct);

    public Task<IndexerRunResponse?> CancelRunAsync(string username, long runId, CancellationToken ct = default)
        => operations.CancelRunAsync(runId, ct);

    public Task<IReadOnlyList<IndexerRunResponse>> ListRunsAsync(
        string username,
        string? status = null,
        string? operation = null,
        int take = 50,
        CancellationToken ct = default)
        => operations.ListRunsAsync(status, operation, take, ct);
}
