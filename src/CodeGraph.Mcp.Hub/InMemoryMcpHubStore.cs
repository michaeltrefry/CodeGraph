using CodeGraph.Data;

namespace CodeGraph.Mcp.Hub;

internal sealed class InMemoryMcpHubStore : IMcpHubStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, McpHubProviderEntity> providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, McpHubToolEntity> tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Provider, string Key), McpHubCredentialEntity> credentials = [];
    private readonly Dictionary<(string Provider, string Key), McpHubConfigEntity> config = [];
    private readonly Dictionary<long, List<string>> entitlements = [];
    private readonly List<McpHubAuditEntity> audit = [];
    private long nextAuditId = 1;

    public Task<IReadOnlyList<McpHubProviderEntity>> ListProvidersAsync(CancellationToken ct = default)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<McpHubProviderEntity>>(providers.Values.ToList());
    }

    public Task<IReadOnlyList<McpHubToolEntity>> ListToolsAsync(CancellationToken ct = default)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<McpHubToolEntity>>(tools.Values.ToList());
    }

    public Task UpsertProviderAsync(McpHubProviderEntity provider, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (providers.TryGetValue(provider.ProviderKey, out var existing))
            {
                provider.Enabled = existing.Enabled;
                provider.SourceVisible = existing.SourceVisible;
                provider.CreatedAtUtc = existing.CreatedAtUtc;
            }
            providers[provider.ProviderKey] = provider;
        }
        return Task.CompletedTask;
    }

    public Task UpsertToolAsync(McpHubToolEntity tool, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (tools.TryGetValue(tool.ToolName, out var existing))
            {
                // Preserve admin-owned state; is_available stays system-owned (from `tool`).
                tool.Enabled = existing.Enabled;
                tool.DefaultSelected = existing.DefaultSelected;
                tool.AccessClass = existing.AccessClass;
                tool.CreatedAtUtc = existing.CreatedAtUtc;
            }
            tools[tool.ToolName] = tool;
        }
        return Task.CompletedTask;
    }

    public Task<bool> SetProviderEnabledAsync(string providerKey, bool enabled, bool? sourceVisible, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!providers.TryGetValue(providerKey, out var provider)) return Task.FromResult(false);
            provider.Enabled = enabled;
            if (sourceVisible is not null) provider.SourceVisible = sourceVisible.Value;
            provider.UpdatedAtUtc = DateTime.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<bool> UpdateToolCatalogStateAsync(
        string toolName,
        bool? enabled,
        bool? defaultSelected,
        string? accessClass,
        CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!tools.TryGetValue(toolName, out var tool)) return Task.FromResult(false);
            if (enabled is not null) tool.Enabled = enabled.Value;
            if (defaultSelected is not null) tool.DefaultSelected = defaultSelected.Value;
            if (!string.IsNullOrWhiteSpace(accessClass)) tool.AccessClass = accessClass.Trim().ToLowerInvariant();
            tool.UpdatedAtUtc = DateTime.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<McpHubCredentialEntity>> ListCredentialsAsync(CancellationToken ct = default)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<McpHubCredentialEntity>>(credentials.Values.ToList());
    }

    public Task<string?> GetCredentialValueAsync(string providerKey, string credentialKey, CancellationToken ct = default)
    {
        lock (gate)
        {
            return Task.FromResult(credentials.TryGetValue((providerKey, credentialKey), out var credential)
                ? credential.EncryptedValue
                : null);
        }
    }

    public Task SetCredentialValueAsync(string providerKey, string credentialKey, string? value, string? updatedBy, CancellationToken ct = default)
    {
        lock (gate)
        {
            credentials[(providerKey, credentialKey)] = new()
            {
                ProviderKey = providerKey,
                CredentialKey = credentialKey,
                EncryptedValue = value,
                UpdatedBy = updatedBy,
                UpdatedAtUtc = DateTime.UtcNow
            };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<McpHubConfigEntity>> ListConfigAsync(CancellationToken ct = default)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<McpHubConfigEntity>>(config.Values.ToList());
    }

    public Task<string?> GetConfigValueAsync(string providerKey, string configKey, CancellationToken ct = default)
    {
        lock (gate)
        {
            return Task.FromResult(config.TryGetValue((providerKey, configKey), out var item)
                ? item.ConfigValue
                : null);
        }
    }

    public Task SetConfigValueAsync(string providerKey, string configKey, string? value, string? updatedBy, CancellationToken ct = default)
    {
        lock (gate)
        {
            config[(providerKey, configKey)] = new()
            {
                ProviderKey = providerKey,
                ConfigKey = configKey,
                ConfigValue = value,
                UpdatedBy = updatedBy,
                UpdatedAtUtc = DateTime.UtcNow
            };
        }
        return Task.CompletedTask;
    }

    public Task ReplaceTokenEntitlementsAsync(long tokenId, IReadOnlyCollection<string> toolNames, CancellationToken ct = default)
    {
        lock (gate) entitlements[tokenId] = toolNames.ToList();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetTokenEntitlementsAsync(long tokenId, CancellationToken ct = default)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<string>>(entitlements.GetValueOrDefault(tokenId) ?? []);
    }

    public Task<bool> IsTokenEntitledAsync(long tokenId, string toolName, CancellationToken ct = default)
    {
        lock (gate) return Task.FromResult(!entitlements.TryGetValue(tokenId, out var names) || names.Contains(toolName, StringComparer.OrdinalIgnoreCase));
    }

    public Task CreateAuditAsync(McpHubAuditEntity item, CancellationToken ct = default)
    {
        lock (gate)
        {
            item.Id = nextAuditId++;
            audit.Add(item);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<McpHubAuditEntity>> ListAuditAsync(int limit, CancellationToken ct = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<McpHubAuditEntity>>(
                audit.OrderByDescending(item => item.CreatedAtUtc).Take(Math.Clamp(limit, 1, 500)).ToList());
        }
    }
}
