using System.Security.Cryptography;
using System.Text;
using CodeGraph.Data;
using CodeGraph.Services;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class McpPersonalAccessTokenServiceTests
{
    [Fact]
    public async Task CreateForUserAsync_IssuesToken_AndStoresOnlyProtectedMetadata()
    {
        var store = new InMemoryMcpPersonalAccessTokenStore();
        var service = CreateService(store);

        var result = await service.CreateForUserAsync("Michael", "Claude Desktop", 30);

        result.Created.ShouldBeTrue();
        result.RawToken.ShouldNotBeNull();
        result.RawToken.ShouldStartWith("cgmcp_");
        result.Token.ShouldNotBeNull();
        result.Token.Status.ShouldBe("active");

        var entity = store.Tokens.Single();
        entity.Username.ShouldBe("michael");
        entity.TokenName.ShouldBe("Claude Desktop");
        entity.TokenHash.ShouldNotBe(result.RawToken);
        entity.TokenPrefixValue.ShouldBe(result.RawToken![..Math.Min(16, result.RawToken.Length)]);
        entity.LastFour.ShouldBe(result.RawToken[^4..]);
    }

    [Fact]
    public async Task ValidateAsync_UpdatesLastUsed_AndRevokedTokensStopAuthenticating()
    {
        var store = new InMemoryMcpPersonalAccessTokenStore();
        var service = CreateService(store);
        var created = await service.CreateForUserAsync("michael", "Desktop", 30);

        var validation = await service.ValidateAsync(created.RawToken!, "127.0.0.1");

        validation.ShouldNotBeNull();
        validation.Username.ShouldBe("michael");
        var stored = store.Tokens.Single();
        stored.LastUsedAt.ShouldNotBeNull();
        stored.LastUsedFrom.ShouldBe("127.0.0.1");

        (await service.RevokeForUserAsync("michael", stored.Id)).ShouldBeTrue();
        (await service.ValidateAsync(created.RawToken!, "127.0.0.1")).ShouldBeNull();
    }

    [Fact]
    public async Task CreateForUserAsync_ReturnsConfigurationError_WhenSigningKeyIsNotBase64()
    {
        var service = new McpPersonalAccessTokenService(
            new InMemoryMcpPersonalAccessTokenStore(),
            new InMemoryMcpHubStore(),
            Options.Create(new CodeGraphStorageOptions { MariaDbEncryptionKey = "not-base64" }));

        var result = await service.CreateForUserAsync("michael", "Desktop", 30);

        result.Created.ShouldBeFalse();
        result.ErrorCode.ShouldBe("pat_configuration_missing");
    }

    [Fact]
    public async Task CreateForUserAsync_StoresSelectedToolEntitlements()
    {
        var store = new InMemoryMcpPersonalAccessTokenStore();
        var hubStore = new InMemoryMcpHubStore();
        var service = new McpPersonalAccessTokenService(
            store,
            hubStore,
            Options.Create(new CodeGraphStorageOptions
            {
                MariaDbEncryptionKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray())
            }));

        var result = await service.CreateForUserAsync("michael", "Selected tools", 30, ["search_graph"]);

        result.Created.ShouldBeTrue();
        result.Token!.EntitlementMode.ShouldBe("selected");
        result.Token.ToolNames.ShouldBe(["search_graph"]);
        (await hubStore.IsTokenEntitledAsync(store.Tokens.Single().Id, "search_graph")).ShouldBeTrue();
        (await hubStore.IsTokenEntitledAsync(store.Tokens.Single().Id, "query_memory")).ShouldBeFalse();
    }

    [Fact]
    public async Task CreateForUserAsync_RejectsUnknownSelectedTools()
    {
        var service = CreateService(new InMemoryMcpPersonalAccessTokenStore());

        var result = await service.CreateForUserAsync("michael", "Bad tools", 30, ["missing_tool"]);

        result.Created.ShouldBeFalse();
        result.ErrorCode.ShouldBe("invalid_tool_entitlement");
    }

    [Fact]
    public async Task CreateForUserAsync_RejectsUnavailableSelectedTools()
    {
        var service = CreateService(new InMemoryMcpPersonalAccessTokenStore());

        // The tool is admin-enabled but is_available = false, so it is not effectively entitleable.
        var result = await service.CreateForUserAsync("michael", "Unavailable tool", 30, ["unavailable_tool"]);

        result.Created.ShouldBeFalse();
        result.ErrorCode.ShouldBe("invalid_tool_entitlement");
    }

    [Fact]
    public async Task UpdateToolsForUserAsync_ReplacesEnabledToolList_AndSwitchesToSelectedMode()
    {
        var store = new InMemoryMcpPersonalAccessTokenStore();
        var hubStore = new InMemoryMcpHubStore();
        var service = CreateService(store, hubStore);

        // Created without a tool list -> entitlement mode "all".
        var created = await service.CreateForUserAsync("michael", "Desktop", 30);
        created.Created.ShouldBeTrue();
        var tokenId = store.Tokens.Single().Id;

        var updated = await service.UpdateToolsForUserAsync("michael", tokenId, ["query_memory"]);

        updated.Created.ShouldBeTrue();
        updated.Token!.EntitlementMode.ShouldBe("selected");
        updated.Token.ToolNames.ShouldBe(["query_memory"]);
        updated.RawToken.ShouldBeNull();
        (await hubStore.GetTokenEntitlementsAsync(tokenId)).ShouldBe(["query_memory"]);
        (await hubStore.IsTokenEntitledAsync(tokenId, "search_graph")).ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateToolsForUserAsync_RejectsRevokedToken()
    {
        var store = new InMemoryMcpPersonalAccessTokenStore();
        var service = CreateService(store);
        await service.CreateForUserAsync("michael", "Desktop", 30);
        var tokenId = store.Tokens.Single().Id;
        await service.RevokeForUserAsync("michael", tokenId);

        var result = await service.UpdateToolsForUserAsync("michael", tokenId, ["search_graph"]);

        result.Created.ShouldBeFalse();
        result.ErrorCode.ShouldBe("token_not_active");
    }

    [Fact]
    public async Task UpdateToolsForUserAsync_RejectsTokenOwnedByAnotherUser()
    {
        var store = new InMemoryMcpPersonalAccessTokenStore();
        var service = CreateService(store);
        await service.CreateForUserAsync("alice", "Desktop", 30);
        var tokenId = store.Tokens.Single().Id;

        var result = await service.UpdateToolsForUserAsync("bob", tokenId, ["search_graph"]);

        result.Created.ShouldBeFalse();
        result.ErrorCode.ShouldBe("token_not_found");
    }

    [Fact]
    public async Task UpdateToolsForUserAsync_RejectsUnknownEmptyOrUnavailableTools()
    {
        var store = new InMemoryMcpPersonalAccessTokenStore();
        var service = CreateService(store);
        await service.CreateForUserAsync("michael", "Desktop", 30);
        var tokenId = store.Tokens.Single().Id;

        (await service.UpdateToolsForUserAsync("michael", tokenId, [])).ErrorCode
            .ShouldBe("tool_entitlement_required");
        (await service.UpdateToolsForUserAsync("michael", tokenId, ["missing_tool"])).ErrorCode
            .ShouldBe("invalid_tool_entitlement");
        (await service.UpdateToolsForUserAsync("michael", tokenId, ["unavailable_tool"])).ErrorCode
            .ShouldBe("invalid_tool_entitlement");
    }


    [Fact]
    public void IsValidTokenFormat_AcceptsCurrentAndLegacyTokenLengths()
    {
        var service = CreateService(new InMemoryMcpPersonalAccessTokenStore());

        service.IsValidTokenFormat("cgmcp_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")
            .ShouldBeTrue();
        service.IsValidTokenFormat("cgmcp_0232e81d95d19b7bf6e6bc12628f00eb1c8a2cc0ba6722f1b82433bc7ba4d68")
            .ShouldBeTrue();
        service.IsValidTokenFormat("cgmcp_short").ShouldBeFalse();
    }

    [Fact]
    public async Task ValidateAsync_AcceptsLegacyTokenLength()
    {
        var store = new InMemoryMcpPersonalAccessTokenStore();
        var signingKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var rawToken = "cgmcp_0232e81d95d19b7bf6e6bc12628f00eb1c8a2cc0ba6722f1b82433bc7ba4d68";
        var now = DateTime.UtcNow;
        store.Tokens.Add(new McpPersonalAccessTokenEntity
        {
            Id = 1,
            Username = "michael",
            TokenName = "Legacy token",
            TokenPrefixValue = rawToken[..Math.Min(16, rawToken.Length)],
            TokenHash = ComputeTokenHash(rawToken, signingKey),
            LastFour = rawToken[^4..],
            CreatedAt = now,
            ExpiresAt = now.AddDays(30)
        });

        var service = new McpPersonalAccessTokenService(
            store,
            new InMemoryMcpHubStore(),
            Options.Create(new CodeGraphStorageOptions { MariaDbEncryptionKey = Convert.ToBase64String(signingKey) }));

        var validation = await service.ValidateAsync(rawToken, "127.0.0.1");

        validation.ShouldNotBeNull();
        validation.Username.ShouldBe("michael");
    }

    private static McpPersonalAccessTokenService CreateService(InMemoryMcpPersonalAccessTokenStore store) =>
        CreateService(store, new InMemoryMcpHubStore());

    private static McpPersonalAccessTokenService CreateService(
        InMemoryMcpPersonalAccessTokenStore store,
        IMcpHubStore hubStore)
    {
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        return new McpPersonalAccessTokenService(
            store,
            hubStore,
            Options.Create(new CodeGraphStorageOptions { MariaDbEncryptionKey = key }));
    }

    private static string ComputeTokenHash(string rawToken, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawToken.Trim()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class InMemoryMcpPersonalAccessTokenStore : IMcpPersonalAccessTokenStore
    {
        private long nextId = 1;

        public List<McpPersonalAccessTokenEntity> Tokens { get; } = [];

        public Task<IReadOnlyList<McpPersonalAccessTokenEntity>> ListMcpPersonalAccessTokensAsync(
            string username,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpPersonalAccessTokenEntity>>(
                Tokens.Where(token => token.Username == username).ToList());

        public Task<McpPersonalAccessTokenEntity> CreateMcpPersonalAccessTokenAsync(
            McpPersonalAccessTokenEntity token,
            CancellationToken ct = default)
        {
            token.Id = nextId++;
            Tokens.Add(token);
            return Task.FromResult(token);
        }

        public Task<McpPersonalAccessTokenEntity?> GetMcpPersonalAccessTokenByHashAsync(
            string tokenHash,
            CancellationToken ct = default) =>
            Task.FromResult(Tokens.FirstOrDefault(token => token.TokenHash == tokenHash));

        public Task<bool> RevokeMcpPersonalAccessTokenAsync(
            string username,
            long id,
            DateTime revokedAt,
            CancellationToken ct = default)
        {
            var token = Tokens.FirstOrDefault(item => item.Id == id && item.Username == username);
            if (token is null)
                return Task.FromResult(false);

            token.RevokedAt ??= revokedAt;
            return Task.FromResult(true);
        }

        public Task<bool> SetMcpPersonalAccessTokenEntitlementModeAsync(
            string username,
            long id,
            string entitlementMode,
            CancellationToken ct = default)
        {
            var token = Tokens.FirstOrDefault(item => item.Id == id && item.Username == username);
            if (token is null)
                return Task.FromResult(false);

            token.EntitlementMode = entitlementMode;
            return Task.FromResult(true);
        }

        public Task<bool> UpdateMcpPersonalAccessTokenLastUsedAsync(
            long id,
            DateTime lastUsedAt,
            string? lastUsedFrom,
            CancellationToken ct = default)
        {
            var token = Tokens.FirstOrDefault(item => item.Id == id);
            if (token is null)
                return Task.FromResult(false);

            token.LastUsedAt = lastUsedAt;
            token.LastUsedFrom = lastUsedFrom;
            return Task.FromResult(true);
        }
    }

    private sealed class InMemoryMcpHubStore : IMcpHubStore
    {
        private readonly Dictionary<long, List<string>> entitlements = [];

        public Task<IReadOnlyList<McpHubProviderEntity>> ListProvidersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpHubProviderEntity>>([]);

        public Task<IReadOnlyList<McpHubToolEntity>> ListToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpHubToolEntity>>([
                new() { ToolName = "search_graph", Enabled = true, IsAvailable = true },
                new() { ToolName = "query_memory", Enabled = true, IsAvailable = true },
                // Enabled by an admin but not available in this deployment -> not effectively entitleable.
                new() { ToolName = "unavailable_tool", Enabled = true, IsAvailable = false }
            ]);

        public Task UpsertProviderAsync(McpHubProviderEntity provider, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertToolAsync(McpHubToolEntity tool, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> SetProviderEnabledAsync(string providerKey, bool enabled, bool? sourceVisible, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> UpdateToolCatalogStateAsync(string toolName, bool? enabled, bool? defaultSelected, string? accessClass, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<McpHubCredentialEntity>> ListCredentialsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<McpHubCredentialEntity>>([]);
        public Task<string?> GetCredentialValueAsync(string providerKey, string credentialKey, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetCredentialValueAsync(string providerKey, string credentialKey, string? value, string? updatedBy, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<McpHubConfigEntity>> ListConfigAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<McpHubConfigEntity>>([]);
        public Task<string?> GetConfigValueAsync(string providerKey, string configKey, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetConfigValueAsync(string providerKey, string configKey, string? value, string? updatedBy, CancellationToken ct = default) => Task.CompletedTask;

        public Task ReplaceTokenEntitlementsAsync(long tokenId, IReadOnlyCollection<string> toolNames, CancellationToken ct = default)
        {
            entitlements[tokenId] = toolNames.ToList();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetTokenEntitlementsAsync(long tokenId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(entitlements.GetValueOrDefault(tokenId) ?? []);

        public Task<bool> IsTokenEntitledAsync(long tokenId, string toolName, CancellationToken ct = default) =>
            Task.FromResult(!entitlements.TryGetValue(tokenId, out var tools) || tools.Contains(toolName));

        public Task CreateAuditAsync(McpHubAuditEntity audit, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<McpHubAuditEntity>> ListAuditAsync(int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<McpHubAuditEntity>>([]);
    }
}
