using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed partial class DreamingRunAcceptanceMariaDbTests
{
    private DreamRun ContinuationRun(Guid project, string status = "running") => new(Guid.NewGuid(), project, "run_now", Human,
        status, db.Clock.GetUtcNow().UtcDateTime, status == "running" ? null : db.Clock.GetUtcNow().UtcDateTime,
        "scripted", "scripted", 0, 0, 0, 1, 0, null, []);
    private static JsonElement CheckpointState(string value) => JsonSerializer.SerializeToElement(new { private_conversation = value });

    [Theory]
    [InlineData("running")]
    [InlineData("budget_exhausted")]
    [InlineData("failed")]
    public async Task Checkpoint_progress_is_atomic_private_and_transfers_only_from_the_expected_owner(string status)
    {
        var project = await ProjectAsync();
        await using var lease = await runs.TryAcquireAsync(project);
        var first = ContinuationRun(project, status);
        (await runs.GetCheckpointAsync(project)).ShouldBeNull();
        await runs.SaveProgressAsync(first, CheckpointState("private-first"), null);
        var saved = (await runs.GetCheckpointAsync(project))!;
        saved.ProjectId.ShouldBe(project);
        saved.RunId.ShouldBe(first.Id);
        saved.State.GetProperty("private_conversation").GetString().ShouldBe("private-first");
        var next = ContinuationRun(project, status) with { ContinuedFrom = first.Id, ContinuationAvailable = true };
        var conflict = await Should.ThrowAsync<WorkException>(() => runs.SaveProgressAsync(next, CheckpointState("stale"), null));
        conflict.Code.ShouldBe(WorkErrorCodes.Conflict);
        (await runs.GetRunAsync(project, next.Id)).ShouldBeNull();
        (await runs.GetCheckpointAsync(project))!.RunId.ShouldBe(first.Id);
        await runs.SaveProgressAsync(next, CheckpointState("private-next"), first.Id);
        (await runs.GetCheckpointAsync(project))!.RunId.ShouldBe(next.Id);
        var publicRuns = JsonSerializer.Serialize(await runs.ListRunsAsync(project, 10), WorkJson.Options);
        publicRuns.ShouldNotContain("private_conversation");
        publicRuns.ShouldNotContain("private-next");
        (await Should.ThrowAsync<WorkException>(() => runs.SaveProgressAsync(first, null, first.Id))).Code.ShouldBe(WorkErrorCodes.Conflict);
        await runs.SaveProgressAsync(next with { Status = "completed", EndedAt = db.Clock.GetUtcNow().UtcDateTime, ContinuationAvailable = false }, null, next.Id);
        (await runs.GetCheckpointAsync(project)).ShouldBeNull();
        (await runs.GetRunAsync(project, next.Id))!.Status.ShouldBe("completed");
    }

    [Fact]
    public async Task Checkpoint_writers_compete_for_one_owner_and_losing_runs_are_not_inserted()
    {
        var project = await ProjectAsync();
        var first = ContinuationRun(project, "failed");
        var second = ContinuationRun(project, "failed");
        async Task<bool> Save(DreamRun run)
        {
            try { await runs.SaveProgressAsync(run, CheckpointState(run.Id.ToString()), null); return true; }
            catch (WorkException e) when (e.Code == WorkErrorCodes.Conflict) { return false; }
        }
        var results = await Task.WhenAll(Save(first), Save(second));
        results.Count(success => success).ShouldBe(1);
        (await runs.ListRunsAsync(project, 10)).Count.ShouldBe(1);
        (await runs.GetCheckpointAsync(project))!.RunId.ShouldBe(results[0] ? first.Id : second.Id);
    }

    [Fact]
    public async Task Checkpoint_cross_project_run_reuse_and_failed_serialization_leave_state_unchanged()
    {
        var project = await ProjectAsync();
        var foreign = await ProjectAsync();
        var first = ContinuationRun(project, "failed");
        await runs.SaveProgressAsync(first, CheckpointState("original"), null);
        (await Should.ThrowAsync<WorkException>(() => runs.SaveProgressAsync(first with { ProjectId = foreign }, CheckpointState("foreign"), null)))
            .Code.ShouldBe(WorkErrorCodes.Conflict);
        (await runs.GetCheckpointAsync(foreign)).ShouldBeNull();
        (await runs.GetRunAsync(project, first.Id))!.ProjectId.ShouldBe(project);
        var broken = ContinuationRun(project, "failed") with { Actions = [new DreamAction("read", default, CheckpointState("result"), [], DateTime.UtcNow)] };
        await Should.ThrowAsync<InvalidOperationException>(() => runs.SaveProgressAsync(broken, CheckpointState("lost"), first.Id));
        (await runs.GetRunAsync(project, broken.Id)).ShouldBeNull();
        (await runs.GetCheckpointAsync(project))!.State.GetProperty("private_conversation").GetString().ShouldBe("original");
        using var scope = DreamExecutionScope.Begin(first.Id, project, "scripted");
        (await Should.ThrowAsync<WorkException>(() => runs.GetCheckpointAsync(foreign))).Code.ShouldBe(WorkErrorCodes.Forbidden);
        (await Should.ThrowAsync<WorkException>(() => runs.GetExecutionResultAsync(foreign, Guid.NewGuid()))).Code.ShouldBe(WorkErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData("dream_record", "record")]
    [InlineData("dream_verify", "verified")]
    [InlineData("dream_revise", "revised")]
    [InlineData("dream_relate", "same_as")]
    [InlineData("dream_relate", "contradicts")]
    [InlineData("dream_promote", "propose")]
    [InlineData("dream_challenge", "challenge")]
    public async Task Every_direct_dream_mutation_recovers_its_committed_execution_without_a_saved_tool_response(string tool, string action)
    {
        var project = await ProjectAsync();
        var target = await LessonAsync(project, "Target for " + action, tool == "dream_challenge" ? CorrectionSourceKind.Prompt : CorrectionSourceKind.Agent, Human);
        var evidence = await RecordAsync(project, "Evidence for " + action);
        var other = await LessonAsync(project, "A distinct independent lesson for " + action, CorrectionSourceKind.Agent, Human);
        if (tool == "dream_verify") (project, target) = await StaleFactAsync();
        var run = ContinuationRun(project);
        await runs.SaveRunAsync(run);
        var executionId = Guid.NewGuid();
        using (var scope = DreamExecutionScope.Begin(run.Id, project, run.Model))
        {
            scope.ExecutionId = executionId;
            if (tool == "dream_verify")
            {
                // Use the actual source reader in this run; a receipt never restores this capability.
                await sources.ExecuteAsync(project, "dream_read_source", JsonSerializer.SerializeToElement(new { repository = "codegraph", path = "Source.cs", detail = "full" }, WorkJson.Options));
                evidence = target;
            }
            object arguments = tool switch
            {
                "dream_record" => new { entry = new { kind = "fact", summary = "Committed once " + executionId, confidence = "high" }, evidence = Evidence(evidence), prevalence = "one entry" },
                "dream_revise" => new { id = target.Id, expected_version = target.Version, applies_when = new { activity = new[] { "merging" } }, evidence = Evidence(evidence), prevalence = "one lesson" },
                "dream_relate" when action == "same_as" => new { id = target.Id, expected_version = target.Version, same_as = other.Id, evidence = Evidence(evidence), prevalence = "two lessons" },
                "dream_relate" => new { id = target.Id, expected_version = target.Version, contradicts = other.Id, evidence = Evidence(evidence), prevalence = "two lessons" },
                "dream_challenge" => new { id = target.Id, reason = "Counterexample deserves review", evidence = Evidence(evidence), prevalence = "one rule" },
                _ => new { id = target.Id, expected_version = target.Version, evidence = Evidence(evidence), prevalence = "one entry" }
            };
            var result = JsonSerializer.SerializeToElement(await ((IKnowledgeDreamActions)db.Service).ExecuteToolAsync(tool, JsonSerializer.SerializeToElement(arguments, WorkJson.Options), scope.Actor), WorkJson.Options);
            result.GetProperty("journal_id").GetInt64().ShouldBeGreaterThan(0);
        }
        // Simulate a new store/process after commit but before saving an action/checkpoint result.
        var restarted = new MySqlDreamingStore(Microsoft.Extensions.Options.Options.Create(new MariaDbStorageOptions { ConnectionString = db.ConnectionString }));
        var recovered = (await restarted.GetExecutionResultAsync(project, executionId))!;
        recovered.RunId.ShouldBe(run.Id);
        recovered.Result.GetProperty("recovered_from_journal").GetBoolean().ShouldBeTrue();
        recovered.Result.GetProperty("data").GetProperty("action").GetString().ShouldBe(action);
        (await restarted.GetExecutionResultAsync(await ProjectAsync(), executionId)).ShouldBeNull();
        using var fresh = DreamExecutionScope.Begin(Guid.NewGuid(), project, run.Model);
        fresh.ReadFile("codegraph", "Source.cs").ShouldBeFalse();
    }

    [Fact]
    public async Task A_duplicate_journal_execution_rolls_back_the_entire_new_mutation_and_null_execution_ids_remain_supported()
    {
        var project = await ProjectAsync();
        var evidence = await RecordAsync(project, "Evidence anchor");
        var run = ContinuationRun(project);
        await runs.SaveRunAsync(run);
        var executionId = Guid.NewGuid();
        using var scope = DreamExecutionScope.Begin(run.Id, project, run.Model);
        scope.ExecutionId = executionId;
        async Task Write(string summary) => await ((IKnowledgeDreamActions)db.Service).ExecuteToolAsync("dream_record",
            JsonSerializer.SerializeToElement(new { entry = new { kind = "fact", summary, confidence = "high" }, evidence = Evidence(evidence), prevalence = "one anchor" }, WorkJson.Options), scope.Actor);
        await Write("First committed mutation");
        (await Should.ThrowAsync<MySqlException>(() => Write("Must roll back duplicate execution"))).Number.ShouldBe(1062);
        await using var c = new MySqlConnection(db.ConnectionString);
        (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM knowledge_entries WHERE project_id=@project AND summary='Must roll back duplicate execution'", new { project })).ShouldBe(0);
        (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dream_write_journal WHERE project_id=@project AND execution_id=@executionId", new { project, executionId })).ShouldBe(1);
        scope.ExecutionId = null;
        await Write("Legacy execution one");
        await Write("Legacy execution two");
        (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dream_write_journal WHERE project_id=@project AND execution_id IS NULL", new { project })).ShouldBe(2);
    }

    [Fact]
    public async Task A_committed_proposal_recovers_its_owner_and_id_and_duplicate_execution_rolls_back_a_different_proposal()
    {
        var project = await ProjectAsync();
        var first = await RecordAsync(project, "First proposal target");
        var second = await RecordAsync(project, "Second proposal target");
        var run = ContinuationRun(project);
        await runs.SaveRunAsync(run);
        var executionId = Guid.NewGuid();
        DreamProposalResult created;
        using (var scope = DreamExecutionScope.Begin(run.Id, project, run.Model))
        {
            scope.ExecutionId = executionId;
            DreamProposalSpec Spec(KnowledgeView target) => new(DreamProposalAction.Retract, target.Id, target.Version,
                "Evidence justifies review", "one target", [new(DreamEvidenceKind.Entry, target.Id.ToString())]);
            created = await db.Store.CreateDreamProposalAsync(project, run.Id, Spec(first), scope.Actor);
            (await Should.ThrowAsync<MySqlException>(() => db.Store.CreateDreamProposalAsync(project, run.Id, Spec(second), scope.Actor))).Number.ShouldBe(1062);
        }
        var result = (await runs.GetExecutionResultAsync(project, executionId))!;
        result.RunId.ShouldBe(run.Id);
        result.Result.GetProperty("recovered_from_proposal").GetBoolean().ShouldBeTrue();
        result.Result.GetProperty("data").GetProperty("proposal").GetProperty("id").GetGuid().ShouldBe(created.Proposal!.Id);
        await using var c = new MySqlConnection(db.ConnectionString);
        (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dream_proposals WHERE project_id=@project", new { project })).ShouldBe(1);
        (await runs.GetExecutionResultAsync(await ProjectAsync(), executionId)).ShouldBeNull();
    }
}
