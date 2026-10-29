using System.Text.Json;
using CodeGraph.Models.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The knowledge tools over REST (E8, sc-27869): record, recall (body and by id), the import dry
/// run and the briefing's paths, hub-gated per token like the work tools.
/// </summary>
public sealed class KnowledgeRestMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>, IAsyncLifetime
{
    private WorkRestHost host = null!;
    private string token = null!;

    public async Task InitializeAsync()
    {
        host = await WorkRestHost.StartAsync(db.Service);
        token = await host.CreateTokenAsync("rest-user");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private async Task<(int Status, JsonElement Body)> Call(HttpMethod method, string path, object? body = null, string? query = null, string? as_ = null) =>
        await host.SendAsync(method, path + query, body is null ? null : JsonSerializer.Serialize(body, WorkJson.Options), as_ ?? token);

    [Fact]
    public async Task Record_recall_import_and_the_briefing_are_reachable_over_rest()
    {
        var key = "KR" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        (await Call(HttpMethod.Post, "/api/work", new { new_project = new { key, name = "Knowledge over REST" } })).Status.ShouldBe(200);

        var (recordStatus, recorded) = await Call(HttpMethod.Post, "/api/work/knowledge", new { project = key, entry = new { kind = "decision", summary = "Use REST for hooks", paths = new[] { "hooks/brief.sh" } } });
        recordStatus.ShouldBe(200);
        var entry = recorded.GetProperty("entry");
        entry.GetProperty("author").GetProperty("name").GetString().ShouldBe("rest-user");
        var id = entry.GetProperty("id").GetString();

        var (_, recalled) = await Call(HttpMethod.Post, "/api/work/knowledge/recall", new { project = key, paths = new[] { "hooks" } });
        recalled.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(id);
        var (byIdStatus, byId) = await Call(HttpMethod.Get, $"/api/work/knowledge/{id}", query: "?detail=full");
        byIdStatus.ShouldBe(200);
        byId.GetProperty("items")[0].GetProperty("revisions").GetArrayLength().ShouldBe(1);
        (await Call(HttpMethod.Get, $"/api/work/knowledge/{id}", query: "?detial=full")).Status.ShouldBe(400);
        (await Call(HttpMethod.Get, $"/api/work/knowledge/{Guid.NewGuid()}")).Status.ShouldBe(404);

        var (importStatus, preview) = await Call(HttpMethod.Post, "/api/work/knowledge/import", new
        {
            project = key,
            candidates = new[] { new { entry = new { kind = "fact", summary = "Hooks time out in 2 s" }, source_file = "memory/hooks.md", verification = "verified" } }
        });
        importStatus.ShouldBe(200);
        preview.GetProperty("candidates")[0].GetProperty("outcome").GetString().ShouldBe("would_store");

        var (_, started) = await Call(HttpMethod.Post, "/api/work/sessions", new { project = key, harness = "shell" });
        var session = started.GetProperty("session").GetProperty("id").GetString();
        var (briefingStatus, briefing) = await Call(HttpMethod.Get, $"/api/work/sessions/{session}/briefing", query: "?paths=hooks/brief.sh,docs");
        briefingStatus.ShouldBe(200);
        briefing.GetProperty("knowledge").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(id);

        // A token that was not given record cannot call it over REST either.
        var readOnly = await host.CreateTokenAsync("reader", "recall");
        var (deniedStatus, denied) = await Call(HttpMethod.Post, "/api/work/knowledge", new { project = key, entry = new { kind = "fact", summary = "x" } }, as_: readOnly);
        (deniedStatus, denied.GetProperty("code").GetString()).ShouldBe((403, "tool_not_entitled"));
        (await Call(HttpMethod.Post, "/api/work/knowledge/recall", new { project = key }, as_: readOnly)).Status.ShouldBe(200);
    }

    [Fact]
    public async Task Promote_challenge_lessons_review_queue_and_the_briefing_activity_are_reachable_over_rest()
    {
        var key = "KL" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        (await Call(HttpMethod.Post, "/api/work", new { new_project = new { key, name = "Lessons over REST" } })).Status.ShouldBe(200);
        var (_, rule) = await Call(HttpMethod.Post, "/api/work/knowledge", new
        {
            project = key,
            entry = new { kind = "lesson", summary = "Never push to main", source = new { kind = "prompt" }, applies_when = new { activity = new[] { "merging" } } }
        });
        var ruleId = rule.GetProperty("entry").GetProperty("id").GetString();
        var (_, candidate) = await Call(HttpMethod.Post, "/api/work/knowledge", new
        {
            project = key,
            entry = new { kind = "lesson", summary = "Squash fixups before review", source = new { kind = "agent" }, applies_when = new { activity = new[] { "reviewing" } } }
        });
        var candidateEntry = candidate.GetProperty("entry");

        var (promoteStatus, promoted) = await Call(HttpMethod.Post, $"/api/work/knowledge/{candidateEntry.GetProperty("id").GetString()}/promote",
            new { expected_version = candidateEntry.GetProperty("version").GetInt64(), action = "propose" });
        promoteStatus.ShouldBe(200);
        promoted.GetProperty("entry").GetProperty("lesson").GetProperty("state").GetString().ShouldBe("proposed");

        var (challengeStatus, _) = await Call(HttpMethod.Post, "/api/work/knowledge/challenges",
            new { open = new { id = ruleId, reason = "Release branches need it", evidence = new[] { new { kind = "link", summary = "RELEASING.md", @ref = "docs/RELEASING.md" } } } });
        challengeStatus.ShouldBe(200);

        var (queueStatus, queue) = await Call(HttpMethod.Get, "/api/work/knowledge/review-queue", query: $"?project={key}&limit=10");
        queueStatus.ShouldBe(200);
        queue.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("kind").GetString()).ShouldBe(["challenge", "proposed_rule"]);
        (await Call(HttpMethod.Get, "/api/work/knowledge/review-queue", query: $"?project={key}&lmit=10")).Status.ShouldBe(400);

        var (lessonsStatus, lessons) = await Call(HttpMethod.Post, "/api/work/knowledge/lessons", new { project = key, view = "recently_learned" });
        lessonsStatus.ShouldBe(200);
        lessons.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(ruleId);

        var (_, started) = await Call(HttpMethod.Post, "/api/work/sessions", new { project = key, harness = "shell" });
        var session = started.GetProperty("session").GetProperty("id").GetString();
        started.GetProperty("briefing").GetProperty("rule_activities").EnumerateArray().Select(value => value.GetString()).ShouldBe(["merging"]);
        var (_, briefing) = await Call(HttpMethod.Get, $"/api/work/sessions/{session}/briefing", query: "?activity=merging,testing");
        var served = briefing.GetProperty("rules").EnumerateArray().Single();
        served.GetProperty("id").GetString().ShouldBe(ruleId);
        served.GetProperty("challenges").GetArrayLength().ShouldBe(1);
    }
}
