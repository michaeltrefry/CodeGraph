namespace CodeGraph.Services.Extractors;

/// <summary>
/// Signals that a repository containing Rust source could not be semantically indexed.
/// This is intentionally fatal so an indexer run cannot report success with a silently
/// degraded Tree-sitter-only graph.
/// </summary>
public sealed class RustSemanticIndexingException : Exception
{
    public string FailureCode { get; }

    public RustSemanticIndexingException(string failureCode, string message)
        : base(message)
    {
        FailureCode = failureCode;
    }

    public RustSemanticIndexingException(string failureCode, string message, Exception innerException)
        : base(message, innerException)
    {
        FailureCode = failureCode;
    }
}
