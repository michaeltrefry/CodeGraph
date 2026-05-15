namespace CodeGraph.Models.Responses;

public record AdminUserResponse(
    string Username,
    DateTime CreatedAt);

public record AgentPromptResponse(
    string Key,
    string Category,
    string CategoryDisplayName,
    string DisplayName,
    string Description,
    string DefaultText,
    string EffectiveText,
    bool HasOverride,
    string? UpdatedBy,
    DateTime? UpdatedAt);

public record AgentPromptGroupResponse(
    string Category,
    string CategoryDisplayName,
    IReadOnlyList<AgentPromptResponse> Prompts);

public record DatabaseSourceResponse(
    long Id,
    string ServerName,
    string DatabaseName,
    string ConnectionString,
    bool Enabled,
    DateTime? LastSyncedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool McpHubEnabled,
    string McpExposureMode,
    string? McpDisplayName,
    string? McpEnvironment);
