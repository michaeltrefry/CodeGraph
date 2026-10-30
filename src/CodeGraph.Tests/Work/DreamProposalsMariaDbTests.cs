using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>Real MariaDB, deterministic scripted writes; no provider or model inference.</summary>
public sealed class DreamProposalsMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private async Task<(string Key, Guid Id)> Project(string prefix, params string[] repositories)
    {
        var key = await db.NewProjectAsync(prefix, repositories);
        var view = await db.CallAsync(WorkTools.GetWork, new { id = key });
        return (key, view.GetProperty("id").GetGuid());
    }
    private async Task<KnowledgeView> Fact(string key, string summary, string[]? paths = null)
    {
        var result = await db.CallAsync(WorkTools.Record, new { project = key, entry = new { kind = "fact", summary, confidence = "high", repositories = paths is null ? null : new[] { "codegraph" }, paths } });
        return (await db.Store.GetKnowledgeAsync(result.GetProperty("entry").GetProperty("id").GetGuid(), false))!;
    }
    // Begin scope in caller: AsyncLocal changes made inside another async method do not flow back.
    private async Task<Guid> RunId(Guid project)
    {
        var id = Guid.NewGuid();
        await using var c = new MySqlConnection(db.ConnectionString);
        await c.ExecuteAsync("INSERT INTO dreaming_runs(id,project_id,status,started_at,run_json) VALUES(@id,@project,'running',@now,'{}')", new { id, project, now = db.Clock.GetUtcNow().UtcDateTime });
        return id;
    }
    private static DreamProposalSpec Retire(KnowledgeView entry, params DreamEvidenceInput[] evidence) =>
        new(DreamProposalAction.Retract, entry.Id, entry.Version, "Referenced method was removed", "One fact checked against one changed method", evidence.Length == 0 ? [new DreamEvidenceInput(DreamEvidenceKind.Entry, entry.Id.ToString())] : evidence);

    [Fact]
    public async Task Scenario_2_stale_fact_stays_served_and_flagged_until_atomic_attributed_supersession()
    {
        var (key, project) = await Project("DPS", "codegraph");
        await using var c = new MySqlConnection(db.ConnectionString);
        await c.ExecuteAsync("INSERT INTO file_hashes(repository,rel_path,content_hash) VALUES('codegraph','dream-fact.cs','before')");
        var entry = await Fact(key, "OldName is the entry point", ["dream-fact.cs"]);
        await c.ExecuteAsync("UPDATE file_hashes SET content_hash='after' WHERE repository='codegraph' AND rel_path='dream-fact.cs'");
        await c.ExecuteAsync("INSERT INTO nodes(repository,label,name,qualified_name,file_path) VALUES('codegraph','Method','NewName','DreamFact.NewName','dream-fact.cs')");
        var node = await c.ExecuteScalarAsync<long>("SELECT id FROM nodes WHERE qualified_name='DreamFact.NewName'");
        var run = await RunId(project);
        DreamProposalResult proposed;
        using (var scope = DreamExecutionScope.Begin(run, project, "scripted"))
        {
            proposed = await db.Store.CreateDreamProposalAsync(project, run, new DreamProposalSpec(DreamProposalAction.Supersede, entry.Id, entry.Version,
                "The method was renamed to NewName", "One method changed in this project", [new(DreamEvidenceKind.GraphNode, node.ToString())],
                Replacement: new KnowledgeSpec(KnowledgeKind.Fact, "NewName is the entry point", null, KnowledgeConfidence.High, null, null, ["codegraph"], ["dream-fact.cs"], [node], null),
                ReplacementInput: new KnowledgeEntryInput { Kind = KnowledgeKind.Fact, Summary = "NewName is the entry point", Confidence = KnowledgeConfidence.High }), scope.Actor);
        }
        proposed.Created.ShouldBeTrue();
        var pending = (await db.Store.GetKnowledgeAsync(entry.Id, false))!;
        pending.Status.ShouldBe(KnowledgeStatus.Active); pending.PossiblyStale.ShouldBeTrue(); pending.Version.ShouldBe(entry.Version);
        var recall = await db.CallAsync(WorkTools.Recall, new { project = key, status = new[] { "active" } });
        recall.GetProperty("items").EnumerateArray().Any(row => row.GetProperty("id").GetGuid() == entry.Id).ShouldBeTrue();
        var queue = await db.CallAsync(WorkTools.ReviewQueue, new { project = key });
        var item = queue.GetProperty("items").EnumerateArray().Single(row => row.GetProperty("kind").GetString() == "dream_proposal");
        item.GetProperty("entry").GetProperty("possibly_stale").GetBoolean().ShouldBeTrue();
        item.GetProperty("proposal").GetProperty("run_id").GetGuid().ShouldBe(run);
        item.GetProperty("proposal").GetProperty("evidence")[0].GetProperty("id").GetString().ShouldBe(node.ToString());
        var accepted = await db.Store.ReviewDreamProposalAsync(project, proposed.Proposal!.Id, 1, DreamReviewAction.Accept, null, new WorkActor("reviewer", WorkActor.SessionKind, "review-session", Model: "review-model"));
        accepted.Entry!.Summary.ShouldBe("NewName is the entry point");
        accepted.Entry.Author.Name.ShouldBe("reviewer"); accepted.Proposal.ReviewedBy!.SessionId.ShouldBe("review-session");
        var old = (await db.Store.GetKnowledgeAsync(entry.Id, true))!;
        old.Status.ShouldBe(KnowledgeStatus.Superseded); old.SupersededBy.ShouldBe(accepted.Entry.Id);
        old.Revisions!.First().Author.Name.ShouldBe("reviewer");
        (await db.Store.ReadReviewQueueAsync(project, 0, 50)).Items.ShouldNotContain(row => row.Kind == ReviewKinds.DreamProposal);
    }

    [Fact]
    public async Task Scenario_8_rejection_survives_runs_and_rewording_but_real_new_evidence_unblocks()
    {
        var (key, project) = await Project("DPR");
        var target = await Fact(key, "The runner uses port 5020");
        var oldEvidence = await Fact(key, "Previously observed port 5020");
        var run = await RunId(project);
        DreamProposalResult first;
        using (var scope = DreamExecutionScope.Begin(run, project, "scripted"))
            first = await db.Store.CreateDreamProposalAsync(project, run, Retire(target), scope.Actor);
        await db.Store.ReviewDreamProposalAsync(project, first.Proposal!.Id, 1, DreamReviewAction.Reject, "The process still uses that port", new WorkActor("reviewer"));
        db.Clock.Advance(TimeSpan.FromSeconds(1));
        var nextRun = await RunId(project);
        using (var scope = DreamExecutionScope.Begin(nextRun, project, "scripted"))
        {
            var result = await db.Store.CreateDreamProposalAsync(project, nextRun, Retire(target) with { Rationale = "Different rationale", Prevalence = "Now described differently" }, scope.Actor);
            result.Suppressed.ShouldBeTrue(); result.Created.ShouldBeFalse();
            var old = await db.Store.CreateDreamProposalAsync(project, nextRun, Retire(target, new DreamEvidenceInput(DreamEvidenceKind.Entry, oldEvidence.Id.ToString())), scope.Actor);
            old.Suppressed.ShouldBeTrue();
        }
        var freshEvidence = await Fact(key, "A new source inspection found port 5037");
        using (var scope = DreamExecutionScope.Begin(nextRun, project, "scripted"))
        {
            var result = await db.Store.CreateDreamProposalAsync(project, nextRun, Retire(target, new DreamEvidenceInput(DreamEvidenceKind.Entry, freshEvidence.Id.ToString())), scope.Actor);
            result.Created.ShouldBeTrue(); result.Suppressed.ShouldBeFalse(); result.Proposal!.Id.ShouldNotBe(first.Proposal.Id);
        }
        (await db.Store.GetKnowledgeAsync(target.Id, false))!.Status.ShouldBe(KnowledgeStatus.Active);
    }

    [Fact]
    public async Task Stale_target_versions_and_concurrent_acceptance_cannot_apply_twice()
    {
        var (key, project) = await Project("DPV");
        var target = await Fact(key, "A fact requiring review");
        var run = await RunId(project); DreamProposalResult result;
        using (var scope = DreamExecutionScope.Begin(run, project, "scripted")) result = await db.Store.CreateDreamProposalAsync(project, run, Retire(target), scope.Actor);
        await db.CallAsync(WorkTools.Record, new { revise = new { id = target.Id, expected_version = target.Version, summary = "A fact edited by a session" } });
        (await Should.ThrowAsync<WorkException>(() => db.Store.ReviewDreamProposalAsync(project, result.Proposal!.Id, 1, DreamReviewAction.Accept, null, new WorkActor("reviewer")))).Code.ShouldBe(WorkErrorCodes.Conflict);
        (await db.Store.GetDreamProposalAsync(project, result.Proposal!.Id))!.Status.ShouldBe(DreamProposalStatus.Pending);
        await db.Store.ReviewDreamProposalAsync(project, result.Proposal.Id, 1, DreamReviewAction.Reject, "Target changed", new WorkActor("reviewer"));
        var another = await Fact(key, "A second fact requiring review");
        using (var scope = DreamExecutionScope.Begin(run, project, "scripted")) result = await db.Store.CreateDreamProposalAsync(project, run, Retire(another), scope.Actor);
        async Task<string> Accept(string name) { try { await db.Store.ReviewDreamProposalAsync(project, result.Proposal!.Id, 1, DreamReviewAction.Accept, null, new WorkActor(name)); return "accepted"; } catch (WorkException ex) { return ex.Code; } }
        var outcomes = await Task.WhenAll(Accept("one"), Accept("two"));
        outcomes.ShouldBe(["accepted", WorkErrorCodes.Conflict], ignoreOrder: true);
        (await db.Store.GetKnowledgeAsync(another.Id, true))!.Revisions!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Run_actor_target_evidence_and_review_scope_are_enforced_at_store_boundary()
    {
        var (key, project) = await Project("DPA"); var (otherKey, otherProject) = await Project("DPB");
        var target = await Fact(key, "A project fact"); var foreign = await Fact(otherKey, "A different project fact"); var run = await RunId(project);
        (await Should.ThrowAsync<WorkException>(() => db.Store.CreateDreamProposalAsync(project, run, Retire(target), new WorkActor("dreamer", "system", run.ToString(), Model: "scripted")))).Message.ShouldContain("server run");
        DreamProposalResult proposal;
        using (var scope = DreamExecutionScope.Begin(run, project, "scripted"))
        {
            await Should.ThrowAsync<WorkException>(() => db.Store.CreateDreamProposalAsync(project, run, Retire(foreign), scope.Actor));
            await Should.ThrowAsync<WorkException>(() => db.Store.CreateDreamProposalAsync(project, run, Retire(target, new DreamEvidenceInput(DreamEvidenceKind.Entry, foreign.Id.ToString())), scope.Actor));
            await Should.ThrowAsync<WorkException>(() => db.Store.CreateDreamProposalAsync(otherProject, run, Retire(foreign), scope.Actor));
            proposal = await db.Store.CreateDreamProposalAsync(project, run, Retire(target), scope.Actor);
            await Should.ThrowAsync<WorkException>(() => db.Store.ReviewDreamProposalAsync(project, proposal.Proposal!.Id, 1, DreamReviewAction.Accept, null, scope.Actor));
            await Should.ThrowAsync<WorkException>(() => db.Store.ReviewDreamProposalAsync(project, proposal.Proposal!.Id, 1, DreamReviewAction.Reject, "No", scope.Actor));
        }
        (await Should.ThrowAsync<WorkException>(() => db.Store.ReviewDreamProposalAsync(otherProject, proposal.Proposal!.Id, 1, DreamReviewAction.Accept, null, new WorkActor("reviewer")))).Code.ShouldBe(WorkErrorCodes.NotFound);
        await Should.ThrowAsync<WorkException>(() => db.Store.ReviewDreamProposalAsync(project, proposal.Proposal!.Id, 1, DreamReviewAction.Reject, " ", new WorkActor("reviewer")));
    }

    [Fact]
    public async Task Proposal_writes_wait_on_the_named_lock_before_starting_a_transaction()
    {
        var (key, project) = await Project("DPL"); var target = await Fact(key, "Named-lock serialization"); var run = await RunId(project);
        await using var c = new MySqlConnection(db.ConnectionString); await c.OpenAsync();
        var name = MySqlWorkStore.ServerLockName(c.Database, MySqlWorkStore.DreamProposalLockName);
        (await c.ExecuteScalarAsync<int>("SELECT GET_LOCK(@name,0)", new { name })).ShouldBe(1);
        using var scope = DreamExecutionScope.Begin(run, project, "scripted");
        var pending = db.Store.CreateDreamProposalAsync(project, run, Retire(target), scope.Actor);
        await Task.Delay(150); pending.IsCompleted.ShouldBeFalse();
        await c.ExecuteAsync("SELECT RELEASE_LOCK(@name)", new { name });
        (await pending).Created.ShouldBeTrue();
    }
    [Fact]
    public async Task Prompt_merge_acceptance_uses_exact_destination_version_and_accepting_actor()
    {
        var (key, project) = await Project("DPM");
        var one = await db.CallAsync(WorkTools.Record, new { project = key, entry = new { kind = "lesson", summary = "Prompt source merge candidate", source = new { kind = "prompt" }, applies_when = new { activity = new[] { "testing" } } } });
        var two = await db.CallAsync(WorkTools.Record, new { project = key, entry = new { kind = "lesson", summary = "Agent destination merge candidate", source = new { kind = "agent" }, applies_when = new { activity = new[] { "testing" } } } });
        var a = (await db.Store.GetKnowledgeAsync(one.GetProperty("entry").GetProperty("id").GetGuid(), false))!;
        var b = (await db.Store.GetKnowledgeAsync(two.GetProperty("entry").GetProperty("id").GetGuid(), false))!;
        var run = await RunId(project); DreamProposalResult proposal;
        using (var scope = DreamExecutionScope.Begin(run, project, "scripted"))
            proposal = await db.Store.CreateDreamProposalAsync(project, run, new DreamProposalSpec(DreamProposalAction.Merge, a.Id, a.Version, "Same instruction", "Two independent source events", [new(DreamEvidenceKind.Entry, a.Id.ToString()), new(DreamEvidenceKind.Entry, b.Id.ToString())], b.Id, b.Version), scope.Actor);
        (await db.Store.GetKnowledgeAsync(a.Id, false))!.Status.ShouldBe(KnowledgeStatus.Active);
        var result = await db.Store.ReviewDreamProposalAsync(project, proposal.Proposal!.Id, 1, DreamReviewAction.Accept, null, new WorkActor("accepting-person"));
        result.Entry!.Id.ShouldBe(b.Id);
        var merged = (await db.Store.GetKnowledgeAsync(a.Id, true))!;
        merged.Status.ShouldBe(KnowledgeStatus.Superseded); merged.Revisions!.First().Author.Name.ShouldBe("accepting-person");
        result.Proposal.ReviewedBy!.Name.ShouldBe("accepting-person");
    }

    [Fact]
    public async Task Draft_acceptance_stores_text_without_marking_the_rule_addressed_and_REST_reject_requires_reason()
    {
        var (key, project) = await Project("DPD");
        var one = await db.CallAsync(WorkTools.Record, new { project = key, entry = new { kind = "lesson", summary = "Check the stated behavior in a focused test", source = new { kind = "prompt", @ref = "draft-prompt" }, applies_when = new { activity = new[] { "testing" } } } });
        var id = one.GetProperty("entry").GetProperty("id").GetGuid();
        var session = (await db.CallAsync(WorkTools.StartSession, new { project = key, harness = "codex", activity = new[] { "testing" } })).GetProperty("session").GetProperty("id").GetString();
        await db.CallAsync(WorkTools.Record, new { session_id = session, entry = new { kind = "lesson", summary = "A regression needs a focused test", source = new { kind = "agent", @ref = "draft-event" }, same_as = id, applies_when = new { activity = new[] { "testing" } } } });
        var target = (await db.Store.GetKnowledgeAsync(id, false))!;
        var run = await RunId(project); DreamProposalResult proposal;
        using (var scope = DreamExecutionScope.Begin(run, project, "scripted"))
            proposal = await db.Store.CreateDreamProposalAsync(project, run, new DreamProposalSpec(DreamProposalAction.TestDraft, id, target.Version, "Rule recurred despite serving", "One recurrence while served", [new(DreamEvidenceKind.Entry, id.ToString())], Draft: "[Fact] public void Regression() { Assert.True(true); }"), scope.Actor);
        await using var host = await WorkRestHost.StartAsync(db.Service);
        var token = await host.CreateTokenAsync("draft-reviewer");
        async Task<(int Status, JsonElement Body)> Review(object body) => await host.SendAsync(HttpMethod.Post, $"/api/work/knowledge/dream-proposals/{proposal.Proposal!.Id}/review", JsonSerializer.Serialize(body, WorkJson.Options), token);
        (await Review(new { project = key, expected_version = 1, action = "reject" })).Status.ShouldBe(400);
        var (status, accepted) = await Review(new { project = key, expected_version = 1, action = "accept" });
        status.ShouldBe(200); accepted.GetProperty("proposal").GetProperty("draft").GetString().ShouldContain("Regression");
        accepted.GetProperty("proposal").GetProperty("reviewed_by").GetProperty("name").GetString().ShouldBe("draft-reviewer");
        var unchanged = (await db.Store.GetKnowledgeAsync(id, false))!;
        unchanged.Version.ShouldBe(target.Version); (unchanged.Lesson!.AddressedBy ?? []).ShouldBeEmpty();
        (await Review(new { project = key, expected_version = 1, action = "accept" })).Status.ShouldBe(409);
    }

}
