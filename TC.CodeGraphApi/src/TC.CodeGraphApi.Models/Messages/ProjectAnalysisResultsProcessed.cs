using TC.Common.TcServiceStack.Queue.Abstractions;

namespace TC.CodeGraphApi.Models.Messages;

/// <summary>
/// Published after per-project analysis results have been stored from a completed batch.
/// Triggers repo-level synthesis independently of result storage.
/// </summary>
[TcServiceBusEvent(TcQueueHosts.Enterprise)]
public class ProjectAnalysisResultsProcessed
{
    public string RepoName { get; set; } = "";
    public string AnthropicBatchId { get; set; } = "";
    public int CompletedCount { get; set; }
}
