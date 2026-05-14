namespace CodeGraph.Models.Requests;

public sealed record McpHubProviderUpdateRequest(bool? Enabled, bool? SourceVisible);

public sealed record McpHubToolUpdateRequest(
    bool? Enabled,
    bool? DefaultSelected = null,
    string? AccessClass = null);

public sealed record McpHubCredentialWriteRequest(string Value);

public sealed record McpHubConfigWriteRequest(string? Value);

public sealed record McpHubSqlQueryRequest(
    string Source,
    string Sql,
    int? Limit);

public sealed record McpHubSensitiveColumnWriteRequest(
    string? SourceKey,
    string? TableName,
    string ColumnName,
    string? Reason,
    bool Allowed);

public sealed record McpProviderCredentialWriteRequest(string Value);
