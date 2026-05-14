using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeGraph.Data;
using CodeGraph.Models.Responses;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services;

public sealed class McpPersonalAccessTokenService(
    IMcpPersonalAccessTokenStore store,
    IMcpHubStore hubStore,
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

        var results = new List<McpPersonalAccessTokenMetadata>();
        foreach (var token in entities.OrderByDescending(token => token.CreatedAt))
        {
            var entitlements = await hubStore.GetTokenEntitlementsAsync(token.Id, cancellationToken);
            results.Add(ToMetadata(token, now, entitlements));
        }

        return results;
    }

    public async Task<McpPersonalAccessTokenCreateResult> CreateForUserAsync(
        string username,
        string tokenName,
        int expiresInDays,
        IReadOnlyCollection<string>? toolNames = null,
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

        var selectedTools = NormalizeToolNames(toolNames);
        if (toolNames is not null)
        {
            var (validatedTools, errorCode, errorMessage) =
                await ValidateSelectedToolsAsync(toolNames, cancellationToken);
            if (errorCode is not null)
                return Error(errorCode, errorMessage!);
            selectedTools = validatedTools;
        }

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
            ExpiresAt = now.AddDays(expiresInDays),
            EntitlementMode = toolNames is null ? "all" : "selected"
        };

        var stored = await store.CreateMcpPersonalAccessTokenAsync(entity, cancellationToken);
        if (toolNames is not null)
            await hubStore.ReplaceTokenEntitlementsAsync(stored.Id, selectedTools, cancellationToken);

        return new McpPersonalAccessTokenCreateResult(
            Created: true,
            Token: ToMetadata(stored, now, selectedTools),
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

    /// <summary>
    /// Replaces the enabled tool list of an active, non-revoked token owned by the caller.
    /// Tools are re-validated against the catalog the same way token creation validates them.
    /// </summary>
    public async Task<McpPersonalAccessTokenCreateResult> UpdateToolsForUserAsync(
        string username,
        long id,
        IReadOnlyCollection<string> toolNames,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        var now = DateTime.UtcNow;

        var tokens = await store.ListMcpPersonalAccessTokensAsync(normalizedUsername, cancellationToken);
        var token = tokens.FirstOrDefault(item => item.Id == id);
        if (token is null)
            return Error("token_not_found", "MCP token not found.");
        if (token.RevokedAt is not null || token.ExpiresAt <= now)
            return Error("token_not_active", "Only active MCP tokens can have their tools edited.");

        var (selectedTools, errorCode, errorMessage) =
            await ValidateSelectedToolsAsync(toolNames, cancellationToken);
        if (errorCode is not null)
            return Error(errorCode, errorMessage!);

        await hubStore.ReplaceTokenEntitlementsAsync(token.Id, selectedTools, cancellationToken);
        await store.SetMcpPersonalAccessTokenEntitlementModeAsync(
            normalizedUsername, token.Id, "selected", cancellationToken);
        token.EntitlementMode = "selected";

        return new McpPersonalAccessTokenCreateResult(
            Created: true,
            Token: ToMetadata(token, now, selectedTools),
            RawToken: null,
            ErrorCode: null,
            ErrorMessage: null);
    }

    // Shared by token creation and tool-edit: a selection must be non-empty and every tool must
    // be effectively entitleable (catalog enabled AND available — see Shortcut sc-1055).
    private async Task<(IReadOnlyList<string> Tools, string? ErrorCode, string? ErrorMessage)>
        ValidateSelectedToolsAsync(IReadOnlyCollection<string> toolNames, CancellationToken cancellationToken)
    {
        var selectedTools = NormalizeToolNames(toolNames);
        if (selectedTools.Count == 0)
            return ([], "tool_entitlement_required", "Select at least one MCP tool for this token.");

        var availableTools = await hubStore.ListToolsAsync(cancellationToken);
        var enabledTools = availableTools
            .Where(tool => tool.Enabled && tool.IsAvailable)
            .Select(tool => tool.ToolName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownTools = selectedTools.Where(tool => !enabledTools.Contains(tool)).ToArray();
        if (unknownTools.Length > 0)
            return ([], "invalid_tool_entitlement", $"Unknown or disabled MCP tools: {string.Join(", ", unknownTools)}.");

        return (selectedTools, null, null);
    }

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

    private static IReadOnlyList<string> NormalizeToolNames(IReadOnlyCollection<string>? toolNames) =>
        toolNames?
            .Where(tool => !string.IsNullOrWhiteSpace(tool))
            .Select(tool => tool.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray()
        ?? [];

    private static McpPersonalAccessTokenMetadata ToMetadata(
        McpPersonalAccessTokenEntity entity,
        DateTime now,
        IReadOnlyList<string> toolNames)
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
            status,
            string.IsNullOrWhiteSpace(entity.EntitlementMode) ? "all" : entity.EntitlementMode,
            toolNames);
    }
}
