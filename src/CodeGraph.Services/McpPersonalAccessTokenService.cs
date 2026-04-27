using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeGraph.Data;
using CodeGraph.Models.Responses;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services;

public sealed class McpPersonalAccessTokenService(
    IMcpPersonalAccessTokenStore store,
    IOptions<CodeGraphStorageOptions> storageOptionsAccessor)
{
    private const string TokenPrefix = "cgmcp_";
    private const int RandomTokenBytes = 32;
    private const int LegacyTokenHexChars = 63;
    private const int ActiveTokenLimit = 5;
    private static readonly int[] AllowedExpirationDays = [30, 60, 90];
    private static readonly Regex TokenFormat = new(
        $"^{TokenPrefix}(?:[a-f0-9]{{{LegacyTokenHexChars}}}|[a-f0-9]{{{RandomTokenBytes * 2}}})$",
        RegexOptions.Compiled);

    private readonly CodeGraphStorageOptions storageOptions = storageOptionsAccessor.Value;

    public async Task<IReadOnlyList<McpPersonalAccessTokenMetadata>> ListForUserAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        var now = DateTime.UtcNow;
        var entities = await store.ListMcpPersonalAccessTokensAsync(normalizedUsername, cancellationToken);

        return entities
            .OrderByDescending(token => token.CreatedAt)
            .Select(token => ToMetadata(token, now))
            .ToList();
    }

    public async Task<McpPersonalAccessTokenCreateResult> CreateForUserAsync(
        string username,
        string tokenName,
        int expiresInDays,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        var trimmedName = tokenName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(trimmedName))
            return Error("token_name_required", "Token name is required.");

        if (!AllowedExpirationDays.Contains(expiresInDays))
            return Error("invalid_expiration", "Expiration must be one of 30, 60, or 90 days.");

        if (!TryGetSigningKey(out var signingKey))
            return Error(
                "pat_configuration_missing",
                "CodeGraph:StorageOptions:MariaDbEncryptionKey must be configured as valid base64 before issuing MCP personal access tokens.");

        var now = DateTime.UtcNow;
        var tokens = await store.ListMcpPersonalAccessTokensAsync(normalizedUsername, cancellationToken);
        var activeTokenCount = tokens.Count(token => token.RevokedAt is null && token.ExpiresAt > now);
        if (activeTokenCount >= ActiveTokenLimit)
            return Error("active_token_limit_reached", $"Users may only have {ActiveTokenLimit} active MCP personal access tokens.");

        var rawToken = GenerateRawToken();
        var entity = new McpPersonalAccessTokenEntity
        {
            Username = normalizedUsername,
            TokenName = trimmedName,
            TokenPrefixValue = rawToken[..Math.Min(16, rawToken.Length)],
            TokenHash = ComputeTokenHash(rawToken, signingKey),
            LastFour = rawToken[^4..],
            CreatedAt = now,
            ExpiresAt = now.AddDays(expiresInDays)
        };

        var stored = await store.CreateMcpPersonalAccessTokenAsync(entity, cancellationToken);
        return new McpPersonalAccessTokenCreateResult(
            Created: true,
            Token: ToMetadata(stored, now),
            RawToken: rawToken,
            ErrorCode: null,
            ErrorMessage: null);
    }

    public Task<bool> RevokeForUserAsync(
        string username,
        long id,
        CancellationToken cancellationToken = default) =>
        store.RevokeMcpPersonalAccessTokenAsync(
            NormalizeUsername(username),
            id,
            DateTime.UtcNow,
            cancellationToken);

    public async Task<McpPersonalAccessTokenValidationResult?> ValidateAsync(
        string rawToken,
        string? lastUsedFrom,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidTokenFormat(rawToken) || !TryGetSigningKey(out var signingKey))
            return null;

        var now = DateTime.UtcNow;
        var tokenHash = ComputeTokenHash(rawToken, signingKey);
        var entity = await store.GetMcpPersonalAccessTokenByHashAsync(tokenHash, cancellationToken);
        if (entity is null || entity.RevokedAt is not null || entity.ExpiresAt <= now)
            return null;

        var trimmedLastUsedFrom = NormalizeLastUsedFrom(lastUsedFrom);
        if (entity.LastUsedAt is null ||
            entity.LastUsedAt < now.AddMinutes(-1) ||
            !string.Equals(entity.LastUsedFrom, trimmedLastUsedFrom, StringComparison.Ordinal))
        {
            await store.UpdateMcpPersonalAccessTokenLastUsedAsync(
                entity.Id,
                now,
                trimmedLastUsedFrom,
                cancellationToken);
        }

        return new McpPersonalAccessTokenValidationResult(
            entity.Id,
            entity.Username,
            entity.TokenName,
            entity.ExpiresAt);
    }

    public bool IsValidTokenFormat(string rawToken) =>
        !string.IsNullOrWhiteSpace(rawToken) && TokenFormat.IsMatch(rawToken.Trim());

    private static McpPersonalAccessTokenCreateResult Error(string code, string message) =>
        new(false, null, null, code, message);

    private static string NormalizeUsername(string? username) =>
        string.IsNullOrWhiteSpace(username) ? "anonymous" : username.Trim().ToLowerInvariant();

    private static string? NormalizeLastUsedFrom(string? lastUsedFrom)
    {
        if (string.IsNullOrWhiteSpace(lastUsedFrom))
            return null;

        var trimmed = lastUsedFrom.Trim();
        return trimmed[..Math.Min(trimmed.Length, 255)];
    }

    private static string ComputeTokenHash(string rawToken, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawToken.Trim()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private bool TryGetSigningKey(out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(storageOptions.MariaDbEncryptionKey))
            return false;

        try
        {
            key = Convert.FromBase64String(storageOptions.MariaDbEncryptionKey!);
            return key.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string GenerateRawToken()
    {
        Span<byte> bytes = stackalloc byte[RandomTokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return TokenPrefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static McpPersonalAccessTokenMetadata ToMetadata(McpPersonalAccessTokenEntity entity, DateTime now)
    {
        var status = entity.RevokedAt is not null
            ? "revoked"
            : entity.ExpiresAt <= now
                ? "expired"
                : "active";

        return new McpPersonalAccessTokenMetadata(
            entity.Id,
            entity.TokenName,
            entity.TokenPrefixValue,
            entity.LastFour,
            entity.CreatedAt,
            entity.ExpiresAt,
            entity.RevokedAt,
            entity.LastUsedAt,
            entity.LastUsedFrom,
            status);
    }
}
