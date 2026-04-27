namespace CodeGraph.Models.Requests;

public class AdminReportQueryRequest
{
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
    public string? Interval { get; init; }
    public string? User { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Tool { get; init; }
}
