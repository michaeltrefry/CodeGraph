namespace CodeGraph.Data;

/// <summary>
/// Per-user delegated provider credentials. Each row belongs to one CodeGraph user, so a
/// provider call runs as the calling user's own credential rather than a single hub-shared
/// secret — see Shortcut sc-1052.
/// </summary>
public interface IMcpProviderCredentialStore
{
    Task<IReadOnlyList<McpProviderCredentialEntity>> ListForUserAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Returns credential metadata and its decrypted secret from one store snapshot. Callers that
    /// use both identity metadata and the secret for one provider invocation must use this method
    /// so a concurrent replacement cannot pair values from different credential versions.
    /// </summary>
    Task<McpProviderCredentialSnapshot?> GetSnapshotAsync(
        string providerKey,
        string username,
        string credentialKey,
        CancellationToken ct = default);

    Task<McpProviderCredentialEntity?> GetAsync(
        string providerKey,
        string username,
        string credentialKey,
        CancellationToken ct = default);

    /// <summary>Returns the decrypted secret for the user's credential, or null when absent.</summary>
    Task<string?> GetValueAsync(
        string providerKey,
        string username,
        string credentialKey,
        CancellationToken ct = default);

    Task UpsertAsync(McpProviderCredentialEntity entity, string? plaintextValue, CancellationToken ct = default);

    Task<bool> DeleteAsync(
        string providerKey,
        string username,
        string credentialKey,
        CancellationToken ct = default);
}

public sealed record McpProviderCredentialSnapshot(
    string ValidationState,
    string? ProviderIdentity,
    DateTime? ExpiresAtUtc,
    string? Value);
