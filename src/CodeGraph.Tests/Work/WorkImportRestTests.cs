using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The import routes as a client reaches them (sc-27876): attachments of imported items and the memory
/// candidate parser take a token (agents run the verify-before-import flow, D18) and read only
/// checkouts CodeGraph knows. The Shortcut and KanbanBoard import routes are gone (CG-147).
/// </summary>
public sealed class WorkImportRestTests
{
    private static async Task<WorkRestHost> HostAsync() => await WorkRestHost.StartAsync(new WorkService());

    [Fact]
    public async Task The_memory_candidates_route_takes_a_token_and_reads_only_known_checkouts()
    {
        await using var host = await HostAsync();
        var token = await host.CreateTokenAsync("agent");

        var missing = await host.SendAsync(HttpMethod.Post, "/api/work/imports/memory/candidates", """{"harness":"claude"}""", token: token);
        missing.Status.ShouldBe(400);
        missing.Body.GetProperty("code").GetString().ShouldBe(WorkErrorCodes.InvalidArgument);

        // A path is never accepted: the repository is a name CodeGraph resolves, or nothing.
        var path = await host.SendAsync(HttpMethod.Post, "/api/work/imports/memory/candidates",
            JsonSerializer.Serialize(new { harness = "claude", repository = "/etc", files = new[] { new { path = "m.md", content = "x" } } }), token: token);
        path.Status.ShouldBe(400);
        path.Body.GetProperty("code").GetString().ShouldBe(WorkErrorCodes.InvalidArgument);
        var oldShape = await host.SendAsync(HttpMethod.Post, "/api/work/imports/memory/candidates",
            JsonSerializer.Serialize(new { harness = "claude", repository_root = "/", files = new[] { new { path = "m.md", content = "x" } } }), token: token);
        oldShape.Status.ShouldBe(400);

        var parsed = await host.SendAsync(HttpMethod.Post, "/api/work/imports/memory/candidates",
            JsonSerializer.Serialize(new
            {
                harness = "claude",
                files = new[] { new { path = "/memory/build.md", content = "---\nname: Build\ndescription: How to build\ntype: reference\n---\nRun dotnet build in /etc/passwd and ../../secret." } }
            }), token: token);
        parsed.Status.ShouldBe(200);
        var candidate = parsed.Body.GetProperty("candidates").EnumerateArray().Single();
        candidate.GetProperty("verification").GetString().ShouldBe("unverifiable");
        candidate.GetProperty("entry").TryGetProperty("paths", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task The_retired_import_routes_are_gone()
    {
        await using var host = await HostAsync();
        (await host.SendAsync(HttpMethod.Get, "/api/work/imports/shortcut/settings", person: "admin")).Status.ShouldBe(404);
        (await host.SendAsync(HttpMethod.Post, "/api/work/imports/shortcut/runs", """{"mode":"dry_run"}""", person: "admin")).Status.ShouldBe(404);
    }

    [Fact]
    public async Task Attachments_take_a_token()
    {
        await using var host = await HostAsync();
        var token = await host.CreateTokenAsync("agent");
        // Authenticated with the token: refused for want of the store (503), not for the scheme.
        (await host.SendAsync(HttpMethod.Get, $"/api/work/attachments/{Guid.NewGuid()}", token: token)).Status.ShouldBe(503);
        (await host.SendAsync(HttpMethod.Get, $"/api/work/attachments/{Guid.NewGuid()}")).Status.ShouldBe(401);
    }
}
