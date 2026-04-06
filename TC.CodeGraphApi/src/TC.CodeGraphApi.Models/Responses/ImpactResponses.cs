namespace TC.CodeGraphApi.Models.Responses;

/// <summary>
/// Full blast-radius report for one or more changed nodes.
/// </summary>
public record ImpactReport(
    IReadOnlyList<AffectedNode> ChangedNodes,
    IReadOnlyList<AffectedNode> AffectedNodes,
    IReadOnlyList<CrossRepoImpact> CrossRepoImpacts,
    ImpactSummary Summary);

public record AffectedNode(
    long NodeId,
    string Name,
    string QualifiedName,
    string Label,
    string Project,
    string? DotnetProject,
    string? FilePath,
    int Depth,
    string EdgeType,
    RiskLevel Risk,
    IReadOnlyList<string> RiskFactors);

public record CrossRepoImpact(
    string SourceProject,
    string TargetProject,
    string EdgeType,
    int AffectedNodeCount);

public record ImpactSummary(
    int TotalAffected,
    int CrossRepoCount,
    int CriticalCount,
    int HighCount,
    int MediumCount,
    int LowCount,
    IReadOnlyList<string> AffectedProjects);

public enum RiskLevel
{
    Critical,
    High,
    Medium,
    Low
}
