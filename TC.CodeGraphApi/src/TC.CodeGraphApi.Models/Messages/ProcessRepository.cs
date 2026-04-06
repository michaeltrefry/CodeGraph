using TC.Common.TcServiceStack.Queue.Abstractions;

namespace TC.CodeGraphApi.Models.Messages;

[TcServiceBusEvent(TcQueueHosts.Enterprise)]
public class ProcessRepository
{
    /// <summary>Short repo name, e.g. "TC.OrdersApi"</summary>
    public string Name { get; set; } = "";

    /// <summary>Absolute local path to the repository root. Optional if GitLabUrl is set.</summary>
    public string Path { get; set; } = "";

    /// <summary>GitLab HTTPS clone URL. If set, the repo will be cloned/fetched into the configured cache directory.</summary>
    public string? GitLabUrl { get; set; }

    /// <summary>GitLab group/namespace path (e.g. "group/subgroup"). Extracted from PathWithNamespace during discovery.</summary>
    public string? GitLabGroup { get; set; }

    /// <summary>Run the graph indexing pipeline</summary>
    public bool ShouldIndex { get; set; }

    /// <summary>Submit an analysis batch to the Anthropic Batches API</summary>
    public bool ShouldAnalyze { get; set; }

    /// <summary>
    /// Compare current HEAD SHA against SyncStateEntity.LastCommitSha.
    /// If they match, skip all processing for this repo.
    /// </summary>
    public bool SkipIfUpToDate { get; set; }

    /// <summary>
    /// Include source code for all classes in the analysis prompt, not just
    /// convention-matched ones (Services, Controllers, Consumers).
    /// Use for repos that don't follow the TC.* project convention.
    /// Source is still capped by the configured token budget.
    /// </summary>
    public bool IncludeAllSource { get; set; }

    /// <summary>
    /// Compute vitals-style codebase health metrics (churn, complexity, coupling, knowledge risk).
    /// Defaults to true — set to false to skip.
    /// </summary>
    public bool ShouldComputeVitals { get; set; } = true;
}
