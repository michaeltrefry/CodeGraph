using CodeGraph.Data;
using CodeGraph.Models;

namespace CodeGraph.Services.Models;

public record RepoAnalysis(
    string Summary,
    ConfidenceLevel Confidence,
    string ModelUsed,
    IReadOnlyList<ProjectAnalysis> Projects)
{
    public IReadOnlyDictionary<string, object>? RepositoryProperties { get; init; }
}

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

/// <summary>Parsed response for a repo-level batch request.</summary>
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

/// <summary>Parsed response for a per-project batch request.</summary>
public record ProjectAnalysisResult(
    string ProjectSummary,
    string Confidence,
    List<NodeAnalysisItem> Nodes);
