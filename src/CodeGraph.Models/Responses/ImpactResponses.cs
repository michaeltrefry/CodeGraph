namespace CodeGraph.Models.Responses;

/// <summary>
/// Full blast-radius report for one or more changed nodes.
/// </summary>
public record ImpactReport(
    IReadOnlyList<AffectedNode> ChangedNodes,
    IReadOnlyList<AffectedNode> AffectedNodes,
    IReadOnlyList<CrossRepoImpact> CrossRepoImpacts,
    ImpactSummary Summary)
{
    /// <summary>The commit this answer describes (the default-branch index commit, or the requested SHA).</summary>
    public string? CommitSha { get; init; }

    /// <summary>
    /// Each repository in the report and the commit its nodes describe: the requested SHA for the
    /// pinned repository, the default-branch index commit for every other repository.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Commits { get; init; }

    /// <summary>
    /// For an answer at a branch-overlay commit: default-branch edges from unchanged files into
    /// files changed or deleted at that commit that could not be mapped to the branch version of
    /// their target, and so are not in this report. Only edges this report would have reached are
    /// counted (from one of its nodes, into a file one of its nodes is in, or into the queried name).
    /// Null for default-branch answers.
    /// </summary>
    public int? UnmappedInboundEdges { get; init; }
}

public record AffectedNode(
    long NodeId,
    string Name,
    string QualifiedName,
    string Label,
    string Repository,
    string? DotnetProject,
    string? FilePath,
    int Depth,
    string EdgeType,
    RiskLevel Risk,
    IReadOnlyList<string> RiskFactors,
    bool LowConfidence = false);

public record CrossRepoImpact(
    string SourceRepository,
    string TargetRepository,
    string EdgeType,
    int AffectedNodeCount);

public record ImpactSummary(
    int TotalAffected,
    int CrossRepoCount,
    int CriticalCount,
    int HighCount,
    int MediumCount,
    int LowCount,
    IReadOnlyList<string> AffectedRepositories,
    int LowConfidenceCount = 0);

public enum RiskLevel
{
    Critical,
    High,
    Medium,
    Low
}
