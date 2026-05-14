using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public sealed class MySqlMcpHubStore(CodeGraphDbContext db, IAesEncryptor encryptor) : IMcpHubStore
{
    public async Task<IReadOnlyList<McpHubProviderEntity>> ListProvidersAsync(CancellationToken ct = default) =>
        await db.McpHubProviders.AsNoTracking()
            .OrderBy(provider => provider.DisplayName)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<McpHubToolEntity>> ListToolsAsync(CancellationToken ct = default) =>
        await db.McpHubTools.AsNoTracking()
            .OrderBy(tool => tool.ProviderKey)
            .ThenBy(tool => tool.ToolName)
            .ToListAsync(ct);

    public async Task UpsertProviderAsync(McpHubProviderEntity provider, CancellationToken ct = default)
    {
        var existing = await db.McpHubProviders.SingleOrDefaultAsync(item => item.ProviderKey == provider.ProviderKey, ct);
        if (existing is null)
        {
            db.McpHubProviders.Add(provider);
        }
        else
        {
            existing.DisplayName = provider.DisplayName;
            existing.Description = provider.Description;
            existing.UpdatedAtUtc = provider.UpdatedAtUtc;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task UpsertToolAsync(McpHubToolEntity tool, CancellationToken ct = default)
    {
        var existing = await db.McpHubTools.SingleOrDefaultAsync(item => item.ToolName == tool.ToolName, ct);
        if (existing is null)
        {
            db.McpHubTools.Add(tool);
        }
        else
        {
            existing.ProviderKey = tool.ProviderKey;
            existing.DisplayName = tool.DisplayName;
            existing.Description = tool.Description;
            existing.ReadOnly = tool.ReadOnly;
            existing.Destructive = tool.Destructive;
            existing.RequiresCredential = tool.RequiresCredential;
            // is_available is system-owned — refreshed on every reseed. Enabled / DefaultSelected
            // / AccessClass are admin-owned and intentionally left untouched here.
            existing.IsAvailable = tool.IsAvailable;
            existing.UpdatedAtUtc = tool.UpdatedAtUtc;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> SetProviderEnabledAsync(string providerKey, bool enabled, bool? sourceVisible, CancellationToken ct = default)
    {
        var provider = await db.McpHubProviders.SingleOrDefaultAsync(item => item.ProviderKey == providerKey, ct);
        if (provider is null)
            return false;

        provider.Enabled = enabled;
        if (sourceVisible is not null)
            provider.SourceVisible = sourceVisible.Value;
        provider.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UpdateToolCatalogStateAsync(
        string toolName,
        bool? enabled,
        bool? defaultSelected,
        string? accessClass,
        CancellationToken ct = default)
    {
        var tool = await db.McpHubTools.SingleOrDefaultAsync(item => item.ToolName == toolName, ct);
        if (tool is null)
            return false;

        if (enabled is not null)
            tool.Enabled = enabled.Value;
        if (defaultSelected is not null)
            tool.DefaultSelected = defaultSelected.Value;
        if (!string.IsNullOrWhiteSpace(accessClass))
            tool.AccessClass = accessClass.Trim().ToLowerInvariant();

        tool.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<McpHubCredentialEntity>> ListCredentialsAsync(CancellationToken ct = default) =>
        await db.McpHubCredentials.AsNoTracking()
            .OrderBy(credential => credential.ProviderKey)
            .ThenBy(credential => credential.CredentialKey)
            .ToListAsync(ct);

    public async Task<string?> GetCredentialValueAsync(string providerKey, string credentialKey, CancellationToken ct = default)
    {
        var encrypted = await db.McpHubCredentials.AsNoTracking()
            .Where(item => item.ProviderKey == providerKey && item.CredentialKey == credentialKey)
            .Select(item => item.EncryptedValue)
            .SingleOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(encrypted) ? null : encryptor.Decrypt(encrypted);
    }

    public async Task SetCredentialValueAsync(string providerKey, string credentialKey, string? value, string? updatedBy, CancellationToken ct = default)
    {
        var credential = await db.McpHubCredentials
            .SingleOrDefaultAsync(item => item.ProviderKey == providerKey && item.CredentialKey == credentialKey, ct);
        if (credential is null)
        {
            credential = new McpHubCredentialEntity
            {
                ProviderKey = providerKey,
                CredentialKey = credentialKey
            };
            db.McpHubCredentials.Add(credential);
        }

        credential.EncryptedValue = string.IsNullOrWhiteSpace(value) ? null : encryptor.Encrypt(value);
        credential.UpdatedBy = NormalizeUser(updatedBy);
        credential.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<McpHubConfigEntity>> ListConfigAsync(CancellationToken ct = default) =>
        await db.McpHubConfig.AsNoTracking()
            .OrderBy(config => config.ProviderKey)
            .ThenBy(config => config.ConfigKey)
            .ToListAsync(ct);

    public async Task<string?> GetConfigValueAsync(string providerKey, string configKey, CancellationToken ct = default) =>
        await db.McpHubConfig.AsNoTracking()
            .Where(item => item.ProviderKey == providerKey && item.ConfigKey == configKey)
            .Select(item => item.ConfigValue)
            .SingleOrDefaultAsync(ct);

    public async Task SetConfigValueAsync(string providerKey, string configKey, string? value, string? updatedBy, CancellationToken ct = default)
    {
        var config = await db.McpHubConfig
            .SingleOrDefaultAsync(item => item.ProviderKey == providerKey && item.ConfigKey == configKey, ct);
        if (config is null)
        {
            config = new McpHubConfigEntity
            {
                ProviderKey = providerKey,
                ConfigKey = configKey
            };
            db.McpHubConfig.Add(config);
        }

        config.ConfigValue = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        config.UpdatedBy = NormalizeUser(updatedBy);
        config.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task ReplaceTokenEntitlementsAsync(long tokenId, IReadOnlyCollection<string> toolNames, CancellationToken ct = default)
    {
        var existing = await db.McpPersonalAccessTokenToolEntitlements
            .Where(item => item.TokenId == tokenId)
            .ToListAsync(ct);
        db.McpPersonalAccessTokenToolEntitlements.RemoveRange(existing);

        var now = DateTime.UtcNow;
        db.McpPersonalAccessTokenToolEntitlements.AddRange(toolNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => new McpPersonalAccessTokenToolEntitlementEntity
            {
                TokenId = tokenId,
                ToolName = name,
                CreatedAt = now
            }));

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetTokenEntitlementsAsync(long tokenId, CancellationToken ct = default) =>
        await db.McpPersonalAccessTokenToolEntitlements.AsNoTracking()
            .Where(item => item.TokenId == tokenId)
            .OrderBy(item => item.ToolName)
            .Select(item => item.ToolName)
            .ToListAsync(ct);

    public async Task<bool> IsTokenEntitledAsync(long tokenId, string toolName, CancellationToken ct = default)
    {
        var token = await db.McpPersonalAccessTokens.AsNoTracking()
            .Where(item => item.Id == tokenId)
            .Select(item => new { item.EntitlementMode })
            .SingleOrDefaultAsync(ct);
        if (token is null)
            return false;

        if (!string.Equals(token.EntitlementMode, "selected", StringComparison.OrdinalIgnoreCase))
            return true;

        return await db.McpPersonalAccessTokenToolEntitlements.AsNoTracking()
            .AnyAsync(item => item.TokenId == tokenId && item.ToolName == toolName, ct);
    }

    public async Task CreateAuditAsync(McpHubAuditEntity audit, CancellationToken ct = default)
    {
        db.McpHubAudit.Add(audit);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<McpHubAuditEntity>> ListAuditAsync(int limit, CancellationToken ct = default) =>
        await db.McpHubAudit.AsNoTracking()
            .OrderByDescending(audit => audit.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);

    private static string? NormalizeUser(string? updatedBy) =>
        string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim().ToLowerInvariant();
}
