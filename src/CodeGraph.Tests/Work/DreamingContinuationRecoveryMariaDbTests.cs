using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed partial class DreamingRunAcceptanceMariaDbTests
{
    private static JsonElement RecoveryJson(object value) => JsonSerializer.SerializeToElement(value, value.GetType(), WorkJson.Options);

    private async Task<DreamContinuationState> RecoveryStateAsync(DreamRun old)
    {
        var instructions = await runs.GetInstructionsAsync(old.ProjectId);
        return new()
        {
            SweepStartedAt = old.StartedAt, DigestSince = old.StartedAt.AddDays(-1),
            BaseRevision = instructions.Base.Revision, ProjectRevision = instructions.Project.Revision,
            Messages = [new("user", "{\"obsolete_digest\":true}")],
            ReadWindows = ((IKnowledgeDreamActions)db.Service).CaptureReadWindows(DreamExecutionScope.Current!)
        };
    }

    private static void RecoveryReplay(DreamContinuationState state, DreamToolCall call, JsonElement result, Guid? executionId = null)
    {
        var action = new DreamAction(call.Name, call.Arguments, result, [], DateTime.UtcNow, executionId);
        state.Replay.Add(DreamFrozenAction.Freeze(action));
        state.Messages.Add(new("assistant", ToolCalls: [call]));
        state.Messages.Add(new("tool", DreamModelContext.ModelJson(result), ToolCallId: call.Id));
        state.DeliveredPrefix = state.Messages.Count;
    }

    private static void RecoveryPending(DreamContinuationState state, DreamRun old, DreamToolCall call, Guid executionId)
    {
        state.Messages.Add(new("assistant", ToolCalls: [call]));
        state.Pending.Add(new(executionId, old.Id, call));
    }

    private Task SaveRecoveryAsync(DreamRun old, DreamContinuationState state) => runs.SaveProgressAsync(
        old with { Status = "failed", EndedAt = db.Clock.GetUtcNow().UtcDateTime, ContinuationAvailable = true },
        JsonSerializer.SerializeToElement(state, WorkJson.Options), null);

    [Fact]
    public async Task Restart_recovers_a_committed_pending_record_once_and_attributes_it_to_its_original_run()
    {
        var project = await ProjectAsync();
        var evidence = await RecordAsync(project, "Persisted evidence for the interrupted record");
        var old = ContinuationRun(project);
        await runs.SaveRunAsync(old);
        var executionId = Guid.NewGuid();
        var call = Call("old-record", "dream_record", new { entry = new { kind = "fact", summary = "Committed before checkpoint response", confidence = "high" }, evidence = Evidence(evidence), prevalence = "one anchor" });
        Guid entryId;
        long journalId;
        using (var scope = DreamExecutionScope.Begin(old.Id, project, old.Model))
        {
            scope.ExecutionId = executionId;
            var committed = RecoveryJson(await ((IKnowledgeDreamActions)db.Service).ExecuteToolAsync(call.Name, call.Arguments, scope.Actor));
            entryId = committed.GetProperty("data").GetProperty("entry").GetProperty("id").GetGuid();
            journalId = committed.GetProperty("journal_id").GetInt64();
            var state = await RecoveryStateAsync(old);
            RecoveryPending(state, old, call, executionId);
            await SaveRecoveryAsync(old, state);
        }
        var model = new ScriptedModel((request, _) =>
        {
            var recovered = JsonDocument.Parse(request.Messages.Single(m => m.ToolCallId == call.Id).Content!).RootElement;
            recovered.GetProperty("recovered_from_journal").GetBoolean().ShouldBeTrue();
            recovered.GetProperty("journal_id").GetInt64().ShouldBe(journalId);
            return Task.FromResult(Response());
        }) { InputCount = 100 };
        var resumed = await (await ServiceAsync(model)).RunAsync(project, "run_now", Human);
        resumed.Status.ShouldBe("completed", resumed.Reason);
        resumed.ContinuedFrom.ShouldBe(old.Id);
        resumed.Actions.ShouldBeEmpty();
        var applied = resumed.PreviouslyApplied!.Single();
        applied.RunId.ShouldBe(old.Id);
        applied.Action.ExecutionId.ShouldBe(executionId);
        applied.Action.Result.GetProperty("journal_id").GetInt64().ShouldBe(journalId);
        (await db.Store.GetKnowledgeAsync(entryId, false))!.Version.ShouldBe(1);
        await using var c = new MySqlConnection(db.ConnectionString);
        (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM knowledge_entries WHERE project_id=@project AND summary='Committed before checkpoint response'", new { project })).ShouldBe(1);
        (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dream_write_journal WHERE project_id=@project", new { project })).ShouldBe(1);
        (await runs.GetCheckpointAsync(project)).ShouldBeNull();
        resumed.ContinuationAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task Historical_source_and_restored_read_windows_do_not_authorize_pending_or_new_verification()
    {
        var (project, target) = await StaleFactAsync();
        await RecordAsync(project, "Another entry makes an immutable read window");
        var old = ContinuationRun(project);
        await runs.SaveRunAsync(old);
        var oldVerify = Call("old-verify", "dream_verify", Verify(target));
        var oldSource = Call("old-source", "dream_read_source", new { repository = "codegraph", path = "Source.cs", detail = "full" });
        var oldPage = Call("old-page", "dream_read", new { limit = 1 });
        using (var scope = DreamExecutionScope.Begin(old.Id, project, old.Model))
        {
            var sourceResult = RecoveryJson(await sources.ExecuteAsync(project, oldSource.Name, oldSource.Arguments));
            scope.ReadFile("codegraph", "Source.cs").ShouldBeTrue();
            var pageResult = RecoveryJson(await ((IKnowledgeDreamActions)db.Service).ExecuteToolAsync(oldPage.Name, oldPage.Arguments, scope.Actor));
            var state = await RecoveryStateAsync(old);
            RecoveryReplay(state, oldSource, sourceResult);
            RecoveryReplay(state, oldPage, pageResult);
            RecoveryPending(state, old, oldVerify, Guid.NewGuid());
            await SaveRecoveryAsync(old, state);
        }
        var turn = 0;
        var model = new ScriptedModel((request, _) =>
        {
            if (++turn == 1)
            {
                var refusal = JsonDocument.Parse(request.Messages.Single(m => m.ToolCallId == oldVerify.Id).Content!).RootElement;
                refusal.GetProperty("error").GetString().ShouldBe(WorkErrorCodes.Forbidden);
                refusal.GetProperty("message").GetString()!.ShouldContain("earlier attempt");
                request.Messages.Single(m => m.ToolCallId == oldSource.Id).Content!.ShouldContain("verification_ready");
                DreamExecutionScope.Current!.ReadFile("codegraph", "Source.cs").ShouldBeFalse();
                return Task.FromResult(Response(Call("new-without-source", "dream_verify", Verify(target))));
            }
            if (turn == 2)
            {
                var refusal = JsonDocument.Parse(request.Messages.Single(m => m.ToolCallId == "new-without-source").Content!).RootElement;
                refusal.GetProperty("error").GetString().ShouldBe(WorkErrorCodes.Forbidden);
                return Task.FromResult(Response(Call("fresh-source", "dream_read_source", new { repository = "codegraph", path = "Source.cs", detail = "full" })));
            }
            if (turn == 3)
            {
                DreamExecutionScope.Current!.ReadFile("codegraph", "Source.cs").ShouldBeTrue();
                return Task.FromResult(Response(Call("fresh-verify", "dream_verify", Verify(target))));
            }
            return Task.FromResult(Response());
        }) { InputCount = 100 };
        var resumed = await (await ServiceAsync(model)).RunAsync(project, "run_now", Human);
        resumed.Status.ShouldBe("completed", resumed.Reason);
        resumed.Actions.Count.ShouldBe(4);
        resumed.Actions.Take(2).ShouldAllBe(a => a.Result.GetProperty("error").GetString() == WorkErrorCodes.Forbidden);
        resumed.Actions.Last().Tool.ShouldBe("dream_verify");
        resumed.Actions.Last().Result.GetProperty("journal_id").GetInt64().ShouldBeGreaterThan(0);
        (await db.Store.GetKnowledgeAsync(target.Id, false))!.PossiblyStale.ShouldBeFalse();
        await using var c = new MySqlConnection(db.ConnectionString);
        (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dream_write_journal WHERE project_id=@project", new { project })).ShouldBe(1);
        (await runs.GetCheckpointAsync(project)).ShouldBeNull();
    }

    [Fact]
    public async Task Restart_recovers_a_committed_pending_proposal_once_without_submitting_another()
    {
        var project = await ProjectAsync();
        var target = await RecordAsync(project, "Proposal target before interrupted response");
        var old = ContinuationRun(project);
        await runs.SaveRunAsync(old);
        var executionId = Guid.NewGuid();
        var call = Call("old-proposal", "dream_propose", new { action = "retract", target = target.Id, expected_version = target.Version,
            rationale = "Needs human review", prevalence = "one target", evidence = new[] { new { kind = "entry", id = target.Id.ToString() } } });
        Guid proposalId;
        using (var scope = DreamExecutionScope.Begin(old.Id, project, old.Model))
        {
            scope.ExecutionId = executionId;
            var committed = RecoveryJson(await ((IKnowledgeDreamActions)db.Service).ExecuteToolAsync(call.Name, call.Arguments, scope.Actor));
            proposalId = committed.GetProperty("data").GetProperty("proposal").GetProperty("id").GetGuid();
            var state = await RecoveryStateAsync(old);
            RecoveryPending(state, old, call, executionId);
            await SaveRecoveryAsync(old, state);
        }
        var model = new ScriptedModel((request, _) =>
        {
            var recovered = JsonDocument.Parse(request.Messages.Single(m => m.ToolCallId == call.Id).Content!).RootElement;
            recovered.GetProperty("recovered_from_proposal").GetBoolean().ShouldBeTrue();
            recovered.GetProperty("data").GetProperty("proposal").GetProperty("id").GetGuid().ShouldBe(proposalId);
            return Task.FromResult(Response());
        }) { InputCount = 100 };
        var resumed = await (await ServiceAsync(model)).RunAsync(project, "run_now", Human);
        resumed.Status.ShouldBe("completed", resumed.Reason);
        resumed.Actions.ShouldBeEmpty();
        resumed.PreviouslyApplied!.Single().RunId.ShouldBe(old.Id);
        resumed.PreviouslyApplied!.Single().Action.ExecutionId.ShouldBe(executionId);
        (await db.Store.GetKnowledgeAsync(target.Id, false))!.Status.ShouldBe(KnowledgeStatus.Active);
        await using var c = new MySqlConnection(db.ConnectionString);
        (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dream_proposals WHERE project_id=@project", new { project })).ShouldBe(1);
        (await runs.GetCheckpointAsync(project)).ShouldBeNull();
    }

    [Fact]
    public async Task Resume_refreshes_instructions_and_signals_and_requires_current_versions_for_reassessed_writes()
    {
        var project = await ProjectAsync();
        var target = await LessonAsync(project, "Old lesson before another actor edits it", CorrectionSourceKind.Agent, Human);
        var old = ContinuationRun(project);
        await runs.SaveRunAsync(old);
        object Revise(long version) => new { id = target.Id, expected_version = version, applies_when = new { activity = new[] { "merging" } }, evidence = Evidence(target), prevalence = "one lesson" };
        using (var scope = DreamExecutionScope.Begin(old.Id, project, old.Model))
        {
            var state = await RecoveryStateAsync(old);
            RecoveryPending(state, old, Call("old-revise", "dream_revise", Revise(target.Version)), Guid.NewGuid());
            await SaveRecoveryAsync(old, state);
        }
        db.Clock.Advance(TimeSpan.FromSeconds(5));
        await db.CallAsync(WorkTools.Record, new { revise = new { id = target.Id, expected_version = target.Version, summary = "A human changed the current lesson" } });
        var current = (await db.Store.GetKnowledgeAsync(target.Id, false))!;
        var newSignal = await RecordAsync(project, "A new signal arrived while the previous attempt was stopped");
        var instructions = await runs.GetInstructionsAsync(project);
        var revision = await runs.SetInstructionsAsync(project, "project", "REVISED_INSTRUCTIONS_REQUIRE_CURRENT_VERSION", instructions.Project.Revision, Human);
        var turn = 0;
        var model = new ScriptedModel((request, _) =>
        {
            if (++turn == 1)
            {
                request.System.ShouldContain("REVISED_INSTRUCTIONS_REQUIRE_CURRENT_VERSION");
                request.System.ShouldContain("supersede");
                request.Messages[0].Content!.ShouldNotContain("obsolete_digest");
                request.Messages[0].Content!.ShouldContain(newSignal.Id.ToString());
                JsonDocument.Parse(request.Messages.Single(m => m.ToolCallId == "old-revise").Content!).RootElement.GetProperty("error").GetString().ShouldBe(WorkErrorCodes.Forbidden);
                return Task.FromResult(Response(Call("stale-version", "dream_revise", Revise(target.Version))));
            }
            if (turn == 2)
            {
                JsonDocument.Parse(request.Messages.Single(m => m.ToolCallId == "stale-version").Content!).RootElement.GetProperty("error").GetString().ShouldBe(WorkErrorCodes.Conflict);
                return Task.FromResult(Response(Call("current-version", "dream_revise", Revise(current.Version))));
            }
            return Task.FromResult(Response());
        }) { InputCount = 100 };
        var resumed = await (await ServiceAsync(model)).RunAsync(project, "run_now", Human);
        resumed.Status.ShouldBe("completed", resumed.Reason);
        resumed.ProjectInstructionRevision.ShouldBe(revision.Revision);
        resumed.Actions.Count.ShouldBe(3);
        resumed.Actions.Last().Result.TryGetProperty("error", out _).ShouldBeFalse();
        var after = (await db.Store.GetKnowledgeAsync(target.Id, false))!;
        after.Summary.ShouldBe(current.Summary);
        after.Version.ShouldBe(current.Version + 1);
        after.Details!.AppliesWhen!.Activity.ShouldBe([LessonActivity.Merging]);
    }

    [Fact]
    public async Task Reverted_historical_writes_are_refreshed_in_prior_outcomes_and_never_reapplied()
    {
        var project = await ProjectAsync();
        var evidence = await RecordAsync(project, "Evidence for an eventually reverted fact");
        var old = ContinuationRun(project);
        await runs.SaveRunAsync(old);
        var executionId = Guid.NewGuid();
        var call = Call("historical-record", "dream_record", new { entry = new { kind = "fact", summary = "Reverted fact must stay retracted", confidence = "high" }, evidence = Evidence(evidence), prevalence = "one anchor" });
        Guid entryId;
        long journalId;
        using (var scope = DreamExecutionScope.Begin(old.Id, project, old.Model))
        {
            scope.ExecutionId = executionId;
            var result = RecoveryJson(await ((IKnowledgeDreamActions)db.Service).ExecuteToolAsync(call.Name, call.Arguments, scope.Actor));
            entryId = result.GetProperty("data").GetProperty("entry").GetProperty("id").GetGuid();
            journalId = result.GetProperty("journal_id").GetInt64();
            var state = await RecoveryStateAsync(old);
            RecoveryReplay(state, call, result, executionId);
            await SaveRecoveryAsync(old, state);
        }
        var reverted = await ((IKnowledgeDreamActions)db.Service).RevertAsync(project, old.Id, Human);
        reverted.Entries.Single().Status.ShouldBe("reverted");
        var model = new ScriptedModel((request, _) =>
        {
            var outcomes = JsonDocument.Parse(request.Messages[0].Content!).RootElement.GetProperty("untrusted_prior_outcomes");
            var prior = outcomes.EnumerateArray().Single();
            prior.GetProperty("journal_id").GetInt64().ShouldBe(journalId);
            prior.GetProperty("origin_run_id").GetGuid().ShouldBe(old.Id);
            prior.GetProperty("reverted_at").ValueKind.ShouldBe(JsonValueKind.String);
            return Task.FromResult(Response());
        }) { InputCount = 100 };
        var resumed = await (await ServiceAsync(model)).RunAsync(project, "run_now", Human);
        resumed.Status.ShouldBe("completed", resumed.Reason);
        resumed.Actions.ShouldBeEmpty();
        (await db.Store.GetKnowledgeAsync(entryId, false))!.Status.ShouldBe(KnowledgeStatus.Retracted);
        await using var c = new MySqlConnection(db.ConnectionString);
        (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dream_write_journal WHERE project_id=@project", new { project })).ShouldBe(1);
        (await runs.GetCheckpointAsync(project)).ShouldBeNull();
    }

    [Fact]
    public async Task An_interrupted_provider_send_preserves_usage_uncertainty_and_a_resumable_checkpoint()
    {
        var project = await ProjectAsync();
        await RecordAsync(project, "Signal before a provider send is interrupted");
        var interrupted = new ScriptedModel((_, _) => throw new OperationCanceledException("scripted transport interruption after send")) { InputCount = 100 };
        var failed = await (await ServiceAsync(interrupted)).RunAsync(project, "run_now", Human);
        failed.Status.ShouldBe("failed");
        failed.UsageUncertain.ShouldBeTrue();
        failed.InputTokens.ShouldBe(0);
        failed.OutputTokens.ShouldBe(0);
        failed.ContinuationAvailable.ShouldBeTrue();
        (await runs.GetCheckpointAsync(project))!.RunId.ShouldBe(failed.Id);
        var resumed = await (await ServiceAsync(new ScriptedModel(Response()) { InputCount = 100 })).RunAsync(project, "run_now", Human);
        resumed.Status.ShouldBe("completed", resumed.Reason);
        resumed.ContinuedFrom.ShouldBe(failed.Id);
        resumed.UsageUncertain.ShouldBeFalse();
        (await runs.GetRunAsync(project, failed.Id))!.UsageUncertain.ShouldBeTrue();
        (await runs.GetCheckpointAsync(project)).ShouldBeNull();
    }
}
