namespace TC.CodeGraphApi.Models.Exceptions;

public class DoNotRetryException : Exception
{
    public DoNotRetryException(string message) : base(message)
    {
    }

    public DoNotRetryException(string message, Exception innerException) : base(message, innerException)
    {
    }
}