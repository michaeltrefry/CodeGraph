using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Indexer;

public interface IIndexerOperationsService
{
    Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(string username, ProcessRequest request, CancellationToken ct = default);
    Task<IndexerAcceptedResponse> StartReIndexAllAsync(string username, CancellationToken ct = default);
    Task<IndexerAcceptedResponse> StartDiscoverAsync(string username, DiscoverRequest? request, CancellationToken ct = default);
    Task<IndexerAcceptedResponse> StartSyncSchemaAsync(string username, long sourceId, CancellationToken ct = default);
    Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(string username, CancellationToken ct = default);
    Task<IndexerAcceptedResponse> StartLinkAsync(string username, CancellationToken ct = default);
    Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(string username, CancellationToken ct = default);
    Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(string username, CancellationToken ct = default);
    Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(string username, string? repo = null, CancellationToken ct = default);

    Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(string username, ProcessRequest request, string? submissionKey, CancellationToken ct = default)
        => throw new NotSupportedException("This indexer implementation does not support durable submission identities.");
    Task<IndexerAcceptedResponse> StartReIndexAllAsync(string username, string? submissionKey, CancellationToken ct = default)
        => throw new NotSupportedException("This indexer implementation does not support durable submission identities.");
    Task<IndexerAcceptedResponse> StartDiscoverAsync(string username, DiscoverRequest? request, string? submissionKey, CancellationToken ct = default)
        => throw new NotSupportedException("This indexer implementation does not support durable submission identities.");
    Task<IndexerAcceptedResponse> StartSyncSchemaAsync(string username, long sourceId, string? submissionKey, CancellationToken ct = default)
        => StartSyncSchemaAsync(username, sourceId, ct);
    Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(string username, string? submissionKey, CancellationToken ct = default)
        => StartSyncAllSchemasAsync(username, ct);
    Task<IndexerAcceptedResponse> StartLinkAsync(string username, string? submissionKey, CancellationToken ct = default)
        => StartLinkAsync(username, ct);
    Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(string username, string? submissionKey, CancellationToken ct = default)
        => StartDetectCommunitiesAsync(username, ct);
    Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(string username, string? submissionKey, CancellationToken ct = default)
        => StartLinkAndDetectAsync(username, ct);
    Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(string username, string? repo, string? submissionKey, CancellationToken ct = default)
        => throw new NotSupportedException("This indexer implementation does not support durable submission identities.");
    Task<IndexerRunResponse?> GetRunAsync(long runId, CancellationToken ct = default);
    Task<IndexerRunResponse?> CancelRunAsync(long runId, CancellationToken ct = default);
    Task<IReadOnlyList<IndexerRunResponse>> ListRunsAsync(
        string? status = null,
        string? operation = null,
        int take = 50,
        CancellationToken ct = default);
}
