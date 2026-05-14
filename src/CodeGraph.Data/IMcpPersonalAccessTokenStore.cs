namespace CodeGraph.Data;

public interface IMcpPersonalAccessTokenStore
{
    Task<IReadOnlyList<McpPersonalAccessTokenEntity>> ListMcpPersonalAccessTokensAsync(
        string username,
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
        CancellationToken ct = default);
    Task<bool> SetMcpPersonalAccessTokenEntitlementModeAsync(
        string username,
        long id,
        string entitlementMode,
        CancellationToken ct = default);
    Task<bool> UpdateMcpPersonalAccessTokenLastUsedAsync(
        long id,
        DateTime lastUsedAt,
        string? lastUsedFrom,
        CancellationToken ct = default);
}
