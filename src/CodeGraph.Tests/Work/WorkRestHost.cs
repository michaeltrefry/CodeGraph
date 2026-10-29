using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CodeGraph.Api.Auth;
using CodeGraph.Api.Controllers;
using CodeGraph.Data;
using CodeGraph.Mcp.Hub;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Work;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The REST surface of the work tools as a client reaches it: a test server running the real
/// routing, the real <see cref="WorkController"/> with its [Authorize] schemes and the real personal
/// access token authentication handler over in-memory token and hub stores. Only the web sign-in
/// scheme is a stand-in (a request with <c>X-Test-User</c> is that signed-in person).
/// </summary>
internal sealed class WorkRestHost : IAsyncDisposable
{
    private readonly IHost host;

    private WorkRestHost(IHost host, InMemoryMcpHubStore hub, McpPersonalAccessTokenService tokens)
    {
        this.host = host;
        Hub = hub;
        Tokens = tokens;
        Client = host.GetTestClient();
    }

    public InMemoryMcpHubStore Hub { get; }

    public McpPersonalAccessTokenService Tokens { get; }

    public HttpClient Client { get; }

    /// <summary>A handler that sends requests into the test server (for clients such as cg).</summary>
    public HttpMessageHandler CreateHandler() => host.GetTestServer().CreateHandler();

    public static async Task<WorkRestHost> StartAsync(IWorkService work)
    {
        var hub = new InMemoryMcpHubStore();
        await new McpHubCatalogSeeder(hub, new InMemoryMcpSensitiveColumnStore()).EnsureCatalogAsync();
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var tokenService = new McpPersonalAccessTokenService(
            new TokenStore(), hub, Options.Create(new CodeGraphStorageOptions { MariaDbEncryptionKey = key }));

        var started = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton(work);
                    services.AddSingleton<IMcpHubStore>(hub);
                    services.AddSingleton(tokenService);
                    services.AddControllers().AddApplicationPart(typeof(WorkController).Assembly);
                    services.AddAuthentication(options =>
                        {
                            options.DefaultAuthenticateScheme = CodeGraphAuthenticationDefaults.Scheme;
                            options.DefaultChallengeScheme = CodeGraphAuthenticationDefaults.Scheme;
                        })
                        .AddScheme<AuthenticationSchemeOptions, SignedInPersonHandler>(CodeGraphAuthenticationDefaults.Scheme, _ => { })
                        .AddScheme<AuthenticationSchemeOptions, McpPatAuthenticationHandler>(McpPatAuthenticationDefaults.Scheme, _ => { });
                    services.AddAuthorization();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }))
            .StartAsync();
        return new WorkRestHost(started, hub, tokenService);
    }

    /// <summary>A personal access token of <paramref name="user"/>: all tools, or only <paramref name="tools"/>.</summary>
    public async Task<string> CreateTokenAsync(string user, params string[] tools)
    {
        var created = await Tokens.CreateForUserAsync(user, "rest test", 30, tools.Length == 0 ? null : tools);
        return created.RawToken ?? throw new InvalidOperationException($"{created.ErrorCode}: {created.ErrorMessage}");
    }

    /// <summary>
    /// Sends a request and answers its status and parsed JSON body (or the raw text under
    /// <c>Raw</c> when it is not JSON). Authenticated with a personal access token, or as a signed-in person.
    /// </summary>
    public async Task<(int Status, JsonElement Body)> SendAsync(
        HttpMethod method,
        string path,
        string? body = null,
        string? token = null,
        string? person = null,
        string? contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = contentType is null ? null : MediaTypeHeaderValue.Parse(contentType);
        }

        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (person is not null)
            request.Headers.Add("X-Test-User", person);

        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonElement parsed;
        try
        {
            parsed = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            parsed = JsonSerializer.SerializeToElement(new { Raw = text });
        }

        return ((int)response.StatusCode, parsed);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await host.StopAsync();
        host.Dispose();
    }

    private sealed class SignedInPersonHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [new Claim("preferred_username", user.ToString()), new Claim(ClaimTypes.Name, user.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class TokenStore : IMcpPersonalAccessTokenStore
    {
        private readonly List<McpPersonalAccessTokenEntity> tokens = [];

        public Task<IReadOnlyList<McpPersonalAccessTokenEntity>> ListMcpPersonalAccessTokensAsync(string username, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpPersonalAccessTokenEntity>>(tokens.Where(token => token.Username == username).ToList());

        public Task<McpPersonalAccessTokenEntity> CreateMcpPersonalAccessTokenAsync(McpPersonalAccessTokenEntity token, CancellationToken ct = default)
        {
            token.Id = tokens.Count + 1;
            tokens.Add(token);
            return Task.FromResult(token);
        }

        public Task<McpPersonalAccessTokenEntity?> GetMcpPersonalAccessTokenByHashAsync(string tokenHash, CancellationToken ct = default) =>
            Task.FromResult(tokens.FirstOrDefault(token => token.TokenHash == tokenHash));

        public Task<bool> RevokeMcpPersonalAccessTokenAsync(string username, long id, DateTime revokedAt, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> SetMcpPersonalAccessTokenEntitlementModeAsync(string username, long id, string entitlementMode, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> UpdateMcpPersonalAccessTokenLastUsedAsync(long id, DateTime lastUsedAt, string? lastUsedFrom, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}
