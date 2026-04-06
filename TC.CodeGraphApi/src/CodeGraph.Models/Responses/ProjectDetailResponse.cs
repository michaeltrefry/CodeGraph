namespace CodeGraph.Models.Responses;

public record ProjectDetailResponse(
    ProjectListItem Project,
    ProjectSummaryResponse? Summary,
    IReadOnlyList<ProjectAnalysisResponse> Analyses,
    Dictionary<string, int> NodeCounts,
    Dictionary<string, Dictionary<string, int>> DotnetProjects,
    int InboundEdgeCount,
    int OutboundEdgeCount,
    IReadOnlyList<string> InboundProjects,
    IReadOnlyList<string> OutboundProjects,
    ProjectHealthSummary? Health);

public record ProjectSummaryResponse(
    string Project,
    string Summary,
    string Confidence,
    string SourceHash,
    string? ModelUsed,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record ProjectAnalysisResponse(
    string Repo,
    string ProjectName,
    string Summary,
    string Confidence,
    IReadOnlyList<EndpointResponse> Endpoints,
    IReadOnlyList<ServiceResponse> Services,
    IReadOnlyList<string> ExternalDependencies,
    IReadOnlyList<string> DatabaseTables,
    string? ModelUsed,
    DateTime UpdatedAt);

public record EndpointResponse(
    string Route,
    string HttpMethod,
    string Description,
    string? RequestModel,
    string? ResponseModel);

public record ServiceResponse(
    string Name,
    string Description,
    string? InterfaceName,
    string Lifetime);
