using System.Text.Json;
using CodeGraph.Services.Work.Imports;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// sc-28194: KanbanBoard is read through its MCP server, the only endpoint a KanbanBoard PAT authenticates. The
/// payloads below are the production tools' answers (2026-10-31), cut down; the tools answer the REST API's JSON.
/// </summary>
public sealed class KanbanMcpSourceTests
{
    private const string EpicId = "25860fbc-3b64-4c44-8dea-7e1bc8723a7c";

    private const string Epic = """
        {"id":"25860fbc-3b64-4c44-8dea-7e1bc8723a7c","projectId":"ed495ae1-9020-417d-9175-bf83162f1820","name":"SceneWorks Code Review 2026-10-27",
         "description":"Remediation backlog.","isArchived":false,"createdAtUtc":"2026-10-27T19:41:15.436505+00:00","updatedAtUtc":"2026-10-27T19:41:15.436505+00:00"}
        """;

    private const string Documents = """
        [{"id":"dc1fd42b-99f3-4023-9804-3c3f18a2c64f","epicId":"25860fbc-3b64-4c44-8dea-7e1bc8723a7c","title":"Requirements","body":"# Requirements"}]
        """;

    private static string Item(string id, string title, string status, string? blocker = null) => $$"""
        {"id":"{{id}}","projectId":"ed495ae1-9020-417d-9175-bf83162f1820","epicId":"{{EpicId}}","title":"{{title}}","description":"Where: auth.rs.",
         "type":"Issue","status":"{{status}}","priority":"High","order":0,"labels":"security,sev-high","label_list":["security","sev-high"],
         "comments":[{"id":"c1","author":"claude","body":"Looked at it.","createdAtUtc":"2026-10-28T10:00:00+00:00"}],
         "createdAtUtc":"2026-10-27T19:41:15.436505+00:00","updatedAtUtc":"2026-10-27T19:41:15.436505+00:00",
         "blockers":[{{(blocker is null ? "" : $$"""{"id":"{{blocker}}","status":"Backlog"}""")}}],"dependents":[],
         "links":[{"kind":"pr","url":"https://github.com/SceneWorks/SceneWorks/pull/3220"}],"version":1,
         "url":"https://kanban.trefry.net/stories/{{id}}"}
        """;

    private sealed record Call(string Tool, IReadOnlyDictionary<string, object?> Arguments);

    private static (KanbanMcpSource Source, List<Call> Calls) Source(Func<Call, string?> answer)
    {
        var calls = new List<Call>();
        return (new KanbanMcpSource((tool, arguments, _) =>
        {
            var call = new Call(tool, arguments);
            calls.Add(call);
            var json = answer(call);
            return Task.FromResult<JsonElement?>(json is null ? null : JsonDocument.Parse(json).RootElement.Clone());
        }), calls);
    }

    [Fact]
    public async Task The_epic_documents_and_every_page_of_items_including_closed_ones_are_read_through_the_tools()
    {
        var (source, calls) = Source(call => call.Tool switch
        {
            "get_epic" => Epic,
            "list_epic_documents" => Documents,
            "list_work_items" when call.Arguments.ContainsKey("status") => $$"""{"items":[{{Item("i3", "[F-003] Closed one", "Closed")}}]}""",
            "list_work_items" when call.Arguments.ContainsKey("cursor") => $$"""{"items":[{{Item("i2", "[F-002] Second", "Ready", blocker: "i1")}}]}""",
            "list_work_items" => $$"""{"items":[{{Item("i1", "[F-001] First", "Backlog")}}],"next_cursor":"page-2"}""",
            _ => throw new InvalidOperationException(call.Tool)
        });

        var epic = (await source.GetEpicAsync(EpicId, CancellationToken.None))!;
        epic.Name.ShouldBe("SceneWorks Code Review 2026-10-27");
        epic.ProjectId.ShouldBe("ed495ae1-9020-417d-9175-bf83162f1820");
        (await source.ListEpicDocumentsAsync(EpicId, CancellationToken.None)).Single().Title.ShouldBe("Requirements");

        var items = await source.ListItemsAsync(EpicId, CancellationToken.None);
        items.Select(item => item.Id).ShouldBe(["i1", "i2", "i3"]);
        items[1].Blockers.ShouldBe(["i1"]);
        items[0].Comments.Single().Author.ShouldBe("claude");
        items[0].Links.Single().Kind.ShouldBe("pr");
        items[0].Labels.ShouldBe(["security", "sev-high"]);
        items[2].Status.ShouldBe("Closed");

        // The tools and arguments KanbanBoard's MCP server names: each page passes the epic, a page size, comments
        // and the cursor of the page before; the Closed items are asked for separately, since the list hides them.
        calls.Select(call => call.Tool).ShouldBe(["get_epic", "list_epic_documents", "list_work_items", "list_work_items", "list_work_items"]);
        calls.ShouldAllBe(call => (string)call.Arguments["epicId"]! == EpicId);
        calls[2].Arguments["limit"].ShouldBe(200);
        calls[2].Arguments["include_comments"].ShouldBe(true);
        calls[3].Arguments["cursor"].ShouldBe("page-2");
        calls[4].Arguments["status"].ShouldBe("Closed");
    }

    [Fact]
    public void A_not_found_answer_is_no_object_and_any_other_error_is_raised()
    {
        CallToolResult Result(string text, bool error) => new() { Content = [new TextContentBlock { Text = text }], IsError = error };

        KanbanMcpSource.Read("get_epic", Result("""{"code":"not_found","message":"No epic."}""", error: true)).ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => KanbanMcpSource.Read("get_epic", Result("""{"code":"forbidden"}""", error: true)))
            .Message.ShouldContain("get_epic");
        KanbanMcpSource.Read("get_epic", Result(Epic, error: false))!.Value.GetProperty("name").GetString().ShouldBe("SceneWorks Code Review 2026-10-27");
    }
}
