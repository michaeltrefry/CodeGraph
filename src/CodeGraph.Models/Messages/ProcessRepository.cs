namespace CodeGraph.Models.Messages;

public class ProcessRepository
{
    /// <summary>Short repo name, e.g. "orders-api"</summary>
    public string Name { get; set; } = "";

    /// <summary>Absolute local path to the repository root. Optional if RepoUrl is set.</summary>
    public string Path { get; set; } = "";

    /// <summary>HTTPS clone URL. If set, the repo will be cloned/fetched into the configured cache directory.</summary>
    public string? RepoUrl { get; set; }

    /// <summary>Source group/namespace path (e.g. "group/subgroup"). Extracted from PathWithNamespace during discovery.</summary>
    public string? SourceGroup { get; set; }

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
    /// Source is still capped by the configured token budget.
    /// </summary>
    public bool IncludeAllSource { get; set; }

    /// <summary>
    /// Compute vitals-style codebase health metrics (churn, complexity, coupling, knowledge risk).
    /// Defaults to true — set to false to skip.
    /// </summary>
    public bool ShouldComputeVitals { get; set; } = true;
}
