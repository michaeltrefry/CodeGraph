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
            Options.Create(new CodeGraphStorageOptions { MariaDbEncryptionKey = "not-base64" }));

        var result = await service.CreateForUserAsync("michael", "Desktop", 30);

        result.Created.ShouldBeFalse();
        result.ErrorCode.ShouldBe("pat_configuration_missing");
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
            Options.Create(new CodeGraphStorageOptions { MariaDbEncryptionKey = Convert.ToBase64String(signingKey) }));

        var validation = await service.ValidateAsync(rawToken, "127.0.0.1");

        validation.ShouldNotBeNull();
        validation.Username.ShouldBe("michael");
    }

    private static McpPersonalAccessTokenService CreateService(InMemoryMcpPersonalAccessTokenStore store)
    {
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        return new McpPersonalAccessTokenService(
            store,
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
}
