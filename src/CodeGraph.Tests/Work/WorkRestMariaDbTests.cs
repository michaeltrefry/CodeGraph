using System.Text.Json;
using CodeGraph.Models.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The REST surface of the work tools (E8) over a real pipeline (routing, [Authorize] schemes, the
/// personal access token handler): the same tools with the same strict arguments, attributed to
/// the authenticated user, with statuses that match the error codes.
/// </summary>
public sealed class WorkRestMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>, IAsyncLifetime
{
    private WorkRestHost host = null!;
    private string token = null!;

    public async Task InitializeAsync()
    {
        host = await WorkRestHost.StartAsync(db.Service);
        token = await host.CreateTokenAsync("rest-user");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private async Task<(int Status, JsonElement Body)> Call(HttpMethod method, string path, object? body = null, string? query = null) =>
        await host.SendAsync(method, path + query, body is null ? (method == HttpMethod.Get ? null : "{}") : JsonSerializer.Serialize(body, WorkJson.Options), token);

    private Task<(int Status, JsonElement Body)> Create(object body) => Call(HttpMethod.Post, "/api/work", body);

    private Task<(int Status, JsonElement Body)> Find(object body) => Call(HttpMethod.Post, "/api/work/find", body);

    private Task<(int Status, JsonElement Body)> Next(object body) => Call(HttpMethod.Post, "/api/work/next", body);

    private Task<(int Status, JsonElement Body)> Get(string id) => Call(HttpMethod.Get, $"/api/work/{id}");

    private Task<(int Status, JsonElement Body)> Update(string id, object body) => Call(HttpMethod.Patch, $"/api/work/{id}", body);

    private Task<(int Status, JsonElement Body)> Changes(string query) => Call(HttpMethod.Get, "/api/work/changes", query: query);

    [Fact]
    public async Task Every_tool_is_reachable_over_rest_and_writes_are_attributed_to_the_caller()
    {
        var key = "RS" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var (created, project) = await Create(new { new_project = new { key, name = "REST" } });
        created.ShouldBe(200);
        project.GetProperty("project").GetProperty("created_by").GetString().ShouldBe("rest-user");

        var (_, item) = await Create(new { project = key, item = new { title = "Over REST", status = "ready" } });
        var itemKey = item.GetProperty("item").GetProperty("key").GetString()!;

        var (_, got) = await Get(itemKey);
        got.GetProperty("title").GetString().ShouldBe("Over REST");

        var (_, found) = await Find(new { project = key, ready = true });
        found.GetProperty("items").GetArrayLength().ShouldBe(1);

        var (_, next) = await Next(new { project = key, marker = "rest" });
        var version = next.GetProperty("item").GetProperty("version").GetInt64();

        var (updatedStatus, updated) = await Update(itemKey, new { expected_version = version, status = "in_progress" });
        updatedStatus.ShouldBe(200);
        updated.GetProperty("item").GetProperty("status").GetString().ShouldBe("in_progress");

        var (_, changes) = await Changes($"?project={key}&from_start=true&limit=10");
        var entries = changes.GetProperty("items").EnumerateArray().ToList();
        entries.Select(entry => entry.GetProperty("kind").GetString()).ShouldBe(["project_created", "item_created", "claimed", "item_updated"]);
        entries.ShouldAllBe(entry => entry.GetProperty("actor").GetProperty("name").GetString() == "rest-user");
    }

    [Fact]
    public async Task Refusals_carry_their_code_and_a_matching_status()
    {
        var key = "RE" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        await Create(new { new_project = new { key, name = "Errors" } });
        var (_, item) = await Create(new { project = key, item = new { title = "Item" } });
        var itemKey = item.GetProperty("item").GetProperty("key").GetString()!;

        var (unknownStatus, unknown) = await Update(itemKey, new { expected_version = 1, titel = "x" });
        (unknownStatus, unknown.GetProperty("code").GetString()).ShouldBe((400, "invalid_argument"));
        unknown.GetProperty("message").GetString()!.ShouldContain("titel");

        var (missingStatus, missing) = await Get($"{key}-404");
        (missingStatus, missing.GetProperty("code").GetString()).ShouldBe((404, "not_found"));

        await Update(itemKey, new { expected_version = 1, title = "First" });
        var (staleStatus, stale) = await Update(itemKey, new { expected_version = 1, title = "Second" });
        (staleStatus, stale.GetProperty("code").GetString()).ShouldBe((409, "conflict"));
        stale.GetProperty("current").GetProperty("title").GetString().ShouldBe("First");

        var (queryStatus, query) = await Changes("?projct=X");
        (queryStatus, query.GetProperty("code").GetString()).ShouldBe((400, "invalid_argument"));
        query.GetProperty("message").GetString()!.ShouldContain("projct");

        var (typeStatus, _) = await Changes("?limit=many");
        typeStatus.ShouldBe(400);
        var (idStatus, _) = await Update(itemKey, new { id = "other", expected_version = 2, title = "x" });
        idStatus.ShouldBe(400);
    }

    [Fact]
    public async Task A_person_signed_in_on_the_web_is_attributed_and_not_subject_to_token_entitlements()
    {
        var key = "RP" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var (status, project) = await host.SendAsync(HttpMethod.Post, "/api/work",
            "{\"new_project\":{\"key\":\"" + key + "\",\"name\":\"Person\"}}", person: "michael");

        status.ShouldBe(200);
        project.GetProperty("project").GetProperty("created_by").GetString().ShouldBe("michael");
    }

    [Fact]
    public async Task A_plan_and_a_single_item_accept_a_differently_cased_repository_and_store_the_canonical_one()
    {
        var key = "RC" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        // The project names its repository in another case than the repositories table spells it.
        var (_, project) = await Create(new { new_project = new { key, name = "Case", repositories = new[] { new { repository = "SceneWorks", role = "owned" } } } });
        project.GetProperty("project").GetProperty("repositories")[0].GetProperty("repository").GetString().ShouldBe("sceneworks");

        var (_, single) = await Create(new { project = key, item = new { title = "Single", repository = "SCENEWORKS" } });
        single.GetProperty("item").GetProperty("repository").GetString().ShouldBe("sceneworks");

        var (planStatus, plan) = await Create(new
        {
            project = key,
            idempotency_key = Guid.NewGuid().ToString("N"),
            plan = new { items = new[] { new { @ref = "a", title = "Planned", repository = "sceneWORKS" } } }
        });
        planStatus.ShouldBe(200);
        var planned = plan.GetProperty("items").GetProperty("a").GetProperty("key").GetString()!;
        (await Get(planned)).Body.GetProperty("repository").GetString().ShouldBe("sceneworks");

        var (_, moved) = await Update(single.GetProperty("item").GetProperty("key").GetString()!,
            new { expected_version = 1, repository = "SceneWorks" });
        moved.GetProperty("item").GetProperty("repository").GetString().ShouldBe("sceneworks");

        var (unownedStatus, unowned) = await Create(new { project = key, item = new { title = "Elsewhere", repository = "INFERENCE" } });
        (unownedStatus, unowned.GetProperty("code").GetString()).ShouldBe((400, "invalid_argument"));
    }
}
