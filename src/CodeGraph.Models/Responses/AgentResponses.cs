namespace CodeGraph.Models.Responses;

/// <summary>An agent (CG-F16) as Admin lists it: status active or disabled, and its active token count.</summary>
public sealed record AgentSummary(
    long Id,
    string Name,
    string Status,
    string CreatedBy,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? DisabledAtUtc,
    int ActiveTokens);
