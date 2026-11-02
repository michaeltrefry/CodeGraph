namespace CodeGraph.Models.Responses;

public sealed record McpPersonalAccessTokenMetadata(
    long Id,
    string TokenName,
    string TokenPrefix,
    string LastFour,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? RevokedAtUtc,
    DateTime? LastUsedAtUtc,
    string? LastUsedFrom,
    string Status,
    string EntitlementMode,
    IReadOnlyList<string> ToolNames);

public sealed record McpPersonalAccessTokenCreateResult(
    bool Created,
    McpPersonalAccessTokenMetadata? Token,
    string? RawToken,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>A valid token: its owner's name, and for an agent's token the agent (whose current name it is).</summary>
public sealed record McpPersonalAccessTokenValidationResult(
    long TokenId,
    string Username,
    string TokenName,
    DateTime ExpiresAtUtc,
    long? AgentId = null);
