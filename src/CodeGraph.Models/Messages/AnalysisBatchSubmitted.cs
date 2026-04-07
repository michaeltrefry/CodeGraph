namespace CodeGraph.Models.Messages;

/// <summary>
/// Published after an analysis batch has been queued with the active provider flow.
/// Can trigger immediate processing instead of waiting for the scheduled job.
/// </summary>
public class AnalysisBatchSubmitted
{
    public string RepoName { get; set; } = "";
    public string ProviderBatchId { get; set; } = "";
    public int RequestCount { get; set; }
}
