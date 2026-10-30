using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Data.MariaDb;
using Microsoft.Extensions.Options;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>Deterministic scripted dream tools against real MariaDB; no provider/network calls.</summary>
public sealed class DreamActionsMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private async Task<(string Key, Guid Id)> ProjectAsync(string prefix)
    {
        var key = await db.NewProjectAsync(prefix);
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.ExecuteAsync("INSERT INTO work_project_repositories(project_id,repository_name,role,position) SELECT id,'codegraph','dependency',0 FROM work_projects WHERE project_key=@key", new { key });
        return (key, (await db.Store.ResolveAsync(key))!.Id);
    }

    private async Task RegisterRunAsync(Guid runId, Guid projectId)
    {
        var store = new MySqlDreamingStore(Options.Create(new MariaDbStorageOptions { ConnectionString = db.ConnectionString }));
        await store.SaveRunAsync(new DreamRun(runId, projectId, "run_now", new("tester"), "running", DateTime.UtcNow, null, "scripted", "scripted", 0, 0, 0, 1, 0, null, []));
    }

    private async Task<KnowledgeView> LessonAsync(string project, string summary, string source = "agent")
    {
        var result = await db.CallAsync(WorkTools.Record, new { project, entry = new { kind = "lesson", summary, what = "Observed correction", source = new { kind = source, @ref = Guid.NewGuid().ToString() }, applies_when = new { activity = new[] { "implementing" } } } });
        return (await db.Store.GetKnowledgeAsync(result.GetProperty("entry").GetProperty("id").GetGuid(), false))!;
    }

    private async Task<KnowledgeView> FactAsync(string project, string summary, string path)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.ExecuteAsync("INSERT INTO file_hashes(repository,rel_path,content_hash) VALUES('codegraph',@path,'old') ON DUPLICATE KEY UPDATE content_hash='old'", new { path });
        var result = await db.CallAsync(WorkTools.Record, new { project, entry = new { kind = "fact", summary, repositories = new[] { "codegraph" }, paths = new[] { path } } });
        await connection.ExecuteAsync("UPDATE file_hashes SET content_hash='new' WHERE repository='codegraph' AND rel_path=@path", new { path });
        return (await db.Store.GetKnowledgeAsync(result.GetProperty("entry").GetProperty("id").GetGuid(), false))!;
    }

    private async Task<JsonElement> ToolAsync(string tool, object input, Guid evidenceEntry)
    {
        var args = JsonSerializer.SerializeToNode(input, WorkJson.Options)!.AsObject();
        args["evidence"] = JsonSerializer.SerializeToNode(new[] { new DreamEvidence("entry", evidenceEntry.ToString(), "Observed in the scoped knowledge entry") }, WorkJson.Options);
        args["prevalence"] = "Observed once in one session";
        var result = await ((IKnowledgeDreamActions)db.Service).ExecuteToolAsync(tool, JsonSerializer.SerializeToElement(args), DreamExecutionScope.Current!.Actor);
        return JsonSerializer.SerializeToElement(result, WorkJson.Options).GetProperty("data");
    }

    [Fact]
    public async Task A_prompt_rule_is_challenged_without_changing_its_content_or_serving_and_the_challenge_reverts()
    {
        var project = await ProjectAsync("DP");
        var prompt = await LessonAsync(project.Key, "Keep explicit requirements before coding", "prompt");
        var run = Guid.NewGuid();
        await RegisterRunAsync(run, project.Id);
        using (var scope = DreamExecutionScope.Begin(run, project.Id, "scripted"))
        {
            await ToolAsync("dream_challenge", new { id = prompt.Id, reason = "Trail evidence calls for narrower applicability" }, prompt.Id);
            var refused = await Should.ThrowAsync<WorkException>(() => ToolAsync("dream_revise", new { id = prompt.Id, expected_version = prompt.Version, applies_when = new { activity = new[] { "testing" } } }, prompt.Id));
            refused.Message.ShouldContain("non-prompt");
            (await db.Store.GetKnowledgeAsync(prompt.Id, false))!.Version.ShouldBe(prompt.Version);
            (await db.Store.GetKnowledgeAsync(prompt.Id, false))!.Summary.ShouldBe(prompt.Summary);
            var rules = await db.Store.ReadRulesAsync(new(project.Id, [LessonActivity.Implementing], null, [], null, 20));
            rules.ShouldContain(rule => rule.Id == prompt.Id && rule.Challenges!.Count == 1);
        }
        var reverted = await ((IKnowledgeDreamActions)db.Service).RevertAsync(project.Id, run, new("michael"));
        reverted.Entries.Single().Status.ShouldBe("reverted");
        var restored = (await db.Store.GetKnowledgeAsync(prompt.Id, true))!;
        restored.Summary.ShouldBe(prompt.Summary);
        (restored.Lesson!.Challenges?.Count ?? 0).ShouldBe(0);
        restored.Revision.ShouldBe(prompt.Revision + 1);
        restored.Revisions!.First().Action.ShouldBe("dream_reverted");
        restored.Revisions!.First().Author.Name.ShouldBe("michael");
    }

    [Fact]
    public async Task Only_applies_when_can_be_revised_and_new_lessons_require_agent_source_and_explicit_confidence()
    {
        var project = await ProjectAsync("DA");
        var lesson = await LessonAsync(project.Key, "Validate release artifacts against declared inputs");
        var run = Guid.NewGuid();
        await RegisterRunAsync(run, project.Id);
        using var scope = DreamExecutionScope.Begin(run, project.Id, "scripted");
        await ToolAsync("dream_revise", new { id = lesson.Id, expected_version = lesson.Version, applies_when = new { activity = new[] { "testing" }, paths = new[] { "src/Tests.cs" }, repositories = new[] { "codegraph" } } }, lesson.Id);
        var changed = (await db.Store.GetKnowledgeAsync(lesson.Id, false))!;
        changed.Summary.ShouldBe(lesson.Summary);
        changed.Paths.ShouldBe(["src/Tests.cs"]);
        changed.Details!.AppliesWhen!.Activity.ShouldBe([LessonActivity.Testing]);
        scope.MarkSourceRead("codegraph", "src/Old.cs");
        await Should.ThrowAsync<WorkException>(() => ToolAsync("dream_verify", new { id = lesson.Id, expected_version = changed.Version }, lesson.Id));
        (await db.Store.GetKnowledgeAsync(lesson.Id, false))!.Version.ShouldBe(changed.Version);
        scope.MarkSourceRead("codegraph", "src/Tests.cs");
        await ToolAsync("dream_verify", new { id = lesson.Id, expected_version = changed.Version }, lesson.Id);
        changed = (await db.Store.GetKnowledgeAsync(lesson.Id, false))!;
        await Should.ThrowAsync<WorkException>(() => ToolAsync("dream_revise", new { id = lesson.Id, expected_version = changed.Version, summary = "Hijacked" }, lesson.Id));
        foreach (var source in new[] { "prompt", "review", "bot_review", "subagent" })
            await Should.ThrowAsync<WorkException>(() => ToolAsync("dream_record", new { entry = new { kind = "lesson", summary = "Wrong authority " + source, what = "observed", source = new { kind = source }, confidence = "high" } }, lesson.Id));
        await Should.ThrowAsync<WorkException>(() => ToolAsync("dream_record", new { entry = new { kind = "fact", summary = "No confidence supplied" } }, lesson.Id));
        var recorded = await ToolAsync("dream_record", new { entry = new { kind = "lesson", summary = "Wait for linker output before final validation", what = "Observed linker race", confidence = "medium", source = new { kind = "agent" }, applies_when = new { activity = new[] { "testing" } } } }, lesson.Id);
        recorded.GetProperty("entry").GetProperty("lesson").GetProperty("state").GetString().ShouldBe("candidate");
        recorded.GetProperty("entry").GetProperty("details").GetProperty("source").GetProperty("kind").GetString().ShouldBe("agent");
        await using var connection = new MySqlConnection(db.ConnectionString);
        var journal = (await connection.QueryAsync<(string EvidenceJson, string Prevalence)>("SELECT evidence_json,prevalence FROM dream_write_journal WHERE run_id=@run", new { run = scope.RunId })).ToList();
        journal.Count.ShouldBe(3);
        journal.ShouldAllBe(row => row.EvidenceJson.Contains(lesson.Id.ToString()) && row.Prevalence == "Observed once in one session");
    }

    [Fact]
    public async Task A_multi_operation_run_reverts_newest_first_as_revisions_and_reports_a_later_session_edit_as_conflict()
    {
        var project = await ProjectAsync("DR");
        var a = await LessonAsync(project.Key, "Run native unit coverage before updating release references");
        var b = await LessonAsync(project.Key, "Inspect target behavior when reviewing a distributed release");
        var mergeSource = await LessonAsync(project.Key, "Gather operator evidence before approving a service change");
        var mergeTarget = await LessonAsync(project.Key, "Collect maintainer diagnostics prior to accepting a deployment");
        var fresh = await FactAsync(project.Key, "Formatting changed while the method remained equivalent", "src/DreamFresh.cs");
        var conflict = await FactAsync(project.Key, "A session will edit this after dreaming verifies it", "src/DreamConflict.cs");
        var run = Guid.NewGuid();
        Guid createdId;
        await RegisterRunAsync(run, project.Id);
        using (var scope = DreamExecutionScope.Begin(run, project.Id, "scripted"))
        {
            await ToolAsync("dream_revise", new { id = a.Id, expected_version = a.Version, applies_when = new { activity = new[] { "testing" } } }, a.Id);
            await ToolAsync("dream_promote", new { id = a.Id, expected_version = a.Version + 1 }, a.Id);
            await ToolAsync("dream_relate", new { id = b.Id, expected_version = b.Version, contradicts = a.Id }, a.Id);
            await ToolAsync("dream_relate", new { id = mergeSource.Id, expected_version = mergeSource.Version, same_as = mergeTarget.Id }, mergeTarget.Id);
            scope.MarkSourceRead("codegraph", "src/DreamFresh.cs");
            scope.MarkSourceRead("codegraph", "src/DreamConflict.cs");
            await ToolAsync("dream_verify", new { id = fresh.Id, expected_version = fresh.Version }, fresh.Id);
            await ToolAsync("dream_verify", new { id = conflict.Id, expected_version = conflict.Version }, conflict.Id);
            var recorded = await ToolAsync("dream_record", new { entry = new { kind = "gotcha", summary = "A deploy may race the final token replacement", confidence = "low" } }, fresh.Id);
            createdId = recorded.GetProperty("entry").GetProperty("id").GetGuid();
        }
        await db.CallAsync(WorkTools.Record, new { revise = new { id = conflict.Id, expected_version = conflict.Version + 1, summary = "The later session's corrected fact" } }, "session-owner");
        var result = await ((IKnowledgeDreamActions)db.Service).RevertAsync(project.Id, run, new("reviewer"));
        result.Entries.Count.ShouldBe(7);
        result.Entries.Select(entry => entry.JournalId).ShouldBeInOrder(SortDirection.Descending);
        result.Entries.Count(entry => entry.Status == "reverted").ShouldBe(6);
        result.Entries.Single(entry => entry.Status == "conflict").EntryId.ShouldBe(conflict.Id);
        var restored = (await db.Store.GetKnowledgeAsync(a.Id, true))!;
        restored.Lesson!.State.ShouldBe(LessonRuleState.Candidate);
        restored.Details!.AppliesWhen!.Activity.ShouldBe([LessonActivity.Implementing]);
        (restored.Lesson.Contradicts?.Count ?? 0).ShouldBe(0);
        restored.Revisions!.Count(revision => revision.Action == "dream_reverted").ShouldBe(3);
        (await db.Store.GetKnowledgeAsync(mergeSource.Id, false))!.Status.ShouldBe(KnowledgeStatus.Active);
        (await db.Store.GetKnowledgeAsync(mergeTarget.Id, false))!.Lesson!.Recurrence.Events.ShouldBe(1);
        (await db.Store.GetKnowledgeAsync(fresh.Id, false))!.PossiblyStale.ShouldBeTrue();
        (await db.Store.GetKnowledgeAsync(conflict.Id, false))!.Summary.ShouldBe("The later session's corrected fact");
        (await db.Store.GetKnowledgeAsync(createdId, false))!.Status.ShouldBe(KnowledgeStatus.Retracted);
        var repeated = await ((IKnowledgeDreamActions)db.Service).RevertAsync(project.Id, run, new("reviewer"));
        repeated.Entries.Count(entry => entry.Status == "already_reverted").ShouldBe(6);
        repeated.Entries.Count(entry => entry.Status == "conflict").ShouldBe(1);
    }

    [Fact]
    public async Task Every_read_and_evidence_reference_stays_within_the_run_project_and_marks_text_as_untrusted()
    {
        var local = await ProjectAsync("DL");
        var remote = await ProjectAsync("DO");
        var own = await LessonAsync(local.Key, "Read project-scoped operator state before acting");
        var foreign = await LessonAsync(remote.Key, "A different project's private release constraint");
        var run = Guid.NewGuid();
        await RegisterRunAsync(run, local.Id);
        using var scope = DreamExecutionScope.Begin(run, local.Id, "scripted");
        var actions = (IKnowledgeDreamActions)db.Service;
        await Should.ThrowAsync<WorkException>(() => actions.ExecuteToolAsync("dream_read", JsonSerializer.SerializeToElement(new { id = foreign.Id }), scope.Actor));
        foreach (var tool in new[] { "dream_read", "dream_lessons", "dream_similar", "dream_review_queue", "dream_work_trail" })
        {
            var input = tool == "dream_lessons" ? JsonSerializer.SerializeToElement(new { view = "unaddressed" }) : JsonSerializer.SerializeToElement(new { });
            var result = JsonSerializer.SerializeToElement(await actions.ExecuteToolAsync(tool, input, scope.Actor), WorkJson.Options);
            result.GetProperty("untrusted_data").GetBoolean().ShouldBeTrue();
            result.GetRawText().ShouldNotContain(foreign.Summary);
        }
        await Should.ThrowAsync<WorkException>(() => ToolAsync("dream_promote", new { id = own.Id, expected_version = own.Version }, foreign.Id));
        var digest = await actions.BuildDigestAsync(local.Id, null);
        digest.Data.GetRawText().ShouldNotContain(foreign.Summary);
        digest.HasSignal.ShouldBeTrue();
        digest.Fingerprint.ShouldBe((await actions.BuildDigestAsync(local.Id, null)).Fingerprint);
    }
}
