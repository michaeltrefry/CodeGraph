using TC.Common.TcServiceStack.Queue.Abstractions;

namespace TC.CodeGraphApi.Models.Messages;

/// <summary>
/// Published after an analysis batch has been submitted to the Anthropic Batches API.
/// Can trigger immediate polling instead of waiting for the scheduled job.
/// </summary>
[TcServiceBusEvent(TcQueueHosts.Enterprise)]
public class AnalysisBatchSubmitted
{
    public string RepoName { get; set; } = "";
    public string AnthropicBatchId { get; set; } = "";
    public int RequestCount { get; set; }
}
