using CodeGraph.Data;

namespace CodeGraph.Mcp.Hub;

/// <summary>
/// Fallback per-user credential store used when no MariaDB provider is registered. Mirrors
/// <see cref="InMemoryMcpHubStore"/> — process-local, non-durable, values held in memory.
/// </summary>
internal sealed class InMemoryMcpProviderCredentialStore : IMcpProviderCredentialStore
{
    private readonly object gate = new();
    private readonly Dictionary<(string Provider, string User, string Key), Row> rows = [];
    private long nextId = 1;

    public Task<IReadOnlyList<McpProviderCredentialEntity>> ListForUserAsync(
        string username,
        CancellationToken ct = default)
    {
        var user = NormalizeUser(username);
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<McpProviderCredentialEntity>>(rows.Values
                .Where(row => row.Entity.Username == user)
                .Select(row => Clone(row.Entity))
                .ToList());
        }
    }

    public Task<McpProviderCredentialEntity?> GetAsync(
        string providerKey,
        string username,
        string credentialKey,
        CancellationToken ct = default)
    {
        var key = NormalizeKey(providerKey, username, credentialKey);
        lock (gate)
            return Task.FromResult(rows.TryGetValue(key, out var row) ? Clone(row.Entity) : null);
    }

    public Task<string?> GetValueAsync(
        string providerKey,
        string username,
        string credentialKey,
        CancellationToken ct = default)
    {
        var key = NormalizeKey(providerKey, username, credentialKey);
        lock (gate)
            return Task.FromResult(rows.TryGetValue(key, out var row) ? row.Value : null);
    }

    public Task UpsertAsync(
        McpProviderCredentialEntity entity,
        string? plaintextValue,
        CancellationToken ct = default)
    {
        var key = NormalizeKey(entity.ProviderKey, entity.Username, entity.CredentialKey);
        lock (gate)
        {
            var now = DateTime.UtcNow;
            var hasValue = !string.IsNullOrWhiteSpace(plaintextValue);
            var stored = new McpProviderCredentialEntity
            {
                ProviderKey = key.Provider,
                Username = key.User,
                CredentialKey = key.Key,
                // The fallback store keeps the secret in Row.Value; this sentinel only drives
                // the HasValue projection so callers see a consistent shape with the DB store.
                EncryptedValue = hasValue ? "[in-memory]" : null,
                TokenFingerprint = entity.TokenFingerprint,
                ProviderIdentity = entity.ProviderIdentity,
                ValidationState = entity.ValidationState,
                ValidationMessage = entity.ValidationMessage,
                LastValidatedAtUtc = entity.LastValidatedAtUtc,
                LastAttemptAtUtc = entity.LastAttemptAtUtc,
                ExpiresAtUtc = entity.ExpiresAtUtc,
                UpdatedAtUtc = now,
            };

            if (rows.TryGetValue(key, out var existing))
            {
                stored.Id = existing.Entity.Id;
                stored.CreatedAtUtc = existing.Entity.CreatedAtUtc;
            }
            else
            {
                stored.Id = nextId++;
                stored.CreatedAtUtc = now;
            }

            rows[key] = new Row(stored, hasValue ? plaintextValue : null);
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(
        string providerKey,
        string username,
        string credentialKey,
        CancellationToken ct = default)
    {
        var key = NormalizeKey(providerKey, username, credentialKey);
        lock (gate)
            return Task.FromResult(rows.Remove(key));
    }

    private static (string Provider, string User, string Key) NormalizeKey(
        string providerKey,
        string username,
        string credentialKey) =>
        (providerKey.Trim().ToLowerInvariant(), NormalizeUser(username), credentialKey.Trim());

    private static string NormalizeUser(string username) => username.Trim().ToLowerInvariant();

    private static McpProviderCredentialEntity Clone(McpProviderCredentialEntity entity) => new()
    {
        Id = entity.Id,
        ProviderKey = entity.ProviderKey,
        Username = entity.Username,
        CredentialKey = entity.CredentialKey,
        EncryptedValue = entity.EncryptedValue,
        TokenFingerprint = entity.TokenFingerprint,
        ProviderIdentity = entity.ProviderIdentity,
        ValidationState = entity.ValidationState,
        ValidationMessage = entity.ValidationMessage,
        LastValidatedAtUtc = entity.LastValidatedAtUtc,
        LastAttemptAtUtc = entity.LastAttemptAtUtc,
        ExpiresAtUtc = entity.ExpiresAtUtc,
        CreatedAtUtc = entity.CreatedAtUtc,
        UpdatedAtUtc = entity.UpdatedAtUtc,
    };

    private sealed record Row(McpProviderCredentialEntity Entity, string? Value);
}
