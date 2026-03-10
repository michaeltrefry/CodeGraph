using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Services.Models;

public record RepoAnalysis(
    string Summary,
    ConfidenceLevel Confidence,
    string ModelUsed,
    IReadOnlyList<ProjectAnalysis> Projects);

public record ProjectAnalysis(
    string ProjectName,
    string Summary,
    ConfidenceLevel Confidence,
    IReadOnlyList<EndpointDescription> Endpoints,
    IReadOnlyList<ServiceDescription> Services,
    IReadOnlyList<string> ExternalDependencies,
    IReadOnlyList<string> DatabaseTables);

public record EndpointDescription(
    string Route,
    string HttpMethod,
    string Description,
    string? RequestModel,
    string? ResponseModel);

public record ServiceDescription(
    string Name,
    string Description,
    string? InterfaceName,
    string Lifetime);

public record AnalysisUpdate(
    string UpdatedSummary,
    ConfidenceLevel Confidence,
    string ChangeDescription);
