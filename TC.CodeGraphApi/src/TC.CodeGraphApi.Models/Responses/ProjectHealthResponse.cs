namespace TC.CodeGraphApi.Models.Responses;

public record ProjectHealthResponse(
    ProjectHealthSummary? RepoHealth,
    IReadOnlyList<ProjectHealthSummary> ProjectHealths,
    IReadOnlyList<FileMetrics> TopHotspots,
    IReadOnlyList<ProjectHealthAnalysis> Analyses,
    ProjectSecuritySummary? SecuritySummary = null);

public record ProjectHealthSummary(
    long Id,
    string Project,
    string? DotnetProject,
    double OverallHealth,
    int TotalFiles,
    int HotspotCount,
    int AlertCount,
    string? TopHotspots,
    DateTime ComputedAt);

public record FileMetrics(
    long Id,
    string Project,
    string FilePath,
    string? DotnetProject,
    int Changes,
    int LinesAdded,
    int LinesRemoved,
    int AuthorCount,
    DateTime? LastChangeAt,
    int ComplexityScore,
    int MaxNestingDepth,
    int DeepNestingLines,
    int FunctionCount,
    int LongestFunction,
    int LintErrors,
    int LintWarnings,
    double TrustScore,
    double MaxCouplingStrength,
    int CouplingPartners,
    int TruckFactor,
    string? TopAuthors,
    double HealthScore,
    string Role,
    double RiskScore,
    DateTime ComputedAt);

public record ProjectHealthAnalysis(
    long Id,
    string Project,
    string? DotnetProject,
    string Analysis,
    string Confidence,
    string? ModelUsed,
    DateTime CreatedAt,
    DateTime UpdatedAt);
