namespace CodeGraph.Data;

public interface IMemoryTenantContext
{
    string Username { get; }
    IDisposable Enter(string username, bool allowLegacyDefault = false);
}

public sealed class MemoryTenantContext : IMemoryTenantContext
{
    public const string LegacyDefaultUsername = "default";
    public const string SystemUsername = "system";
    private const string UserPrefix = "user:";

    private string username = SystemUsername;

    public string Username => username;

    public IDisposable Enter(string value, bool allowLegacyDefault = false)
    {
        var normalized = NormalizeStorageUsername(value, allowLegacyDefault);
        var previous = username;
        username = normalized;
        return new Scope(() => username = previous);
    }

    public static string ForAuthenticatedUser(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("An authenticated username is required for personal memory access.");

        return UserPrefix + username.Trim().ToLowerInvariant();
    }

    public static string NormalizeAdministrativeTarget(string username)
    {
        if (username.Trim().Equals(LegacyDefaultUsername, StringComparison.OrdinalIgnoreCase))
            return LegacyDefaultUsername;

        return username.StartsWith(UserPrefix, StringComparison.OrdinalIgnoreCase)
            ? NormalizeStorageUsername(username)
            : ForAuthenticatedUser(username);
    }

    public static string ForTrustedIdentity(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("A trusted memory identity is required.");

        var normalized = username.Trim().ToLowerInvariant();
        return normalized == LegacyDefaultUsername ||
               normalized == SystemUsername ||
               normalized.StartsWith(UserPrefix, StringComparison.Ordinal)
            ? normalized
            : ForAuthenticatedUser(normalized);
    }

    private static string NormalizeStorageUsername(string value, bool allowLegacyDefault = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("A memory tenant username is required.");

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized == LegacyDefaultUsername && !allowLegacyDefault)
            throw new InvalidOperationException("Legacy default memory is quarantined and requires an administrative operation.");

        if (normalized != SystemUsername &&
            normalized != LegacyDefaultUsername &&
            !normalized.StartsWith(UserPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Memory tenant usernames must be canonical storage identities.");
        }

        return normalized;
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? restoreAction = restore;

        public void Dispose() => Interlocked.Exchange(ref restoreAction, null)?.Invoke();
    }
}
