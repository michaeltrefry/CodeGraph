using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed partial class DreamingRunAcceptanceMariaDbTests
{
    [Fact]
    public async Task Trail_discovery_is_compact_newest_first_and_selected_full_content_remains_exact()
    {
        var project = await ProjectAsync();
        var item = await db.NewItemAsync(project.ToString(), "Evidence selection");
        var body = "Distinct full evidence: " + new string('x', 3900);
        for (var index = 0; index < 8; index++)
        {
            db.Clock.Advance(TimeSpan.FromSeconds(1));
            await db.CallAsync(WorkTools.Post, new { project, item = item.GetProperty("key").GetString(), kind = "info", body = body + index });
        }
        var actions = (IKnowledgeDreamActions)db.Service;
        using var scope = DreamExecutionScope.Begin(Guid.NewGuid(), project, "scripted");
        var summaryPage = await ReadBoundedAsync(actions, "dream_work_trail", new { kind = "message", item = item.GetProperty("id").GetGuid() });
        var summaries = await FinishBoundedReadAsync(actions, "dream_work_trail", summaryPage);
        summaries.Count.ShouldBe(8);
        summaries.Select(row => row.GetProperty("created_at").GetDateTime()).ShouldBeInOrder(SortDirection.Descending);
        summaries.ShouldAllBe(row => row.GetProperty("preview_truncated").GetBoolean() && row.GetProperty("preview").GetString()!.Length == 240);
        summaries.Any(row => row.TryGetProperty("data", out _)).ShouldBeFalse();

        var selected = summaries[0];
        var fullPage = await ReadBoundedAsync(actions, "dream_work_trail", selected.GetProperty("read").GetProperty("arguments"));
        var full = (await FinishBoundedReadAsync(actions, "dream_work_trail", fullPage)).Single();
        full.GetProperty("id").GetString().ShouldBe(selected.GetProperty("id").GetString());
        full.GetProperty("data").GetProperty("body").GetString().ShouldBe(body + "7");
        summaries.Sum(row => row.GetRawText().Length).ShouldBeLessThan(8 * full.GetRawText().Length / 2);
        var bySince = await ReadBoundedAsync(actions, "dream_work_trail", new { kind = "message", since = selected.GetProperty("created_at").GetDateTime() });
        bySince.GetProperty("total_count").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Trail_drilldown_rejects_changed_hash_foreign_ids_and_invalid_selectors()
    {
        var project = await ProjectAsync();
        var other = await ProjectAsync("inference");
        await db.CallAsync(WorkTools.Post, new { project, kind = "info", body = "Original evidence" });
        var foreign = await RecordAsync(other, "Foreign evidence");
        var actions = (IKnowledgeDreamActions)db.Service;
        using var scope = DreamExecutionScope.Begin(Guid.NewGuid(), project, "scripted");
        var page = await ReadBoundedAsync(actions, "dream_work_trail", new { kind = "message" });
        var selected = page.GetProperty("items")[0];
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.ExecuteAsync("UPDATE work_messages SET body='Changed evidence' WHERE id=@id", new { id = selected.GetProperty("id").GetString() });
        var changed = await Should.ThrowAsync<WorkException>(() => actions.ExecuteToolAsync("dream_work_trail", selected.GetProperty("read").GetProperty("arguments"), scope.Actor));
        changed.Code.ShouldBe(WorkErrorCodes.Conflict);
        var foreignRead = await Should.ThrowAsync<WorkException>(() => actions.ExecuteToolAsync("dream_work_trail", JsonSerializer.SerializeToElement(new { kind = "revision", id = foreign.Id + ":1" }), scope.Actor));
        foreignRead.Code.ShouldBe(WorkErrorCodes.NotFound);
        foreach (var arguments in new object[] { new { kind = "invalid" }, new { id = "not-an-id" }, new { detail = "preview" }, new { expected_hash = new string('a', 64) }, new { item = Guid.Empty } })
            (await Should.ThrowAsync<WorkException>(() => actions.ExecuteToolAsync("dream_work_trail", JsonSerializer.SerializeToElement(arguments), scope.Actor))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        // Discovery's immutable captured rows retain the original preview; a new read gets the current row.
        selected.GetProperty("preview").GetString().ShouldBe("Original evidence");
        var fresh = await ReadBoundedAsync(actions, "dream_work_trail", new { kind = "message", id = selected.GetProperty("id").GetString() });
        fresh.GetProperty("items")[0].GetProperty("data").GetProperty("body").GetString().ShouldBe("Changed evidence");
    }

    [Fact]
    public async Task Empty_digest_does_not_offer_an_empty_view_checklist()
    {
        var project = await ProjectAsync();
        var digest = await ((IKnowledgeDreamActions)db.Service).BuildDigestAsync(project, null);
        digest.Data.GetProperty("reads").GetArrayLength().ShouldBe(0);
        digest.Data.GetProperty("selection").GetString()!.ShouldContain("not a checklist");
    }
}
