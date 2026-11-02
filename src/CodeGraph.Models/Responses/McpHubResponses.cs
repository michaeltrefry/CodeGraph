namespace CodeGraph.Models.Responses;

public sealed record McpHubCatalogResponse(
    IReadOnlyList<McpHubProviderResponse> Providers,
    IReadOnlyList<McpHubToolResponse> Tools);

public sealed record McpHubProviderResponse(
    string ProviderKey,
    string DisplayName,
    string Description,
    bool Enabled,
    bool SourceVisible,
    DateTime UpdatedAtUtc);

public sealed record McpHubToolResponse(
    string ToolName,
    string ProviderKey,
    string ProviderType,
    string DisplayName,
    string Description,
    bool ReadOnly,
    bool Destructive,
    bool Enabled,
    bool IsAvailable,
    bool DefaultSelected,
    string AccessClass,
    bool RequiresCredential,
    DateTime UpdatedAtUtc);

public sealed record McpHubCredentialResponse(
    string ProviderKey,
    string CredentialKey,
    bool HasValue,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy);

public sealed record McpHubConfigResponse(
    string ProviderKey,
    string ConfigKey,
    string? ConfigValue,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy);

public sealed record McpHubSensitiveColumnResponse(
    long Id,
    string SourceKey,
    string TableName,
    string ColumnName,
    string? Reason,
    bool Allowed,
    bool IsManual,
    DateTime UpdatedAtUtc);

public sealed record McpHubAuditResponse(
    long Id,
    string? Username,
    long? TokenId,
    string ProviderKey,
    string ProviderType,
    string ToolName,
    string Action,
    string Operation,
    string? ResourceKey,
    string CredentialMode,
    string? ProviderIdentity,
    string AuthorizationDecision,
    string StatusClass,
    int DurationMs,
    bool Success,
    string? Message,
    DateTime CreatedAtUtc);
