namespace CodeGraph.Data;

public interface IMcpHubStore
{
    Task<IReadOnlyList<McpHubProviderEntity>> ListProvidersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<McpHubToolEntity>> ListToolsAsync(CancellationToken ct = default);
    Task UpsertProviderAsync(McpHubProviderEntity provider, CancellationToken ct = default);
    Task UpsertToolAsync(McpHubToolEntity tool, CancellationToken ct = default);
    Task<bool> SetProviderEnabledAsync(string providerKey, bool enabled, bool? sourceVisible, CancellationToken ct = default);

    /// <summary>
    /// Updates the admin-owned catalog state of a tool. Only non-null arguments are applied.
    /// <c>is_available</c> is system-owned and is not settable here — it is maintained by the
    /// catalog seeder / discovery refresh.
    /// </summary>
    Task<bool> UpdateToolCatalogStateAsync(
        string toolName,
        bool? enabled,
        bool? defaultSelected,
        string? accessClass,
        CancellationToken ct = default);

    Task<IReadOnlyList<McpHubCredentialEntity>> ListCredentialsAsync(CancellationToken ct = default);
    Task<string?> GetCredentialValueAsync(string providerKey, string credentialKey, CancellationToken ct = default);
    Task SetCredentialValueAsync(string providerKey, string credentialKey, string? value, string? updatedBy, CancellationToken ct = default);

    Task<IReadOnlyList<McpHubConfigEntity>> ListConfigAsync(CancellationToken ct = default);
    Task<string?> GetConfigValueAsync(string providerKey, string configKey, CancellationToken ct = default);
    Task SetConfigValueAsync(string providerKey, string configKey, string? value, string? updatedBy, CancellationToken ct = default);

    Task ReplaceTokenEntitlementsAsync(long tokenId, IReadOnlyCollection<string> toolNames, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetTokenEntitlementsAsync(long tokenId, CancellationToken ct = default);
    Task<bool> IsTokenEntitledAsync(long tokenId, string toolName, CancellationToken ct = default);

    Task CreateAuditAsync(McpHubAuditEntity audit, CancellationToken ct = default);
    Task<IReadOnlyList<McpHubAuditEntity>> ListAuditAsync(int limit, CancellationToken ct = default);
}
