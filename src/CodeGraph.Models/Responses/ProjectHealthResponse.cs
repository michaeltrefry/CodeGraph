namespace CodeGraph.Models.Responses;

public record ProjectHealthResponse(
    ProjectHealthSummary? RepoHealth,
    IReadOnlyList<ProjectHealthSummary> ProjectHealths,
    IReadOnlyList<FileMetrics> TopHotspots,
    IReadOnlyList<ProjectHealthAnalysis> Analyses,
    ProjectSecuritySummary? SecuritySummary = null,
    DotnetSupportInfo? DotnetSupport = null,
    RepositoryVitalitySummary? RepositoryVitality = null);

public enum HistoryMaturity
{
    Young,
    Growing,
    Mature
}

public record ProjectHealthSummary(
    long Id,
    string Project,
    string? DotnetProject,
    double OverallHealth,
    int TotalFiles,
    int HotspotCount,
    int AlertCount,
    string? TopHotspots,
    DateTime ComputedAt,
    double BaseOverallHealth = 0,
    double ScorePenalty = 0,
    HistoryMaturity? HistoryMaturity = null);

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
    DateTime ComputedAt,
    double ConcernScore = 0,
    double Churn30d = 0,
    double Churn90d = 0,
    double Churn365d = 0,
    double BugFixCommits90d = 0,
    double BugFixCommits365d = 0,
    double BugFixRatio365d = 0,
    double BugFixWeightedTouches365d = 0,
    double RecurringChurnScore = 0);

public record RepositoryVitalitySummary(
    HistoryMaturity? HistoryMaturity,
    bool HasSufficientHistoryForTrends,
    string? ActivityStatus,
    string? FirefightingStatus,
    IReadOnlyList<MonthlyCommitPoint> MonthlyCommits,
    int VelocityLast6Months,
    int VelocityPrior6Months,
    double VelocityChangePercent,
    int DormantMonths12m,
    int MaxInactiveStreakMonths,
    int FirefightingCommits90d,
    int FirefightingCommits365d,
    double FirefightingRate90d,
    double FirefightingRate365d);

public record MonthlyCommitPoint(
    string Month,
    int CommitCount);

public record ProjectHealthAnalysis(
    long Id,
    string Project,
    string? DotnetProject,
    string Analysis,
    string Confidence,
    string? ModelUsed,
    DateTime CreatedAt,
    DateTime UpdatedAt);
