namespace CodeGraph.Models.Responses;

public record SecurityFinding(
    string Category,
    string Severity,
    string Title,
    string Description,
    string? FilePath,
    int? LineNumber,
    string? Package,
    string? PackageVersion,
    string? Advisory);

public record SecurityScanResult(
    double SecurityScore,
    int CriticalCount,
    int HighCount,
    int MediumCount,
    int LowCount,
    IReadOnlyList<SecurityFinding> Findings);

public record ProjectSecurityResponse(
    string Project,
    double SecurityScore,
    int CriticalCount,
    int HighCount,
    int MediumCount,
    int LowCount,
    IReadOnlyList<SecurityFinding> Findings,
    string? Analysis,
    DateTime ComputedAt);

public record ProjectSecuritySummary(
    double SecurityScore,
    int CriticalCount,
    int HighCount,
    int MediumCount,
    int LowCount,
    DateTime ComputedAt);
