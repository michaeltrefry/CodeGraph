using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Data;

public enum TraceDirection { Outbound, Inbound, Both }

public record TraversalEntry(
    GraphNode Node,
    int Depth,
    EdgeType EdgeType,
    long? ParentNodeId,
    Dictionary<string, object>? EdgeProperties);

public record ProjectInfo(
    string Name,
    string? RepoUrl,
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
