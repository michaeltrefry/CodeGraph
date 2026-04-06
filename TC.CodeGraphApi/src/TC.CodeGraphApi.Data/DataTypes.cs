using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Data;

public enum TraceDirection { Outbound, Inbound, Both }

public record TraversalEntry(
    GraphNode Node,
    int Depth,
    EdgeType EdgeType,
    long? ParentNodeId,
    Dictionary<string, object>? EdgeProperties);

public record RepositorySearchResult(
    IReadOnlyList<ProjectInfo> Items,
    int TotalCount);

public record ProjectInfo(
    string Name,
    string? RepoUrl,
    string? GitLabGroup,
    string? LocalPath,
    string? LastCommitSha,
    DateTime? IndexedAt,
    string? Language,
    string? Framework,
    bool IsFoundational,
    Dictionary<string, object>? Properties);

public record ProjectSummary(
    string Project,
    string Summary,
    ConfidenceLevel Confidence,
    string SourceHash,
    string? ModelUsed,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record StoredProjectAnalysis(
    string Repo,
    string ProjectName,
    string Summary,
    ConfidenceLevel Confidence,
    IReadOnlyList<StoredEndpoint> Endpoints,
    IReadOnlyList<StoredService> Services,
    IReadOnlyList<string> ExternalDependencies,
    IReadOnlyList<string> DatabaseTables,
    string? ModelUsed,
    DateTime UpdatedAt);

public record StoredEndpoint(
    string Route,
    string HttpMethod,
    string Description,
    string? RequestModel,
    string? ResponseModel);

public record StoredService(
    string Name,
    string Description,
    string? InterfaceName,
    string Lifetime);

public record StoredNodeAnalysis(
    long NodeId,
    string Description,
    string Confidence,
    string? ModelUsed,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record StoredAnalysisBatch(
    long Id,
    string Repo,
    string AnthropicBatchId,
    string Status,
    int RequestCount,
    int CompletedCount,
    DateTime SubmittedAt,
    DateTime? CompletedAt);
