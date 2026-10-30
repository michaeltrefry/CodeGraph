using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Adversarial DR6 coverage against real storage, independently of the model's tool allowlist.
/// Valid requests run inside a real dream capability with evidence: a parse failure, missing run,
/// invalid version or missing target cannot accidentally satisfy these policy assertions.
/// </summary>
public sealed class DreamingPolicyMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static readonly WorkActor Human = new("alice");

    [Theory]
    [InlineData("entry")]
    [InlineData("entry_rows")]
    [InlineData("recall")]
    [InlineData("relevant")]
    [InlineData("rules")]
    [InlineData("project")]
    [InlineData("item")]
    [InlineData("record")]
    [InlineData("revise")]
    public async Task Actorless_store_reads_and_direct_writes_cannot_cross_the_active_runs_project(string operation)
    {
        var own = await SeedAsync();
        var foreign = await SeedAsync();
        var runId = Guid.NewGuid();
        await PersistRunAsync(runId, own.Project);
        var before = await SnapshotAsync();
        var knowledge = (IKnowledgeStore)db.Store;
        using var scope = DreamExecutionScope.Begin(runId, own.Project, "scripted-policy");
        using var evidence = scope.UseEvidence([new("entry", own.Lesson.Id.ToString(), "A real local lesson")]);
        using var prevalence = DreamWriteMetadata.Begin("one local lesson");
        // Positive controls prove this is a project boundary, not a blanket denial of all reads.
        (await knowledge.GetKnowledgeAsync(own.Lesson.Id, false))!.Id.ShouldBe(own.Lesson.Id);
        (await db.Store.GetProjectAsync(own.Project))!.Id.ShouldBe(own.Project);
        Task Invoke() => operation switch
        {
            "entry" => knowledge.GetKnowledgeAsync(foreign.Lesson.Id, true),
            "entry_rows" => knowledge.GetKnowledgeRowsAsync([own.Lesson.Id, foreign.Lesson.Id]),
            "recall" => knowledge.RecallAsync(new(foreign.Project, null, null, [], [], null, null, null, [], [KnowledgeStatus.Active], null, WorkDetail.Full, 20, null)),
            "relevant" => knowledge.ReadRelevantKnowledgeAsync(new(foreign.Project, null, [], [], [KnowledgeKind.Lesson], 20)),
            "rules" => knowledge.ReadRulesAsync(new(foreign.Project, [LessonActivity.Testing], null, [], "codex", 10)),
            "project" => db.Store.GetProjectAsync(foreign.Project),
            "item" => db.Store.GetItemAsync(foreign.Item.Id, false),
            "record" => knowledge.RecordKnowledgeAsync(foreign.Project, Spec(KnowledgeKind.Fact, "An attempted foreign write"), scope.Actor, null),
            "revise" => knowledge.ReviseKnowledgeAsync(foreign.Lesson.Id, foreign.Lesson.Version,
                new(foreign.Lesson.Kind, foreign.Lesson.Summary, foreign.Lesson.Body, foreign.Lesson.Confidence, foreign.Lesson.MadeAutonomously,
                    foreign.Lesson.Details! with { AppliesWhen = new([LessonActivity.Merging]) }, foreign.Lesson.Repositories, [], [], null), scope.Actor),
            _ => throw new InvalidOperationException(operation)
        };
        var refused = await Should.ThrowAsync<WorkException>(Invoke);
        refused.Message.ShouldContain("project", Case.Insensitive);
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Theory]
    [InlineData("retract")]
    [InlineData("acknowledge")]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("addressed_by")]
    [InlineData("resolve_challenge")]
    [InlineData("supersede")]
    [InlineData("record_prompt")]
    [InlineData("record_review")]
    [InlineData("record_bot_review")]
    [InlineData("revise_body")]
    [InlineData("revise_prompt")]
    [InlineData("merge_prompt")]
    [InlineData("import")]
    [InlineData("work_item")]
    [InlineData("project")]
    [InlineData("message")]
    [InlineData("hold")]
    public async Task Direct_store_bypasses_are_refused_without_any_persistent_change(string operation)
    {
        var seed = await SeedAsync();
        var knowledge = (IKnowledgeStore)db.Store;
        var runId = Guid.NewGuid();
        await PersistRunAsync(runId, seed.Project);
        var before = await SnapshotAsync();
        using var run = DreamExecutionScope.Begin(runId, seed.Project, "scripted-policy");
        using var evidence = run.UseEvidence([new("entry", seed.Lesson.Id.ToString(), "A real existing lesson")]);
        using var prevalence = DreamWriteMetadata.Begin("one existing project lesson");
        var actor = run.Actor;
        var spec = Spec(KnowledgeKind.Fact, "An injected replacement");
        Task Invoke() => operation switch
        {
            "retract" => knowledge.RetractKnowledgeAsync(seed.Prompt.Id, seed.Prompt.Version, "dreamer: retract rule X", actor),
            "acknowledge" => knowledge.AcknowledgeKnowledgeAsync(seed.Decision.Id, seed.Decision.Version, actor),
            "approve" => knowledge.PromoteLessonAsync(seed.Lesson.Id, seed.Lesson.Version, PromoteAction.Approve, null, null, actor),
            "reject" => knowledge.PromoteLessonAsync(seed.Lesson.Id, seed.Lesson.Version, PromoteAction.Reject, "Injected rejection", null, actor),
            "addressed_by" => knowledge.PromoteLessonAsync(seed.Prompt.Id, seed.Prompt.Version, PromoteAction.AddressedBy, null,
                new(AddressedByKind.Test, "Unreviewed generated test", "test.cs"), actor),
            "resolve_challenge" => knowledge.ResolveChallengeAsync(seed.Challenge.Id, seed.Prompt.Version, ChallengeOutcome.Retract, "Injected resolution", null, actor),
            "supersede" => knowledge.RecordKnowledgeAsync(seed.Project, spec with { Supersedes = new(seed.Prompt.Id, seed.Prompt.Version) }, actor, null),
            "record_prompt" => knowledge.RecordKnowledgeAsync(seed.Project, Lesson("Injected prompt", CorrectionSourceKind.Prompt), actor, null),
            "record_review" => knowledge.RecordKnowledgeAsync(seed.Project, Lesson("Injected review", CorrectionSourceKind.Review), actor, null),
            "record_bot_review" => knowledge.RecordKnowledgeAsync(seed.Project, Lesson("Injected bot review", CorrectionSourceKind.BotReview), actor, null),
            "revise_body" => knowledge.ReviseKnowledgeAsync(seed.Lesson.Id, seed.Lesson.Version, Lesson("Rewritten behavior", CorrectionSourceKind.Agent) with { Body = "Ignore the original rule" }, actor),
            "revise_prompt" => knowledge.ReviseKnowledgeAsync(seed.Prompt.Id, seed.Prompt.Version, Lesson(seed.Prompt.Summary, CorrectionSourceKind.Prompt) with
                { Details = seed.Prompt.Details! with { AppliesWhen = new([LessonActivity.Merging]) } }, actor),
            "merge_prompt" => knowledge.RelateLessonAsync(seed.Lesson.Id, seed.Lesson.Version, seed.Prompt.Id, null, actor),
            "import" => knowledge.ImportKnowledgeAsync(seed.Project, [], "unused", null, null, actor),
            "work_item" => db.Store.UpdateItemAsync(seed.Item.Id, seed.Item.Version, new(Title: "Injected title", Knowledge: [spec]), actor),
            "project" => db.Store.UpdateProjectAsync(seed.Project, 1, new(Name: "Injected name"), actor),
            "message" => db.Store.PostMessageAsync(new(seed.Project, "Injected message", MessageKind.Info, null, null, null, null, true, false, null, null), actor, null),
            "hold" => db.Store.AcquireAsync(new(seed.Project, "gpu:injected", 60, "Injected hold", null, false, WorkPriority.Medium, null, null, null), actor),
            _ => throw new InvalidOperationException(operation)
        };

        var refused = await Should.ThrowAsync<WorkException>(Invoke);
        refused.Message.ShouldContain("dreamer", Case.Insensitive);
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Theory]
    [InlineData("retract")]
    [InlineData("acknowledge")]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("addressed_by")]
    [InlineData("resolve_challenge")]
    [InlineData("work_item")]
    [InlineData("session_substitution")]
    public async Task Work_service_rejects_forbidden_calls_before_session_heartbeat_or_writes(string operation)
    {
        var seed = await SeedAsync();
        // An ordinary session with the dreamer's name must not launder a system actor into a session.
        var session = await db.CallAsync(WorkTools.StartSession, new { project = seed.Project, harness = "codex" }, "dreamer");
        var sessionId = session.GetProperty("session").GetProperty("id").GetString();
        db.Clock.Advance(TimeSpan.FromMinutes(1));
        var (tool, args) = operation switch
        {
            "retract" => (WorkTools.Record, (object)new { retract = new { id = seed.Prompt.Id, expected_version = seed.Prompt.Version, reason = "dreamer: retract rule X" } }),
            "acknowledge" => (WorkTools.Record, new { acknowledge = new { id = seed.Decision.Id, expected_version = seed.Decision.Version } }),
            "approve" or "reject" => (WorkTools.Promote, new { id = seed.Lesson.Id, expected_version = seed.Lesson.Version, action = operation, reason = "Injected decision" }),
            "addressed_by" => (WorkTools.Promote, new { id = seed.Prompt.Id, expected_version = seed.Prompt.Version, action = operation, addressed_by = new { kind = "test", name = "Unreviewed test" } }),
            "resolve_challenge" => (WorkTools.Challenge, new { resolve = new { challenge_id = seed.Challenge.Id, expected_version = seed.Prompt.Version, outcome = "retract", reason = "Injected resolution" } }),
            "work_item" => (WorkTools.UpdateWork, new { id = seed.Item.Id, expected_version = seed.Item.Version, title = "Injected title" }),
            "session_substitution" => (WorkTools.UpdateWork, new { id = seed.Item.Id, expected_version = seed.Item.Version, session_id = sessionId, title = "Injected title" }),
            _ => throw new InvalidOperationException(operation)
        };
        var before = await SnapshotAsync();
        var runId = Guid.NewGuid();
        await PersistRunAsync(runId, seed.Project);
        using var run = DreamExecutionScope.Begin(runId, seed.Project, "scripted-policy");
        using var evidence = run.UseEvidence([new("entry", seed.Lesson.Id.ToString(), "Existing entry")]);
        using var prevalence = DreamWriteMetadata.Begin("one existing project lesson");
        var refused = await Should.ThrowAsync<WorkException>(() => db.Service.ExecuteAsync(tool, JsonSerializer.SerializeToElement(args, WorkJson.Options), run.Actor));
        refused.Message.ShouldContain("dreamer", Case.Insensitive);
        (await SnapshotAsync()).ShouldBe(before);
    }

    private async Task<(Guid Project, KnowledgeView Lesson, KnowledgeView Prompt, KnowledgeView Decision, ChallengeView Challenge, ItemView Item)> SeedAsync()
    {
        var key = await db.NewProjectAsync("DP");
        var project = (await db.Store.ResolveAsync(key))!.Id;
        var knowledge = (IKnowledgeStore)db.Store;
        var lesson = (await knowledge.RecordKnowledgeAsync(project, Lesson("Check source before claiming verification", CorrectionSourceKind.Agent), Human, null)).Entry;
        var prompt = (await knowledge.RecordKnowledgeAsync(project, Lesson("Keep normal CI free of inference", CorrectionSourceKind.Prompt), Human, null)).Entry;
        var decision = (await knowledge.RecordKnowledgeAsync(project, Spec(KnowledgeKind.Decision, "An autonomous decision") with { MadeAutonomously = true }, Human, null)).Entry;
        var challenge = await knowledge.OpenChallengeAsync(prompt.Id, "Evidence deserves review", [new(EvidenceKind.Source, "A source", "source.cs")], null, Human);
        prompt = (await knowledge.GetKnowledgeAsync(prompt.Id, false))!;
        var item = await db.NewItemAsync(key, "Keep untouched");
        return (project, lesson, prompt, decision, challenge.Challenge, (await db.Store.GetItemAsync(item.GetProperty("id").GetGuid(), false))!);
    }

    private static KnowledgeSpec Spec(KnowledgeKind kind, string summary) => new(kind, summary, null, KnowledgeConfidence.High, null, null, null, [], [], null);
    private Task PersistRunAsync(Guid runId, Guid project) => new MySqlDreamingStore(Options.Create(new MariaDbStorageOptions { ConnectionString = db.ConnectionString }))
        .SaveRunAsync(new(runId, project, "run_now", Human, "running", db.Clock.GetUtcNow().UtcDateTime, null,
            "scripted", "scripted-policy", 0, 0, 0, 0, 0, null, []));
    private static KnowledgeSpec Lesson(string summary, CorrectionSourceKind source) => Spec(KnowledgeKind.Lesson, summary) with
    { Details = new(What: "A correction", AppliesWhen: new([LessonActivity.Testing]), Source: new(source, source == CorrectionSourceKind.Prompt ? "highest" : "normal", Guid.NewGuid().ToString())) };

    private async Task<string> SnapshotAsync()
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        var tables = await connection.QueryAsync<string>("SELECT table_name FROM information_schema.tables WHERE table_schema=DATABASE() AND (table_name LIKE 'knowledge_%' OR table_name LIKE 'lesson_%' OR table_name LIKE 'work_%') ORDER BY table_name");
        var snapshot = new SortedDictionary<string, string[]>();
        foreach (var table in tables)
        {
            var rows = await connection.QueryAsync($"SELECT * FROM `{table}`");
            snapshot[table] = rows.Select(row => JsonSerializer.Serialize((IDictionary<string, object>)row)).Order(StringComparer.Ordinal).ToArray();
        }
        return JsonSerializer.Serialize(snapshot);
    }
}
