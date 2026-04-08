namespace CodeGraph.Models.Exceptions;

public class RetryableAnalysisException : Exception
{
    public RetryableAnalysisException(string message) : base(message)
    {
    }

    public RetryableAnalysisException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
