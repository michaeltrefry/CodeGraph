using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using CodeGraph.Api.Controllers;
using CodeGraph.Data;
using CodeGraph.Services;
using CodeGraph.Services.Indexer;
using CodeGraph.Tests.Extractors;
using CodeGraph.Tests.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Api;

public sealed class GitHubWebhookEndpointTests
{
    private const string Secret = "webhook-endpoint-secret";

    [Fact]
    public async Task SignedDeliveryReachesTheWebhookWithoutUserAuthentication()
    {
        using var host = await StartHostAsync();
        const string body = """{"zen":"Keep it logically awesome."}""";

        var context = await host.GetTestServer().SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Post;
            ctx.Request.Path = "/api/webhooks/github";
            ctx.Request.ContentType = "application/json";
            ctx.Request.Headers["X-GitHub-Event"] = "ping";
            ctx.Request.Headers["X-Hub-Signature-256"] = "sha256=" + Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        });

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).ShouldContain("ignored");
    }

    [Fact]
    public async Task UnsignedDeliveryIsRefusedByTheWebhook()
    {
        using var host = await StartHostAsync();

        var context = await host.GetTestServer().SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Post;
            ctx.Request.Path = "/api/webhooks/github";
            ctx.Request.Headers["X-GitHub-Event"] = "push";
            ctx.Request.Body = new MemoryStream("{}"u8.ToArray());
        });

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).ShouldContain("invalid_signature");
    }

    // Mirrors the production API pipeline shape: authentication + an authorization fallback policy
    // that requires an authenticated user for every endpoint not explicitly opted out.
    private static Task<IHost> StartHostAsync() =>
        new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddControllers()
                        .AddApplicationPart(typeof(GitHubWebhookController).Assembly);
                    var options = new RepositorySourceOptions();
                    options.GitHub.WebhookSecret = Secret;
                    var graph = new InMemoryGraphStore();
                    services.AddSingleton(new GitHubPushWebhookService(
                        Options.Create(options),
                        graph,
                        new ExclusionService(graph, Microsoft.Extensions.Logging.Abstractions.NullLogger<ExclusionService>.Instance),
                        new StandaloneIndexerOperationsService(
                            new StandaloneIndexerOperationsServiceTests.FakeIndexerRunStore(),
                            new StandaloneIndexerOperationsServiceTests.FakeDatabaseSourceStore(),
                            new StandaloneIndexerOperationsServiceTests.RecordingBackgroundRunner()),
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubPushWebhookService>.Instance));
                    services.AddAuthentication(RejectingAuthHandler.Scheme)
                        .AddScheme<AuthenticationSchemeOptions, RejectingAuthHandler>(RejectingAuthHandler.Scheme, _ => { });
                    services.AddAuthorization(options =>
                        options.FallbackPolicy = new AuthorizationPolicyBuilder(RejectingAuthHandler.Scheme)
                            .RequireAuthenticatedUser()
                            .Build());
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }))
            .StartAsync();

    private sealed class RejectingAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public new const string Scheme = "Rejecting";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
