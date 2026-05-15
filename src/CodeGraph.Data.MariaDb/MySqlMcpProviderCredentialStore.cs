using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public sealed class MySqlMcpProviderCredentialStore(CodeGraphDbContext db, IAesEncryptor encryptor)
    : IMcpProviderCredentialStore
{
    public async Task<IReadOnlyList<McpProviderCredentialEntity>> ListForUserAsync(
        string username,
        CancellationToken ct = default)
    {
        var normalizedUser = NormalizeUser(username);
        return await db.McpProviderCredentials.AsNoTracking()
            .Where(credential => credential.Username == normalizedUser)
            .OrderBy(credential => credential.ProviderKey)
            .ThenBy(credential => credential.CredentialKey)
            .ToListAsync(ct);
    }

    public async Task<McpProviderCredentialEntity?> GetAsync(
        string providerKey,
        string username,
        string credentialKey,
        CancellationToken ct = default)
    {
        var (provider, user, key) = Normalize(providerKey, username, credentialKey);
        return await db.McpProviderCredentials.AsNoTracking()
            .SingleOrDefaultAsync(
                credential => credential.ProviderKey == provider
                    && credential.Username == user
                    && credential.CredentialKey == key,
                ct);
    }

    public async Task<string?> GetValueAsync(
        string providerKey,
        string username,
        string credentialKey,
        CancellationToken ct = default)
    {
        var (provider, user, key) = Normalize(providerKey, username, credentialKey);
        var encrypted = await db.McpProviderCredentials.AsNoTracking()
            .Where(credential => credential.ProviderKey == provider
                && credential.Username == user
                && credential.CredentialKey == key)
            .Select(credential => credential.EncryptedValue)
            .SingleOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(encrypted) ? null : encryptor.Decrypt(encrypted);
    }

    public async Task UpsertAsync(
        McpProviderCredentialEntity entity,
        string? plaintextValue,
        CancellationToken ct = default)
    {
        var (provider, user, key) = Normalize(entity.ProviderKey, entity.Username, entity.CredentialKey);
        var existing = await db.McpProviderCredentials.SingleOrDefaultAsync(
            credential => credential.ProviderKey == provider
                && credential.Username == user
                && credential.CredentialKey == key,
            ct);

        var now = DateTime.UtcNow;
        var encryptedValue = string.IsNullOrWhiteSpace(plaintextValue) ? null : encryptor.Encrypt(plaintextValue);

        if (existing is null)
        {
            db.McpProviderCredentials.Add(new McpProviderCredentialEntity
            {
                ProviderKey = provider,
                Username = user,
                CredentialKey = key,
                EncryptedValue = encryptedValue,
                TokenFingerprint = entity.TokenFingerprint,
                ProviderIdentity = entity.ProviderIdentity,
                ValidationState = entity.ValidationState,
                ValidationMessage = entity.ValidationMessage,
                LastValidatedAtUtc = entity.LastValidatedAtUtc,
                LastAttemptAtUtc = entity.LastAttemptAtUtc,
                ExpiresAtUtc = entity.ExpiresAtUtc,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }
        else
        {
            existing.EncryptedValue = encryptedValue;
            existing.TokenFingerprint = entity.TokenFingerprint;
            existing.ProviderIdentity = entity.ProviderIdentity;
            existing.ValidationState = entity.ValidationState;
            existing.ValidationMessage = entity.ValidationMessage;
            existing.LastValidatedAtUtc = entity.LastValidatedAtUtc;
            existing.LastAttemptAtUtc = entity.LastAttemptAtUtc;
            existing.ExpiresAtUtc = entity.ExpiresAtUtc;
            existing.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(
        string providerKey,
        string username,
        string credentialKey,
        CancellationToken ct = default)
    {
        var (provider, user, key) = Normalize(providerKey, username, credentialKey);
        var existing = await db.McpProviderCredentials.SingleOrDefaultAsync(
            credential => credential.ProviderKey == provider
                && credential.Username == user
                && credential.CredentialKey == key,
            ct);
        if (existing is null)
            return false;

        db.McpProviderCredentials.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static (string Provider, string User, string Key) Normalize(
        string providerKey,
        string username,
        string credentialKey) =>
        (providerKey.Trim().ToLowerInvariant(), NormalizeUser(username), credentialKey.Trim());

    private static string NormalizeUser(string username) => username.Trim().ToLowerInvariant();
}
