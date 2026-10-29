using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The read-only listings of the work UI (sc-27874) over the real REST pipeline and MariaDB: sessions,
/// resources with holders and waiters, and messages by channel. For people signed in; a personal
/// access token is not accepted, and unknown parameters are refused.
/// </summary>
public sealed class WorkViewsRestMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>, IAsyncLifetime
{
    private WorkRestHost host = null!;
    private string token = null!;

    public async Task InitializeAsync()
    {
        host = await WorkRestHost.StartAsync(db.Service, db.Store);
        token = await host.CreateTokenAsync("agent");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private Task<(int Status, JsonElement Body)> Get(string path, string? person = "michael") =>
        host.SendAsync(HttpMethod.Get, path, person: person);

    [Fact]
    public async Task Sessions_resources_and_messages_are_listed_with_their_actors()
    {
        var project = await db.NewProjectAsync("VW");
        var item = (await db.NewItemAsync(project, "Thread item")).GetProperty("key").GetString()!;
        var started = await db.CallAsync(WorkTools.StartSession, new { project, harness = "claude-code", model = "opus", host = "mac" }, "agent");
        var session = started.GetProperty("session").GetProperty("id").GetString()!;
        await db.CallAsync(WorkTools.Acquire, new { session_id = session, project, resource = "gpu", purpose = "training", ttl_seconds = 600 }, "agent");
        await db.CallAsync(WorkTools.Acquire, new { project, resource = "gpu", purpose = "queued", wait_seconds = 5 }, "other");
        await db.CallAsync(WorkTools.Post, new { project, item, body = "Thread note", kind = "info" }, "michael");
        await db.CallAsync(WorkTools.Post, new { session_id = session, to_human = true, kind = "question", blocking = false, body = "Which gpu?" }, "agent");
        await db.CallAsync(WorkTools.Post, new { project, body = "Announcement", kind = "announcement" }, "agent");

        var (sessionsStatus, sessions) = await Get($"/api/work/projects/{project}/sessions");
        sessionsStatus.ShouldBe(200);
        var first = sessions.GetProperty("items")[0];
        first.GetProperty("id").GetString().ShouldBe(session);
        first.GetProperty("harness").GetString().ShouldBe("claude-code");
        first.GetProperty("live").GetBoolean().ShouldBeTrue();

        var (resourcesStatus, resources) = await Get($"/api/work/projects/{project}/resources");
        resourcesStatus.ShouldBe(200);
        var gpu = resources.GetProperty("items")[0];
        gpu.GetProperty("name").GetString().ShouldBe("gpu");
        gpu.GetProperty("holders")[0].GetProperty("holder").GetProperty("harness").GetString().ShouldBe("claude-code");
        gpu.GetProperty("waiters")[0].GetProperty("purpose").GetString().ShouldBe("queued");

        var (_, all) = await Get($"/api/work/projects/{project}/messages");
        all.GetProperty("items").GetArrayLength().ShouldBe(3);
        all.GetProperty("items")[0].GetProperty("body").GetString().ShouldBe("Announcement");

        var (_, thread) = await Get($"/api/work/projects/{project}/messages?item={item}");
        thread.GetProperty("items").GetArrayLength().ShouldBe(1);
        thread.GetProperty("items")[0].GetProperty("from").GetProperty("name").GetString().ShouldBe("michael");

        var (_, open) = await Get($"/api/work/projects/{project}/messages?open=true&to_human=true");
        open.GetProperty("items").GetArrayLength().ShouldBe(1);
        open.GetProperty("items")[0].GetProperty("from").GetProperty("harness").GetString().ShouldBe("claude-code");

        var (_, page) = await Get($"/api/work/projects/{project}/messages?limit=2");
        page.GetProperty("items").GetArrayLength().ShouldBe(2);
        var before = page.GetProperty("next_before").GetString()!;
        var (_, older) = await Get($"/api/work/projects/{project}/messages?limit=2&before={before}");
        older.GetProperty("items").GetArrayLength().ShouldBe(1);
        older.TryGetProperty("next_before", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Get_work_takes_detail_over_rest_like_the_tool()
    {
        var project = await db.NewProjectAsync("VD");
        var item = (await db.NewItemAsync(project, "Detailed")).GetProperty("key").GetString()!;

        var (full, body) = await Get($"/api/work/{item}?detail=full");
        full.ShouldBe(200);
        body.GetProperty("key").GetString().ShouldBe(item);

        var (bad, refusal) = await Get($"/api/work/{item}?detail=everything");
        bad.ShouldBe(400);
        refusal.GetProperty("code").GetString().ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task GitHub_evidence_of_an_item_is_listed_with_author_state_and_review_verdict()
    {
        var project = await db.NewProjectAsync("VG", "codegraph");
        var key = (await db.NewItemAsync(project, "Evidence")).GetProperty("key").GetString()!;
        var url = "https://github.com/o/r/pull/7";
        await db.Store.RecordGitHubEvidenceAsync(new GitHubEvidenceSpec(Guid.NewGuid().ToString(), GitHubEvidenceKind.PullRequestMerged, ["codegraph"], [key], "octocat",
            Branch: "claude/x", PullRequestUrl: url, PullRequestNumber: 7, Title: $"{key} add it", Url: url));
        await db.Store.RecordGitHubEvidenceAsync(new GitHubEvidenceSpec(Guid.NewGuid().ToString(), GitHubEvidenceKind.Review, ["codegraph"], [key], "reviewer",
            PullRequestUrl: url, PullRequestNumber: 7, Url: url, ReviewState: "changes_requested", Body: "Needs a test"));

        await db.Store.RecordGitHubEvidenceAsync(new GitHubEvidenceSpec(Guid.NewGuid().ToString(), GitHubEvidenceKind.Push, ["codegraph"], [key], "pusher",
            Branch: "claude/x", BranchUrl: "https://github.com/o/r/tree/claude/x", Url: "https://github.com/o/r/commit/abcdef1234567", ExternalId: "abcdef1234567",
            Body: "abcdef1234567 Add the thing"));

        var (status, body) = await Get($"/api/work/projects/{project}/items/{key}/github-evidence");
        status.ShouldBe(200);
        var items = body.GetProperty("items");
        items.GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);
        var review = items.EnumerateArray().Single(entry => entry.GetProperty("kind").GetString() == "review");
        review.GetProperty("author").GetString().ShouldEndWith("reviewer");
        review.GetProperty("review_state").GetString().ShouldBe("changes_requested");
        review.GetProperty("pull_request").GetInt32().ShouldBe(7);

        var push = items.EnumerateArray().Single(entry => entry.GetProperty("kind").GetString() == "push");
        push.GetProperty("external_id").GetString().ShouldBe("abcdef1234567");
        push.GetProperty("body").GetString().ShouldBe("abcdef1234567 Add the thing");

        (await Get($"/api/work/projects/{project}/items/{key}/github-evidence?bogus=1")).Status.ShouldBe(400);
        (await Get($"/api/work/projects/{project}/items/NOPE-9/github-evidence")).Status.ShouldBe(404);
        var other = await db.NewProjectAsync("VH");
        (await Get($"/api/work/projects/{other}/items/{key}/github-evidence")).Status.ShouldBe(404);
        (await host.SendAsync(HttpMethod.Get, $"/api/work/projects/{project}/items/{key}/github-evidence", token: token)).Status.ShouldBeOneOf(401, 403);
    }

    [Fact]
    public async Task Resources_are_paged_and_an_items_changes_are_read_on_their_own()
    {
        var project = await db.NewProjectAsync("VP");
        foreach (var name in new[] { "a-gpu", "b-gpu", "c-gpu" })
            await db.CallAsync(WorkTools.Acquire, new { project, resource = name, purpose = "p", ttl_seconds = 60 }, "michael");

        var (_, first) = await Get($"/api/work/projects/{project}/resources?limit=2");
        first.GetProperty("items").GetArrayLength().ShouldBe(2);
        var next = first.GetProperty("next_after").GetString()!;
        next.ShouldBe("b-gpu");
        var (_, second) = await Get($"/api/work/projects/{project}/resources?limit=2&after={next}");
        second.GetProperty("items").GetArrayLength().ShouldBe(1);
        second.TryGetProperty("next_after", out _).ShouldBeFalse();

        var item = (await db.NewItemAsync(project, "Moves")).GetProperty("key").GetString()!;
        var other = (await db.NewItemAsync(project, "Other")).GetProperty("key").GetString()!;
        var version = (await db.CallAsync(WorkTools.GetWork, new { id = item })).GetProperty("version").GetInt64();
        await db.UpdateAsync(item, new { expected_version = version, status = "in_progress" }, "michael");
        await db.UpdateAsync(other, new { expected_version = 1, status = "in_progress" }, "michael");

        var (status, feed) = await Get($"/api/work/projects/{project}/items/{item}/changes");
        status.ShouldBe(200);
        var entries = feed.GetProperty("items").EnumerateArray().ToList();
        entries.Select(entry => entry.GetProperty("entity_key").GetString()).Distinct().ShouldBe([item]);
        entries.Select(entry => entry.GetProperty("kind").GetString()).ShouldBe(["item_created", "item_updated"]);
        var (_, recent) = await Get($"/api/work/projects/{project}/recent-changes?limit=2");
        recent.GetProperty("items").EnumerateArray().Select(entry => entry.GetProperty("entity_key").GetString()).ShouldBe([item, other]);
        var (_, newest) = await Get($"/api/work/projects/{project}/items/{item}/changes?limit=1");
        newest.GetProperty("items").EnumerateArray().Single().GetProperty("kind").GetString().ShouldBe("item_updated");
    }

    [Theory]
    [InlineData("messages?limit=0")]
    [InlineData("messages?limit=201")]
    [InlineData("messages?limit=")]
    [InlineData("messages?limit=abc")]
    [InlineData("messages?open=maybe")]
    [InlineData("messages?open=")]
    [InlineData("sessions?limit=-1")]
    [InlineData("sessions?live_only=nope")]
    [InlineData("resources?limit=500")]
    [InlineData("resources?after=")]
    public async Task Bad_query_values_are_refused_in_the_work_error_envelope_not_clamped(string route)
    {
        var project = await db.NewProjectAsync("VQ");
        var (status, body) = await Get($"/api/work/projects/{project}/{route}");
        status.ShouldBe(400);
        body.GetProperty("code").GetString().ShouldBe("invalid_argument");
        body.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
        if (route.Contains("limit=0") || route.Contains("limit=201") || route.Contains("limit=500") || route.Contains("limit=-1"))
            body.GetProperty("message").GetString()!.ShouldStartWith("limit must be 1 to");
    }

    [Fact]
    public async Task The_listings_are_strict_and_for_signed_in_people_only()
    {
        var project = await db.NewProjectAsync("VX");

        var (unknownStatus, unknown) = await Get($"/api/work/projects/{project}/messages?bogus=1");
        unknownStatus.ShouldBe(400);
        unknown.GetProperty("code").GetString().ShouldBe("invalid_argument");

        var (channelStatus, _) = await Get($"/api/work/projects/{project}/messages?channel=nope");
        channelStatus.ShouldBe(400);

        var (missingStatus, missing) = await Get("/api/work/projects/NOPE/sessions");
        missingStatus.ShouldBe(404);
        missing.GetProperty("code").GetString().ShouldBe("not_found");

        var (itemStatus, _) = await Get($"/api/work/projects/{project}/messages?item=NOPE-1");
        itemStatus.ShouldBe(404);

        var (tokenStatus, _) = await host.SendAsync(HttpMethod.Get, $"/api/work/projects/{project}/resources", token: token);
        tokenStatus.ShouldBeOneOf(401, 403);

        var (anonymousStatus, _) = await Get($"/api/work/projects/{project}/resources", person: null);
        anonymousStatus.ShouldBeOneOf(401, 403);
    }
}
