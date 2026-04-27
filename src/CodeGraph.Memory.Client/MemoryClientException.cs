using System.Net;

namespace CodeGraph.Memory.Client;

public sealed class MemoryClientException : Exception
{
    public MemoryClientException(HttpStatusCode statusCode, string? errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }
    public string? ErrorCode { get; }
}
