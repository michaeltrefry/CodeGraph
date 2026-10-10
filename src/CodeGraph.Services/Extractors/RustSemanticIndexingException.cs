namespace CodeGraph.Services.Extractors;

/// <summary>
/// Signals that a Cargo workspace could not be semantically indexed. The indexing pipeline
/// does not fail the run on it: the workspace's files fall back to low-confidence
/// tree-sitter extraction and the failure is recorded with a
/// <see cref="RustSemanticFailureCategory"/> code.
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
