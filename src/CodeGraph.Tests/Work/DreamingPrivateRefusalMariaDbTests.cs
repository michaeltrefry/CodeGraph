using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed partial class DreamingRunAcceptanceMariaDbTests
{
    [Fact]
    public async Task Mistyped_private_arguments_are_recorded_as_refusals_and_independent_calls_continue()
    {
        var project = await ProjectAsync();
        var entry = await RecordAsync(project, "The independent reader must remain usable after malformed calls");
        object Verify(object citation) => new { id = entry.Id, expected_version = entry.Version, evidence = new[] { citation }, prevalence = "one entry" };
        object Write(object fields)
        {
            var json = JsonSerializer.SerializeToNode(fields, WorkJson.Options)!.AsObject();
            json["evidence"] = JsonSerializer.SerializeToNode(Evidence(entry), WorkJson.Options);
            json["prevalence"] = "one entry";
            return json;
        }
        var calls = new[]
        {
            Call("numeric_kind", "dream_verify", Verify(new { kind = 42, reference = entry.Id.ToString(), summary = "x" })),
            Call("numeric_reference", "dream_verify", Verify(new { kind = "entry", reference = 42, summary = "x" })),
            Call("object_summary", "dream_verify", Verify(new { kind = "entry", reference = entry.Id.ToString(), summary = new { text = "x" } })),
            Call("scalar_evidence", "dream_verify", Verify(42)),
            Call("array_evidence", "dream_verify", Verify(new[] { "entry" })),
            Call("array_arguments", "dream_read", new[] { 1 }),
            Call("missing_summary", "dream_record", Write(new { entry = new { kind = "fact", confidence = "high" } })),
            Call("invalid_enum", "dream_record", Write(new { entry = new { kind = "invalid_kind", summary = "x", confidence = "high" } })),
            Call("challenge_id", "dream_challenge", Write(new { id = 42, reason = "x" })),
            Call("challenge_reason", "dream_challenge", Write(new { id = entry.Id, reason = new[] { "x" } })),
            Call("read_id", "dream_read", new { id = 42 }),
            Call("read_detail", "dream_read", new { detail = 42 }),
            Call("lessons_view", "dream_lessons", new { view = 42 }),
            Call("queue_limit", "dream_review_queue", new { limit = "one" }),
            Call("similar_offset", "dream_similar", new { offset = "one" }),
            Call("similar_since", "dream_similar", new { since = "2026-01-01T00:00:00Z" }),
            Call("trail_offset", "dream_work_trail", new { offset = false }),
            Call("trail_since", "dream_work_trail", new { since = "not a timestamp" }),
            Call("cursor_type", "dream_read", new { cursor = 42 }),
            Call("cursor_encoding", "dream_read", new { cursor = "dr:not-base64" }),
            Call("source_detail_type", "dream_read_source", new { repository="codegraph", path="Source.cs", detail=42 }),
            Call("source_detail_value", "dream_read_source", new { repository="codegraph", path="Source.cs", detail="preview" }),
            new CodeGraph.Services.Work.DreamToolCall("duplicate", "dream_read", JsonDocument.Parse("""{"detail":"summary","detail":"full"}""").RootElement.Clone()),
            Call("independent", "dream_read", new { id = entry.Id })
        };
        var model = new ScriptedModel(Response(calls), Response());
        var service = await ServiceAsync(model);

        var run = await service.RunAsync(project, "run_now", Human);

        run.Status.ShouldBe("completed", run.Reason);
        run.Actions.Count.ShouldBe(calls.Length);
        foreach (var action in run.Actions.SkipLast(1))
        {
            action.Result.GetProperty("error").GetString().ShouldBe(WorkErrorCodes.InvalidArgument, action.Arguments.GetRawText());
            action.Result.GetProperty("message").GetString().ShouldNotBeNullOrEmpty();
        }
        run.Actions[0].Arguments.GetProperty("evidence")[0].GetProperty("kind").GetInt32().ShouldBe(42);
        run.Actions[^2].Arguments.GetProperty("invalid_arguments_json").GetString().ShouldBe(calls[^2].Arguments.GetRawText());
        run.Actions.Last().Result.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(entry.Id);
        var persisted = (await service.GetRunAsync(project, run.Id))!;
        persisted.Status.ShouldBe("completed");
        persisted.Actions.Count.ShouldBe(calls.Length);
        persisted.Actions.SkipLast(1).ShouldAllBe(action => action.Result.GetProperty("error").GetString() == WorkErrorCodes.InvalidArgument);
        persisted.Actions[^2].Arguments.GetProperty("invalid_arguments_json").GetString().ShouldBe(calls[^2].Arguments.GetRawText());
        model.Requests.Count.ShouldBe(2);
        var after = (await ((IKnowledgeStore)db.Store).GetKnowledgeAsync(entry.Id, true))!;
        after.Version.ShouldBe(entry.Version);
        after.Revisions!.Count.ShouldBe(1);
        run.ActionCounts.ShouldBeEmpty();
    }
}
