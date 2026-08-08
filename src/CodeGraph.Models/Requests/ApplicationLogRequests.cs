namespace CodeGraph.Models.Requests;

public sealed class AdminApplicationLogQueryRequest
{
    public int Page { get; init; } = 1;
    public string? Container { get; init; }
    public string? Level { get; init; }
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public string? Search { get; init; }
}

public sealed class ClientErrorReportRequest
{
    public string Message { get; init; } = string.Empty;
    public string? Stack { get; init; }
    public string? Url { get; init; }
    public string? UserAgent { get; init; }
}
