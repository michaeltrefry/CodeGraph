using System.Text;
using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class DreamReplayContextTests
{
    [Fact]
    public void Prior_reader_selection_uses_known_audit_availability_without_changing_unknown_counter_fallback()
    {
        IReadOnlyList<DreamToolDefinition> tools = DreamingService.Tools.Where(tool => tool.Name != "dream_read_prior")
            .Append(new("dream_read_prior", "Read this run's immutable audit", JsonSerializer.SerializeToElement(new { type = "object" }))).ToArray();
        var unknownCounts = JsonSerializer.SerializeToElement(new { });
        DreamToolSelection.Select(tools, unknownCounts).ShouldBeSameAs(tools);
        DreamToolSelection.Select(tools, unknownCounts, hasPriorResults: true).ShouldBeSameAs(tools);
        var noPrior = DreamToolSelection.Select(tools, unknownCounts, hasPriorResults: false);
        noPrior.Select(tool => tool.Name).ShouldBe(tools.Where(tool => tool.Name != "dream_read_prior").Select(tool => tool.Name));
        noPrior.ShouldAllBe(tool => tools.Contains(tool));
        DreamToolSelection.Select(tools, unknownCounts, unknownNewEntries: true, hasPriorResults: false)
            .Select(tool => tool.Name).ShouldNotContain("dream_read_prior");
    }

    [Fact]
    public void Only_delivered_reads_are_archived_and_duplicate_tool_ids_keep_chronological_audit_indexes()
    {
        var first = Source();
        var write = JsonSerializer.SerializeToElement(new { data = new { entry = new { id = Guid.NewGuid(), body = new string('w', 20000) } }, journal_id = 9 });
        var trail = JsonSerializer.SerializeToElement(new { data = new { total_count = 1, items = new[] { new { id = Guid.NewGuid(), kind = "thread", summary = "Evidence thread", body = new string('t', 20000) } } } });
        var fresh = Source("Fresh.cs");
        var actions = new[] { Action("dream_read_source", first), Action("dream_record", write), Action("dream_work_trail", trail), Action("dream_read_source", fresh) };
        var messages = new List<DreamModelMessage> { new("user", "Digest") };
        foreach (var action in actions)
        {
            messages.Add(new("assistant", "Selected evidence", [new("duplicate", action.Tool, JsonSerializer.SerializeToElement(new { }))]));
            messages.Add(new("tool", DreamModelContext.ModelJson(action.Result), ToolCallId: "duplicate"));
        }
        var audit = actions.Select(action => action.Result.GetRawText()).ToArray();
        var originalMessages = messages.ToArray();
        var before = Bytes(messages);

        var saved = DreamReplayContext.ArchiveDeliveredReads(messages, actions, deliveredPrefix: 7);

        saved.ShouldBe(before - Bytes(messages));
        saved.ShouldBeGreaterThan(30000);
        var receipt = JsonSerializer.Deserialize<JsonElement>(messages[2].Content!);
        receipt.GetProperty("action_index").GetInt32().ShouldBe(1);
        receipt.GetProperty("original_tool").GetString().ShouldBe("dream_read_source");
        receipt.GetProperty("metadata_not_evidence").GetBoolean().ShouldBeTrue();
        receipt.GetProperty("previously_delivered").GetBoolean().ShouldBeTrue();
        receipt.GetProperty("full_result_in_run_report").GetBoolean().ShouldBeTrue();
        var fields = receipt.GetProperty("metadata").GetProperty("fields");
        fields.GetProperty("path").GetString().ShouldBe("Large.cs");
        fields.GetProperty("content_hash").GetString().ShouldBe("actual-hash");
        fields.TryGetProperty("source", out _).ShouldBeFalse();
        var read = receipt.GetProperty("read");
        read.GetProperty("tool").GetString().ShouldBe("dream_read_prior");
        read.GetProperty("arguments").EnumerateObject().Select(property => property.Name).ShouldBe(new[] { "action_index" });
        Page(actions, read.GetProperty("arguments")).GetProperty("original_tool").GetString().ShouldBe("dream_read_source");
        var second = JsonSerializer.Deserialize<JsonElement>(messages[6].Content!);
        second.GetProperty("action_index").GetInt32().ShouldBe(3);
        second.GetProperty("original_tool").GetString().ShouldBe("dream_work_trail");
        second.GetProperty("metadata").GetProperty("items")[0].GetProperty("summary").GetString().ShouldBe("Evidence thread");
        second.GetProperty("metadata").GetProperty("items")[0].TryGetProperty("body", out _).ShouldBeFalse();
        foreach (var index in new[] { 0, 1, 3, 4, 5, 7, 8 }) messages[index].ShouldBeSameAs(originalMessages[index]);
        actions.Select(action => action.Result.GetRawText()).ShouldBe(audit);
        DreamReplayContext.ArchiveDeliveredReads(messages, actions, deliveredPrefix: 7).ShouldBe(0);
    }

    [Theory]
    [InlineData("dream_read")]
    [InlineData("dream_lessons")]
    [InlineData("dream_similar")]
    [InlineData("dream_review_queue")]
    [InlineData("dream_work_trail")]
    [InlineData("dream_search_graph")]
    [InlineData("dream_read_source")]
    [InlineData("dream_read_prior")]
    public void Every_delivered_read_kind_has_an_exact_replay_route(string tool)
    {
        var result = Source();
        var actions = new[] { Action(tool, result) };
        var messages = new List<DreamModelMessage> { new("tool", DreamModelContext.ModelJson(result), ToolCallId: "read") };
        DreamReplayContext.ArchiveDeliveredReads(messages, actions, 0).ShouldBe(0);
        DreamReplayContext.ArchiveDeliveredReads(messages, actions, 1).ShouldBeGreaterThan(0);
        var receipt = JsonSerializer.Deserialize<JsonElement>(messages[0].Content!);
        receipt.GetProperty("read").GetProperty("arguments").GetProperty("action_index").GetInt32().ShouldBe(1);
        result.GetRawText().ShouldBe(actions[0].Result.GetRawText());
    }

    [Fact]
    public void Errors_write_outcomes_existing_receipts_and_short_metadata_are_retained_exactly()
    {
        var results = new[]
        {
            Action("dream_read_source", JsonSerializer.SerializeToElement(new { error = "forbidden", message = new string('e', 20000) })),
            Action("dream_verify", Source()),
            Action("dream_propose", Source()),
            Action("unknown_tool", Source()),
            Action("dream_read_source", JsonSerializer.SerializeToElement(new { untrusted_data = new { compacted_after = new { journal_id = 1 }, note = new string('x', 20000) } })),
            Action("dream_read", JsonSerializer.SerializeToElement(new { data = new { total_count = 0, items = Array.Empty<object>() } }))
        };
        var messages = results.Select(action => new DreamModelMessage("tool", DreamModelContext.ModelJson(action.Result), ToolCallId: "same")).ToList();
        var before = messages.ToArray();
        DreamReplayContext.ArchiveDeliveredReads(messages, results, messages.Count).ShouldBe(0);
        for (var index = 0; index < messages.Count; index++) messages[index].ShouldBeSameAs(before[index]);
    }

    [Fact]
    public void Large_unicode_json_reconstructs_exactly_from_byte_bounded_pages_without_split_surrogates()
    {
        // Parse raw Unicode so GetRawText contains actual UTF-16 surrogate pairs, rather than
        // serializer-generated ASCII escapes that could hide boundary bugs.
        var source = Source();
        var actions = new[] { Action("dream_read_source", source) };
        var expected = source.GetRawText();
        expected.ShouldContain("😀");
        var reconstructed = new StringBuilder();
        var offset = 0;
        var pages = 0;
        while (true)
        {
            (++pages).ShouldBeLessThan(1000);
            var page = Page(actions, JsonSerializer.SerializeToElement(new { action_index = 1, offset_chars = offset }));
            page.GetProperty("offset_chars").GetInt32().ShouldBe(offset);
            page.GetProperty("total_chars").GetInt32().ShouldBe(expected.Length);
            page.GetProperty("format").GetString().ShouldBe("json");
            var text = page.GetProperty("content").GetString()!;
            ValidUtf16(text).ShouldBeTrue();
            reconstructed.Append(text);
            if (!page.GetProperty("has_more").GetBoolean()) break;
            text.Length.ShouldBeGreaterThan(0);
            offset = page.GetProperty("next_offset_chars").GetInt32();
            offset.ShouldBe(reconstructed.Length);
        }
        pages.ShouldBeGreaterThan(2);
        reconstructed.ToString().ShouldBe(expected);
        JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(reconstructed.ToString()), source).ShouldBeTrue();
        actions[0].Result.GetRawText().ShouldBe(expected);
        var end = Page(actions, JsonSerializer.SerializeToElement(new { action_index = 1, offset_chars = expected.Length }));
        end.GetProperty("content").GetString().ShouldBe("");
        end.GetProperty("has_more").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Replay_rejects_unknown_actions_and_offsets_outside_valid_utf16_boundaries()
    {
        var actions = new[] { Action("dream_read_source", Source()) };
        foreach (var index in new[] { -1, 0, 2 })
            Should.Throw<WorkException>(() => DreamReplayContext.ReadPrior(actions, JsonSerializer.SerializeToElement(new { action_index = index })))
                .Code.ShouldBe(WorkErrorCodes.NotFound);
        var json = actions[0].Result.GetRawText();
        var splitPair = json.IndexOf("😀", StringComparison.Ordinal) + 1;
        char.IsHighSurrogate(json[splitPair - 1]).ShouldBeTrue();
        char.IsLowSurrogate(json[splitPair]).ShouldBeTrue();
        foreach (var offset in new[] { -1, json.Length + 1, splitPair })
            Should.Throw<WorkException>(() => DreamReplayContext.ReadPrior(actions, JsonSerializer.SerializeToElement(new { action_index = 1, offset_chars = offset })))
                .Code.ShouldBe(WorkErrorCodes.InvalidArgument);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("project_id")]
    [InlineData("run_id")]
    [InlineData("session_id")]
    [InlineData("actor")]
    public void Replay_accepts_no_caller_selected_identity(string identity)
    {
        var arguments = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["action_index"] = 1, [identity] = "foreign" });
        Should.Throw<WorkException>(() => DreamReplayContext.ReadPrior([Action("dream_read_source", Source())], arguments))
            .Code.ShouldBe(WorkErrorCodes.InvalidArgument);
    }

    [Theory]
    [InlineData("{\"action_index\":\"1\"}")]
    [InlineData("{\"action_index\":1.5}")]
    [InlineData("{\"action_index\":1,\"offset_chars\":true}")]
    [InlineData("{}")]
    public void Replay_preserves_strict_argument_types(string json) =>
        Should.Throw<WorkException>(() => DreamReplayContext.ReadPrior([Action("dream_read_source", Source())], JsonSerializer.Deserialize<JsonElement>(json)))
            .Code.ShouldBe(WorkErrorCodes.InvalidArgument);

    [Fact]
    public void Archiving_and_replaying_source_never_establish_new_source_or_graph_proof()
    {
        using var scope = DreamExecutionScope.Begin(Guid.NewGuid(), Guid.NewGuid(), "scripted");
        scope.MarkSourceRead("repo", "Already.cs", 9, "old", "old");
        var actions = new[] { Action("dream_read_source", Source()) };
        var messages = new List<DreamModelMessage> { new("tool", DreamModelContext.ModelJson(actions[0].Result), ToolCallId: "source") };
        DreamReplayContext.ArchiveDeliveredReads(messages, actions, 1).ShouldBeGreaterThan(0);
        Page(actions, JsonSerializer.SerializeToElement(new { action_index = 1 }));
        scope.ReadFile("repo", "Large.cs").ShouldBeFalse();
        scope.ReadFileHash("repo", "Large.cs").ShouldBeNull();
        scope.ReadNode(42).ShouldBeFalse();
        scope.ReadNodeHash(42).ShouldBeNull();
        scope.ReadFileHash("repo", "Already.cs").ShouldBe("old");
        scope.ReadNode(9).ShouldBeTrue();
    }

    private static JsonElement Source(string path = "Large.cs") => JsonSerializer.Deserialize<JsonElement>(
        "{\"untrusted_data\":{\"repository\":\"repo\",\"path\":\"" + path + "\",\"node_id\":42,\"content_hash\":\"actual-hash\",\"indexed_hash\":\"actual-hash\",\"source\":\""
        + string.Concat(Enumerable.Repeat("界😀café", 6000)) + "\"}}");

    private static DreamAction Action(string tool, JsonElement result) => new(tool, JsonSerializer.SerializeToElement(new { }), result, [], DateTime.UtcNow);
    private static int Bytes(IEnumerable<DreamModelMessage> messages) => messages.Sum(message => Encoding.UTF8.GetByteCount(message.Content ?? ""));
    private static JsonElement Page(IReadOnlyList<DreamAction> actions, JsonElement arguments)
    {
        var value = DreamReplayContext.ReadPrior(actions, arguments);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, WorkJson.Options);
        bytes.Length.ShouldBeLessThanOrEqualTo(WorkService.DreamReadPageBytes);
        return JsonSerializer.Deserialize<JsonElement>(bytes);
    }
    private static bool ValidUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsLowSurrogate(value[index])) return false;
            if (!char.IsHighSurrogate(value[index])) continue;
            if (++index == value.Length || !char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }
}
