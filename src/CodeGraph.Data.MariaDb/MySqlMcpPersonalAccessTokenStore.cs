using System.Linq.Expressions;
using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public sealed class MySqlMcpPersonalAccessTokenStore(CodeGraphDbContext db) : IMcpPersonalAccessTokenStore
{
    public async Task<IReadOnlyList<McpPersonalAccessTokenEntity>> ListMcpPersonalAccessTokensAsync(
        string username,
        long? agentId = null,
        CancellationToken ct = default)
    {
        return await db.McpPersonalAccessTokens
            .AsNoTracking()
            .Where(OwnedBy(username, agentId))
            .OrderByDescending(token => token.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<McpPersonalAccessTokenEntity> CreateMcpPersonalAccessTokenAsync(
        McpPersonalAccessTokenEntity token,
        CancellationToken ct = default)
    {
        db.McpPersonalAccessTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return token;
    }

    public async Task<McpPersonalAccessTokenEntity?> GetMcpPersonalAccessTokenByHashAsync(
        string tokenHash,
        CancellationToken ct = default)
    {
        return await db.McpPersonalAccessTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(token => token.TokenHash == tokenHash, ct);
    }

    public async Task<bool> RevokeMcpPersonalAccessTokenAsync(
        string username,
        long id,
        DateTime revokedAt,
        long? agentId = null,
        CancellationToken ct = default)
    {
        var token = await db.McpPersonalAccessTokens
            .Where(OwnedBy(username, agentId))
            .FirstOrDefaultAsync(item => item.Id == id, ct);

        if (token is null)
        {
            return false;
        }

        token.RevokedAt ??= revokedAt;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetMcpPersonalAccessTokenEntitlementModeAsync(
        string username,
        long id,
        string entitlementMode,
        long? agentId = null,
        CancellationToken ct = default)
    {
        var token = await db.McpPersonalAccessTokens
            .Where(OwnedBy(username, agentId))
            .FirstOrDefaultAsync(item => item.Id == id, ct);

        if (token is null)
        {
            return false;
        }

        token.EntitlementMode = entitlementMode;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UpdateMcpPersonalAccessTokenLastUsedAsync(
        long id,
        DateTime lastUsedAt,
        string? lastUsedFrom,
        CancellationToken ct = default)
    {
        var token = await db.McpPersonalAccessTokens.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (token is null)
        {
            return false;
        }

        token.LastUsedAt = lastUsedAt;
        token.LastUsedFrom = string.IsNullOrWhiteSpace(lastUsedFrom)
            ? null
            : lastUsedFrom.Trim()[..Math.Min(lastUsedFrom.Trim().Length, 255)];
        await db.SaveChangesAsync(ct);
        return true;
    }

    // A person's tokens are theirs by username and never include an agent's; an agent's are its own by id.
    private static Expression<Func<McpPersonalAccessTokenEntity, bool>> OwnedBy(string username, long? agentId) =>
        agentId is { } agent
            ? token => token.AgentId == agent
            : token => token.AgentId == null && token.Username == username;
}
