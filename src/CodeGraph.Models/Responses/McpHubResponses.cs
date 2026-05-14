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

public sealed record McpProviderCredentialResponse(
    string ProviderKey,
    string CredentialKey,
    bool HasValue,
    string? TokenFingerprint,
    string? ProviderIdentity,
    string ValidationState,
    string? ValidationMessage,
    DateTime? LastValidatedAtUtc,
    DateTime? LastAttemptAtUtc,
    DateTime? ExpiresAtUtc,
    DateTime UpdatedAtUtc);

public sealed record McpProviderCredentialWriteResult(
    bool Stored,
    string ValidationState,
    string? ProviderIdentity,
    string? Message);

public sealed record McpHubAuditResponse(
    long Id,
    string? Username,
    long? TokenId,
    string ProviderKey,
    string ToolName,
    string Action,
    string Operation,
    string? ResourceKey,
    string CredentialMode,
    string AuthorizationDecision,
    string StatusClass,
    int DurationMs,
    bool Success,
    string? Message,
    DateTime CreatedAtUtc);
