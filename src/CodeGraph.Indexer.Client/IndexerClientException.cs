using System.Net;

namespace CodeGraph.Indexer.Client;

public sealed class IndexerClientException(HttpStatusCode statusCode, string? errorCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? ErrorCode { get; } = errorCode;
}
