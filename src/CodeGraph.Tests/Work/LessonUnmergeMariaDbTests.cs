using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed class LessonUnmergeMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private async Task<JsonElement> Lesson(string project, string summary, string reference, string activity = "implementing", string source = "agent", string? sameAs = null, string? sessionId = null) =>
        (await db.CallAsync(WorkTools.Record, new { project, session_id = sessionId, entry = new { kind = "lesson", summary, what = "Observed this correction", confidence = "high",
            applies_when = new { activity = new[] { activity } }, source = new { kind = source, @ref = reference }, same_as = sameAs } })).GetProperty("entry");

    private async Task<JsonElement> Entry(string id) =>
        (await db.CallAsync(WorkTools.Recall, new { id, detail = "full" })).GetProperty("items")[0];

    [Fact]
    public async Task Merge_and_unmerge_restore_events_count_flags_links_challenges_and_rule_state()
    {
        var project = await db.NewProjectAsync("UM");
        var sourceSession = (await db.CallAsync(WorkTools.StartSession, new { project, harness = "codex" })).GetProperty("session").GetProperty("id").GetString();
        var targetSession = (await db.CallAsync(WorkTools.StartSession, new { project, harness = "codex" })).GetProperty("session").GetProperty("id").GetString();
        var source = await Lesson(project, "Use bounded retry on source operations", "source-event", "reviewing", sessionId: sourceSession);
        var sourceId = source.GetProperty("id").GetString()!;
        source = await Lesson(project, "Use bounded retry on source operations", "shared-event", "reviewing", "review", sourceId, sourceSession);
        // Original uncounted redeliveries must retain their flags when restored.
        source = await Lesson(project, "Use bounded retry on source operations", "shared-event", "reviewing", "review", sourceId, sourceSession);
        var target = await Lesson(project, "Use bounded retry for target operations", "shared-event", sessionId: targetSession);
        var targetId = target.GetProperty("id").GetString()!;
        var other = await Lesson(project, "Never retry these operations", "contradiction-event");
        var otherId = other.GetProperty("id").GetString()!;
        await db.CallAsync(WorkTools.Record, new { relate = new { id = sourceId, expected_version = source.GetProperty("version").GetInt64(), contradicts = otherId } });
        source = await Entry(sourceId);
        await db.CallAsync(WorkTools.Challenge, new { open = new { id = sourceId, reason = "Needs narrower applicability", evidence = new[] { new { kind = "test_result", summary = "A counterexample", @ref = "test:bounded" } } } });
        source = await Entry(sourceId);
        target = await Entry(targetId);
        await using var connection = new MySqlConnection(db.ConnectionString);
        var original = (await connection.QueryAsync<(Guid CorrectionId, Guid EntryId, bool Counted)>(
            "SELECT correction_id, entry_id, counted FROM lesson_events WHERE entry_id IN @ids ORDER BY correction_id", new { ids = new[] { sourceId, targetId } })).ToList();
        var links = (await connection.QueryAsync<string>("SELECT CONCAT(entry_id, ':', other_id, ':', author) FROM lesson_links WHERE entry_id = @source OR other_id = @source ORDER BY entry_id, other_id", new { source = sourceId })).ToList();
        var challenge = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM lesson_challenges WHERE entry_id = @source", new { source = sourceId });
        var projectId = source.GetProperty("project_id").GetGuid();
        var run = Guid.NewGuid();
        using (var scope = DreamExecutionScope.Begin(run, projectId, "scripted"))
        using (scope.UseEvidence([new DreamEvidence("entry", sourceId, "Duplicate rule and lesson: two sessions")]))
        using (DreamWriteMetadata.Begin("Two events across two sessions"))
            await db.Store.RelateLessonAsync(Guid.Parse(sourceId), source.GetProperty("version").GetInt64(), Guid.Parse(targetId), null, scope.Actor);
        var merged = await Entry(targetId);
        merged.GetProperty("lesson").GetProperty("recurrence").GetProperty("events").GetInt32().ShouldBe(2);
        merged.GetProperty("lesson").GetProperty("state").GetString().ShouldBe("rule");
        (await connection.QuerySingleAsync<(string AuthorKind, string Session)>("SELECT author_kind, author_session_id AS session FROM knowledge_revisions WHERE entry_id = @id ORDER BY revision DESC LIMIT 1", new { id = sourceId })).ShouldBe(("system", run.ToString()));
        var superseded = await Entry(sourceId);
        var restored = (await db.CallAsync(WorkTools.Record, new { unmerge = new { id = sourceId, expected_version = superseded.GetProperty("version").GetInt64() } }, "another-user")).GetProperty("entry");
        restored.GetProperty("status").GetString().ShouldBe("active");
        restored.GetProperty("lesson").GetProperty("state").GetString().ShouldBe("rule");
        restored.GetProperty("lesson").GetProperty("priority").GetString().ShouldBe("high");
        restored.GetProperty("lesson").GetProperty("recurrence").GetProperty("events").GetInt32().ShouldBe(2);
        var targetAfter = await Entry(targetId);
        targetAfter.GetProperty("lesson").GetProperty("state").GetString().ShouldBe("candidate");
        targetAfter.GetProperty("lesson").GetProperty("priority").GetString().ShouldBe("normal");
        targetAfter.GetProperty("lesson").GetProperty("recurrence").GetProperty("events").GetInt32().ShouldBe(1);
        targetAfter.GetProperty("details").GetProperty("applies_when").GetProperty("activity").EnumerateArray().Select(a => a.GetString()).ShouldBe(["implementing"]);
        (await connection.QueryAsync<(Guid CorrectionId, Guid EntryId, bool Counted)>("SELECT correction_id, entry_id, counted FROM lesson_events WHERE entry_id IN @ids ORDER BY correction_id", new { ids = new[] { sourceId, targetId } })).ShouldBe(original);
        (await connection.QueryAsync<string>("SELECT CONCAT(entry_id, ':', other_id, ':', author) FROM lesson_links WHERE entry_id = @source OR other_id = @source ORDER BY entry_id, other_id", new { source = sourceId })).ShouldBe(links);
        (await connection.ExecuteScalarAsync<Guid>("SELECT entry_id FROM lesson_challenges WHERE id = @challenge", new { challenge })).ShouldBe(Guid.Parse(sourceId));
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM lesson_links WHERE entry_id = @target OR other_id = @target", new { target = targetId })).ShouldBe(0);
        var stale = await db.RefusedAsync(WorkTools.Record, new { unmerge = new { id = sourceId, expected_version = superseded.GetProperty("version").GetInt64() } });
        stale.Code.ShouldBe(WorkErrorCodes.Conflict);
    }

    [Fact]
    public async Task New_target_corrections_remain_and_suppressed_duplicate_becomes_counted_after_unmerge()
    {
        var project = await db.NewProjectAsync("UN");
        var source = await Lesson(project, "Source lesson", "source-event", "reviewing");
        var target = await Lesson(project, "Target lesson", "target-event");
        var sourceId = source.GetProperty("id").GetString()!;
        var targetId = target.GetProperty("id").GetString()!;
        await db.CallAsync(WorkTools.Record, new { relate = new { id = sourceId, expected_version = source.GetProperty("version").GetInt64(), same_as = targetId } });
        await Lesson(project, "Post merge target correction", "source-event", "testing", "review", targetId);
        target = await Entry(targetId);
        await db.CallAsync(WorkTools.Record, new { revise = new { id = targetId, expected_version = target.GetProperty("version").GetInt64(), summary = "Target edited later" } });
        var mergedSource = await Entry(sourceId);
        var wrongVersion = await db.RefusedAsync(WorkTools.Record, new { unmerge = new { id = sourceId, expected_version = 999 } });
        wrongVersion.Code.ShouldBe(WorkErrorCodes.Conflict);
        await db.CallAsync(WorkTools.Record, new { unmerge = new { id = sourceId, expected_version = mergedSource.GetProperty("version").GetInt64() } });
        var after = await Entry(targetId);
        after.GetProperty("summary").GetString().ShouldBe("Target edited later");
        after.GetProperty("lesson").GetProperty("recurrence").GetProperty("events").GetInt32().ShouldBe(2);
        after.GetProperty("details").GetProperty("applies_when").GetProperty("activity").EnumerateArray().Select(a => a.GetString()).ShouldBe(["implementing", "testing"]);
        await using var connection = new MySqlConnection(db.ConnectionString);
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM lesson_events WHERE entry_id = @id", new { id = targetId })).ShouldBe(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dreamer_cannot_merge_when_either_lesson_is_prompt_sourced(bool promptSource)
    {
        var project = await db.NewProjectAsync("UP");
        var source = await Lesson(project, "Source prompt test", "source-prompt", source: promptSource ? "prompt" : "agent");
        var target = await Lesson(project, "Target prompt test", "target-prompt", source: promptSource ? "agent" : "prompt");
        using var scope = DreamExecutionScope.Begin(Guid.NewGuid(), source.GetProperty("project_id").GetGuid(), "scripted");
        using var evidence = scope.UseEvidence([new DreamEvidence("entry", source.GetProperty("id").GetString()!, "Two similar lessons")]);
        var refused = await Should.ThrowAsync<WorkException>(() => db.Store.RelateLessonAsync(source.GetProperty("id").GetGuid(),
            source.GetProperty("version").GetInt64(), target.GetProperty("id").GetGuid(), null, scope.Actor));
        refused.Code.ShouldBe("forbidden");
        (await Entry(source.GetProperty("id").GetString()!)).GetProperty("status").GetString().ShouldBe("active");
    }

    [Fact]
    public async Task Contradiction_asserted_on_target_after_merge_stays_on_target_and_original_source_link_returns()
    {
        var project = await db.NewProjectAsync("UC");
        var source = await Lesson(project, "Source contradiction", "source-contradiction");
        var target = await Lesson(project, "Target contradiction", "target-contradiction");
        var other = await Lesson(project, "Another contradiction", "other-contradiction");
        var sourceId = source.GetProperty("id").GetString()!;
        var targetId = target.GetProperty("id").GetString()!;
        var otherId = other.GetProperty("id").GetString()!;
        await db.CallAsync(WorkTools.Record, new { relate = new { id = sourceId, expected_version = 1, contradicts = otherId } });
        await db.CallAsync(WorkTools.Record, new { relate = new { id = sourceId, expected_version = 2, same_as = targetId } });
        target = await Entry(targetId);
        await db.CallAsync(WorkTools.Record, new { relate = new { id = targetId, expected_version = target.GetProperty("version").GetInt64(), contradicts = otherId } });
        await db.CallAsync(WorkTools.Record, new { unmerge = new { id = sourceId, expected_version = 3 } });
        await using var connection = new MySqlConnection(db.ConnectionString);
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM lesson_links WHERE other_id = @other", new { other = otherId })).ShouldBe(2);
    }

    [Fact]
    public async Task Rest_unmerge_is_available_to_a_different_actor()
    {
        var project = await db.NewProjectAsync("UR");
        var source = await Lesson(project, "Source REST", "source-rest");
        var target = await Lesson(project, "Target REST", "target-rest");
        var sourceId = source.GetProperty("id").GetString()!;
        await db.CallAsync(WorkTools.Record, new { relate = new { id = sourceId, expected_version = 1, same_as = target.GetProperty("id").GetString() } });
        await using var host = await WorkRestHost.StartAsync(db.Service);
        var token = await host.CreateTokenAsync("rest-unmerger");
        var response = await host.SendAsync(HttpMethod.Post, "/api/work/knowledge",
            JsonSerializer.Serialize(new { unmerge = new { id = sourceId, expected_version = 2 } }), token);
        response.Status.ShouldBe(200);
        response.Body.GetProperty("entry").GetProperty("status").GetString().ShouldBe("active");
        await using var connection = new MySqlConnection(db.ConnectionString);
        (await connection.ExecuteScalarAsync<string>("SELECT author FROM knowledge_revisions WHERE entry_id = @sourceId ORDER BY revision DESC LIMIT 1", new { sourceId })).ShouldBe("rest-unmerger");
    }

    [Fact]
    public async Task Legacy_merge_without_journal_is_refused_without_mutation()
    {
        var project = await db.NewProjectAsync("UL");
        var source = await Lesson(project, "Legacy source", "legacy-source");
        var target = await Lesson(project, "Legacy target", "legacy-target");
        var sourceId = source.GetProperty("id").GetString()!;
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.ExecuteAsync("UPDATE knowledge_entries SET status = 'superseded', superseded_by_id = @target WHERE id = @source", new { source = sourceId, target = target.GetProperty("id").GetString() });
        var refused = await db.RefusedAsync(WorkTools.Record, new { unmerge = new { id = sourceId, expected_version = source.GetProperty("version").GetInt64() } });
        refused.Message.ShouldContain("Legacy");
        (await Entry(sourceId)).GetProperty("status").GetString().ShouldBe("superseded");
    }
}
