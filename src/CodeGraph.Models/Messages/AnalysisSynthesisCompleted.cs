namespace CodeGraph.Models.Messages;

/// <summary>
/// Published after repo-level synthesis has been completed (or skipped if no project analyses exist).
/// Triggers CODEGRAPH.md generation independently of synthesis.
/// </summary>
public class AnalysisSynthesisCompleted
{
    public string RepoName { get; set; } = "";
    public string ProviderBatchId { get; set; } = "";
}
