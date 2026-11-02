namespace CodeGraph.Data;

/// <summary>
/// MCP personal access tokens. A token is owned by a person (<c>agentId</c> null: the username is
/// the owner) or by an agent (<c>agentId</c> set); the owner-scoped calls see only that owner's tokens.
/// </summary>
public interface IMcpPersonalAccessTokenStore
{
    Task<IReadOnlyList<McpPersonalAccessTokenEntity>> ListMcpPersonalAccessTokensAsync(
        string username,
        long? agentId = null,
        CancellationToken ct = default);
    Task<McpPersonalAccessTokenEntity> CreateMcpPersonalAccessTokenAsync(
        McpPersonalAccessTokenEntity token,
        CancellationToken ct = default);
    Task<McpPersonalAccessTokenEntity?> GetMcpPersonalAccessTokenByHashAsync(
        string tokenHash,
        CancellationToken ct = default);
    Task<bool> RevokeMcpPersonalAccessTokenAsync(
        string username,
        long id,
        DateTime revokedAt,
        long? agentId = null,
        CancellationToken ct = default);
    Task<bool> SetMcpPersonalAccessTokenEntitlementModeAsync(
        string username,
        long id,
        string entitlementMode,
        long? agentId = null,
        CancellationToken ct = default);
    Task<bool> UpdateMcpPersonalAccessTokenLastUsedAsync(
        long id,
        DateTime lastUsedAt,
        string? lastUsedFrom,
        CancellationToken ct = default);
}
