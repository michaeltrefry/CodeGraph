using System.Security.Claims;
using System.Text.Json;
using CodeGraph.Api.Controllers;
using CodeGraph.Models.Work;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The REST surface of the work tools (E8): the same tools with the same strict arguments,
/// attributed to the authenticated user, with statuses that match the error codes.
/// </summary>
public sealed class WorkRestMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private WorkController Controller(string user = "rest-user", string? query = null)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "Test"))
        };
        if (query is not null)
            context.Request.QueryString = new QueryString(query);
        return new WorkController(db.Service) { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private static (int Status, JsonElement Body) Read(IActionResult result)
    {
        var content = result.ShouldBeOfType<ContentResult>();
        return (content.StatusCode!.Value, JsonDocument.Parse(content.Content!).RootElement);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, WorkJson.Options);

    [Fact]
    public async Task Every_tool_is_reachable_over_rest_and_writes_are_attributed_to_the_caller()
    {
        var key = "RS" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var (created, project) = Read(await Controller().Create(Json(new { new_project = new { key, name = "REST" } })));
        created.ShouldBe(200);
        project.GetProperty("project").GetProperty("created_by").GetString().ShouldBe("rest-user");

        var (_, item) = Read(await Controller().Create(Json(new { project = key, item = new { title = "Over REST", status = "ready" } })));
        var itemKey = item.GetProperty("item").GetProperty("key").GetString()!;

        var (_, got) = Read(await Controller().Get(itemKey));
        got.GetProperty("title").GetString().ShouldBe("Over REST");

        var (_, found) = Read(await Controller().Find(Json(new { project = key, ready = true })));
        found.GetProperty("items").GetArrayLength().ShouldBe(1);

        var (_, next) = Read(await Controller().Next(Json(new { project = key, marker = "rest" })));
        var version = next.GetProperty("item").GetProperty("version").GetInt64();

        var (updatedStatus, updated) = Read(await Controller().Update(itemKey, Json(new { expected_version = version, status = "in_progress" })));
        updatedStatus.ShouldBe(200);
        updated.GetProperty("item").GetProperty("status").GetString().ShouldBe("in_progress");

        var (_, changes) = Read(await Controller(query: $"?project={key}&from_start=true&limit=10").Changes());
        var entries = changes.GetProperty("items").EnumerateArray().ToList();
        entries.Select(entry => entry.GetProperty("kind").GetString()).ShouldBe(["project_created", "item_created", "claimed", "item_updated"]);
        entries.ShouldAllBe(entry => entry.GetProperty("actor").GetProperty("name").GetString() == "rest-user");
    }

    [Fact]
    public async Task Refusals_carry_their_code_and_a_matching_status()
    {
        var key = "RE" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        Read(await Controller().Create(Json(new { new_project = new { key, name = "Errors" } })));
        var (_, item) = Read(await Controller().Create(Json(new { project = key, item = new { title = "Item" } })));
        var itemKey = item.GetProperty("item").GetProperty("key").GetString()!;

        var (unknownStatus, unknown) = Read(await Controller().Update(itemKey, Json(new { expected_version = 1, titel = "x" })));
        (unknownStatus, unknown.GetProperty("code").GetString()).ShouldBe((400, "invalid_argument"));
        unknown.GetProperty("message").GetString()!.ShouldContain("titel");

        var (missingStatus, missing) = Read(await Controller().Get($"{key}-404"));
        (missingStatus, missing.GetProperty("code").GetString()).ShouldBe((404, "not_found"));

        Read(await Controller().Update(itemKey, Json(new { expected_version = 1, title = "First" })));
        var (staleStatus, stale) = Read(await Controller().Update(itemKey, Json(new { expected_version = 1, title = "Second" })));
        (staleStatus, stale.GetProperty("code").GetString()).ShouldBe((409, "conflict"));
        stale.GetProperty("current").GetProperty("title").GetString().ShouldBe("First");

        var (queryStatus, query) = Read(await Controller(query: "?projct=X").Changes());
        (queryStatus, query.GetProperty("code").GetString()).ShouldBe((400, "invalid_argument"));
        query.GetProperty("message").GetString()!.ShouldContain("projct");

        var (typeStatus, _) = Read(await Controller(query: "?limit=many").Changes());
        typeStatus.ShouldBe(400);
        var (idStatus, _) = Read(await Controller().Update(itemKey, Json(new { id = "other", expected_version = 2, title = "x" })));
        idStatus.ShouldBe(400);
    }
}
