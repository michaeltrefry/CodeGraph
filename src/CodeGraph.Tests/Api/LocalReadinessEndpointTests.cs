using System.Net;
using System.Text.Encodings.Web;
using CodeGraph.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Api;

public sealed class LocalReadinessEndpointTests
{
    [Fact]
    public async Task LoopbackCallerGetsFixedHealthyBodyWithoutAuthentication()
    {
        using var host = await StartHostAsync();

        var context = await host.GetTestServer().SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Get;
            ctx.Request.Path = LocalReadinessEndpoint.Path;
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).ShouldBe("Healthy");
    }

    [Theory]
    [InlineData(HealthStatus.Unhealthy, StatusCodes.Status503ServiceUnavailable, "Unhealthy")]
    [InlineData(HealthStatus.Degraded, StatusCodes.Status200OK, "Degraded")]
    public async Task LoopbackCallerGetsOnlyTheAggregateStatusOfRegisteredChecks(
        HealthStatus status, int expectedStatusCode, string expectedBody)
    {
        using var host = await StartHostAsync(new HealthCheckResult(
            status, description: "secret-detail", data: new Dictionary<string, object> { ["connection"] = "secret" }));

        var context = await host.GetTestServer().SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Get;
            ctx.Request.Path = LocalReadinessEndpoint.Path;
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        context.Response.StatusCode.ShouldBe(expectedStatusCode);
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).ShouldBe(expectedBody);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.18.0.4")]
    public async Task NonLoopbackCallerGetsNotFound(string remoteAddress)
    {
        using var host = await StartHostAsync();

        var context = await host.GetTestServer().SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Get;
            ctx.Request.Path = LocalReadinessEndpoint.Path;
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        });

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task OtherRoutesStillRequireAuthentication()
    {
        using var host = await StartHostAsync();

        var context = await host.GetTestServer().SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Get;
            ctx.Request.Path = "/protected";
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void IsLoopbackRecognizesMappedAddresses(string address, bool expected) =>
        LocalReadinessEndpoint.IsLoopback(IPAddress.Parse(address)).ShouldBe(expected);

    [Fact]
    public void IsLoopbackRejectsUnknownCaller() =>
        LocalReadinessEndpoint.IsLoopback(null).ShouldBeFalse();

    // Mirrors the production API pipeline shape: authentication + an authorization fallback policy
    // that requires an authenticated user for every endpoint not explicitly opted out.
    private static Task<IHost> StartHostAsync(HealthCheckResult? checkResult = null) =>
        new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    var healthChecks = services.AddHealthChecks();
                    if (checkResult is { } result)
                        healthChecks.AddCheck("bus", () => result);
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
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/protected", () => "secret");
                        endpoints.MapLocalReadinessEndpoint();
                    });
                }))
            .StartAsync();

    private sealed class RejectingAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Scheme = "Rejecting";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
