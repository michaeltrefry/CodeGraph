using TC.Common.TcServiceStack.Queue.Abstractions;

namespace TC.CodeGraphApi.Models.Messages;

/// <summary>
/// Published after repo-level synthesis has been completed (or skipped if no project analyses exist).
/// Triggers CODEGRAPH.md generation independently of synthesis.
/// </summary>
[TcServiceBusEvent(TcQueueHosts.Enterprise)]
public class AnalysisSynthesisCompleted
{
    public string RepoName { get; set; } = "";
    public string AnthropicBatchId { get; set; } = "";
}
