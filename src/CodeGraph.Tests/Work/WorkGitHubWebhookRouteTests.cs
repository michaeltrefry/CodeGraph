using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeGraph.Api.Controllers;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Indexer;
using CodeGraph.Services.Work;
using CodeGraph.Tests.Extractors;
using CodeGraph.Tests.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The real webhook route with the work store behind it (sc-27870): a signed delivery moves the
/// item, a forged or unsigned one changes nothing, and a push to a feature branch records evidence
/// without touching the indexing answer.
/// </summary>
public sealed class WorkGitHubWebhookRouteTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private const string Secret = "work-evidence-secret";

    private async Task<IHost> StartHostAsync() =>
        await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddControllers().AddApplicationPart(typeof(GitHubWebhookController).Assembly);
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
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubPushWebhookService>.Instance,
                        GitHubWorkEvidenceService.ForStore(db.Store)));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }))
            .StartAsync();

    private static string Sign(byte[] body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body));

    private static async Task<(int Status, string Body)> PostAsync(IHost host, string eventName, string text, string? signature, string? delivery)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var context = await host.GetTestServer().SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Post;
            ctx.Request.Path = "/api/webhooks/github";
            ctx.Request.ContentType = "application/json";
            ctx.Request.Headers["X-GitHub-Event"] = eventName;
            if (signature is not null)
                ctx.Request.Headers["X-Hub-Signature-256"] = signature;
            if (delivery is not null)
                ctx.Request.Headers["X-GitHub-Delivery"] = delivery;
            ctx.Request.Body = new MemoryStream(body);
        });
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static string Fixture(string name, string key) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GitHub", name + ".json")).Replace("__KEY__", key)
            .Replace("/pull/7", "/pull/9007").Replace("/pulls/7", "/pulls/9007").Replace("\"number\": 7", "\"number\": 9007");

    [Fact]
    public async Task Signed_deliveries_drive_an_item_from_Ready_to_merged_and_forged_or_unsigned_ones_change_nothing()
    {
        var project = await db.NewProjectAsync("GW", "sceneworks");
        var item = await db.NewItemAsync(project, "Webhook driven");
        var key = item.GetProperty("key").GetString()!;
        using var host = await StartHostAsync();

        async Task<(int Status, string Body)> Send(string eventName, string fixture, string delivery, bool signed = true, bool forged = false)
        {
            var text = Fixture(fixture, key);
            var signature = !signed ? null : Sign(Encoding.UTF8.GetBytes(forged ? text + " " : text));
            return await PostAsync(host, eventName, text, signature, delivery);
        }

        // Forged, unsigned: refused, nothing moves.
        (await Send("push", "push", "d-forged", forged: true)).Status.ShouldBe(StatusCodes.Status401Unauthorized);
        (await Send("push", "push", "d-unsigned", signed: false)).Status.ShouldBe(StatusCodes.Status401Unauthorized);
        (await db.CallAsync(WorkTools.GetWork, new { id = key })).GetProperty("status").GetString().ShouldBe("ready");

        // The push is to a feature branch of a repository that is not indexed: answered as before
        // (nothing to index) with the evidence on top.
        var push = await Send("push", "push", "d-1");
        push.Status.ShouldBe(StatusCodes.Status200OK);
        using (var answer = JsonDocument.Parse(push.Body))
        {
            answer.RootElement.GetProperty("status").GetString().ShouldBe("recorded");
            answer.RootElement.GetProperty("evidence")[0].GetProperty("key").GetString().ShouldBe(key);
        }

        (await Send("pull_request", "pull_request.opened", "d-2")).Status.ShouldBe(StatusCodes.Status200OK);
        var opened = await db.CallAsync(WorkTools.GetWork, new { id = key });
        opened.GetProperty("status").GetString().ShouldBe("in_review");
        opened.GetProperty("links").GetArrayLength().ShouldBe(2);

        // The same delivery again, as GitHub redelivers: nothing more is written.
        var feed = (await db.AllChangesAsync(project)).Count;
        (await Send("push", "push", "d-1")).Status.ShouldBe(StatusCodes.Status200OK);
        (await Send("pull_request", "pull_request.opened", "d-2")).Status.ShouldBe(StatusCodes.Status200OK);
        (await db.AllChangesAsync(project)).Count.ShouldBe(feed);

        (await Send("pull_request", "pull_request.closed.merged", "d-3")).Status.ShouldBe(StatusCodes.Status200OK);
        var merged = await db.CallAsync(WorkTools.GetWork, new { id = key });
        merged.GetProperty("status").GetString().ShouldBe("in_review");
        merged.GetProperty("merged_awaiting_acceptance").GetBoolean().ShouldBeTrue();

        // An event that names no item is still a plain ignored delivery.
        var ping = await PostAsync(host, "ping", """{"zen":"x"}""", Sign(Encoding.UTF8.GetBytes("""{"zen":"x"}""")), "d-4");
        ping.Status.ShouldBe(StatusCodes.Status200OK);
        ping.Body.ShouldContain("ignored");
    }
}
