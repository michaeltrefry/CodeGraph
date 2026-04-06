using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Services.Models;

public record RepoAnalysis(
    string Summary,
    ConfidenceLevel Confidence,
    string ModelUsed,
    IReadOnlyList<ProjectAnalysis> Projects);

// Uses Data-layer StoredEndpoint/StoredService so results persist without mapping.
public record ProjectAnalysis(
    string ProjectName,
    string Summary,
    ConfidenceLevel Confidence,
    IReadOnlyList<StoredEndpoint> Endpoints,
    IReadOnlyList<StoredService> Services,
    IReadOnlyList<string> ExternalDependencies,
    IReadOnlyList<string> DatabaseTables);

public record AnalysisUpdate(
    string UpdatedSummary,
    ConfidenceLevel Confidence,
    string ChangeDescription);

/// <summary>Claude's parsed response for a repo-level batch request.</summary>
public record RepoAnalysisResult(
    string RepoSummary,
    string Confidence,
    List<ProjectSummaryItem> Projects,
    List<NodeAnalysisItem> Nodes);

public record ProjectSummaryItem(
    string ProjectName,
    string Summary,
    string Confidence);

public record NodeAnalysisItem(
    long NodeId,
    string Description,
    string Confidence);

/// <summary>Claude's parsed response for a per-project batch request.</summary>
public record ProjectAnalysisResult(
    string ProjectSummary,
    string Confidence,
    List<NodeAnalysisItem> Nodes);
