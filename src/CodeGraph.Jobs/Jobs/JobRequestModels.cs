namespace CodeGraph.Jobs.Jobs;

public sealed record EmptyJobRequest;

public sealed class ProcessBatchAnalysisJobRequest
{
    public string? Repo { get; set; }
}
