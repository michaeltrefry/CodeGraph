using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>One real database/seed for the remaining raw work-read routes. A restricted model
/// tool catalog cannot substitute for the project checks on these callable store methods.</summary>
public sealed class DreamingWorkReadScopeMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    [Fact]
    public async Task Ambient_run_scope_rejects_foreign_work_reads_global_reads_and_delivery_writes()
    {
        var own = await SeedAsync("WO", "codegraph");
        var foreign = await SeedAsync("WF", "inference");
        var runId = Guid.NewGuid();
        var runs = new MySqlDreamingStore(Options.Create(new MariaDbStorageOptions { ConnectionString = db.ConnectionString }));
        await runs.SaveRunAsync(new(runId, own.Project, "run_now", new("owner"), "running", db.Clock.GetUtcNow().UtcDateTime,
            null, "scripted", "scripted-read-scope", 0, 0, 0, 0, 0, null, []));
        var before = await SnapshotAsync();
        using var scope = DreamExecutionScope.Begin(runId, own.Project, "scripted-read-scope");
        using var evidence = scope.UseEvidence([new("item", own.Item.ToString(), "An existing local item")]);
        using var prevalence = DreamWriteMetadata.Begin("one local item");

        var routes = new Dictionary<string, Func<Task>>
        {
            ["session"] = () => db.Store.GetSessionAsync(foreign.Session),
            ["sessions"] = () => db.Store.ListSessionsAsync(foreign.Project, false, 20),
            ["briefing"] = () => db.Store.ReadBriefingAsync(foreign.Session, null),
            ["briefing foreign focus"] = () => db.Store.ReadBriefingAsync(own.Session, foreign.Item),
            ["resources"] = () => db.Store.ListResourcesAsync(foreign.Project, null, 20),
            ["changes"] = () => db.Store.ReadChangesAsync(foreign.Project, 0, 20),
            ["recent changes"] = () => db.Store.ReadRecentChangesAsync(foreign.Project, 20),
            ["entity changes"] = () => db.Store.ReadEntityChangesAsync(foreign.Item, 20),
            ["messages"] = () => db.Store.ListMessagesAsync(new(foreign.Project, null, null, null, null, false, false, null, 20)),
            ["message events"] = () => db.Store.ReadMessageEventsAsync(foreign.Project, 0, 20),
            ["GitHub evidence"] = () => db.Store.ListGitHubEvidenceAsync(foreign.Item, 20),
            ["work areas"] = () => db.Store.ReadLiveWorkAreasAsync(foreign.Project),
            ["unread count"] = () => db.Store.CountUnreadAsync(MessageChannels.HumanRecipient, foreign.Project),
            ["repository owner"] = () => db.Store.FindOwningProjectAsync("inference"),
            ["global changes"] = () => db.Store.ReadChangesAsync(null, 0, 20),
            ["global change head"] = () => db.Store.GetChangeHeadAsync(),
            ["global inbox head"] = () => db.Store.GetInboxHeadAsync(),
            ["global message event head"] = () => db.Store.GetMessageEventHeadAsync(),
            // These apparent reads write delivery/session state; even the current project is refused.
            ["inbox delivery"] = () => db.Store.ReadInboxAsync(new(own.Project, MessageChannels.HumanRecipient, 0, true, 20)),
            ["mark delivered"] = () => db.Store.MarkDeliveredAsync(MessageChannels.HumanRecipient, [own.Message]),
            ["served rules"] = () => ((IKnowledgeStore)db.Store).LogServedRulesAsync(own.Session, own.Project, []),
            ["briefing context"] = () => ((IKnowledgeStore)db.Store).SetSessionBriefingContextAsync(own.Session, [LessonActivity.Testing], []),
            ["message retention"] = () => db.Store.PruneMessagesAsync(DateTime.UtcNow.AddDays(1), 20),
            ["session retention"] = () => db.Store.PruneSessionsAsync(DateTime.UtcNow.AddDays(1), 20),
            ["ordinary actor substitution"] = () => ((IKnowledgeStore)db.Store).RecordKnowledgeAsync(own.Project,
                new(KnowledgeKind.Fact, "A forged ordinary actor", null, KnowledgeConfidence.High, null, null, null, [], [], null), new("owner"), null)
        };
        foreach (var (route, call) in routes)
        {
            var refused = await Should.ThrowAsync<WorkException>(call, route);
            refused.Code.ShouldBe(WorkErrorCodes.Forbidden, route);
        }

        // Positive reads make a blanket read denial an invalid implementation of this contract.
        (await db.Store.GetSessionAsync(own.Session))!.ProjectId.ShouldBe(own.Project);
        (await db.Store.ListSessionsAsync(own.Project, false, 20)).Single().ProjectId.ShouldBe(own.Project);
        (await db.Store.ListResourcesAsync(own.Project, null, 20)).Single().Project.ShouldBe(own.Key);
        (await db.Store.ReadChangesAsync(own.Project, 0, 50)).ShouldAllBe(change => change.ProjectKey == own.Key);
        (await db.Store.ReadEntityChangesAsync(own.Item, 20)).ShouldNotBeEmpty();
        (await db.Store.ListMessagesAsync(new(own.Project, null, null, null, null, false, false, null, 20))).Items.Single().Body.ShouldBe(own.Secret);
        (await db.Store.ReadMessageEventsAsync(own.Project, 0, 20)).ShouldNotBeEmpty();
        (await db.Store.ListGitHubEvidenceAsync(own.Item, 20)).Single().Body.ShouldBe(own.Secret);
        (await db.Store.ReadLiveWorkAreasAsync(own.Project)).ShouldNotBeEmpty();
        (await db.Store.FindOwningProjectAsync("codegraph")).ShouldBe(own.Project);
        (await SnapshotAsync()).ShouldBe(before);
    }

    private async Task<(Guid Project, string Key, Guid Session, Guid Item, Guid Message, string Secret)> SeedAsync(string prefix, string repository)
    {
        var key = await db.NewProjectAsync(prefix, repository);
        var project = (await db.Store.ResolveAsync(key))!.Id;
        var user = "owner-" + prefix;
        var secret = "Private work trail of " + key;
        var item = await db.NewItemAsync(key, secret);
        var session = (await db.CallAsync(WorkTools.StartSession, new { project = key, harness = "codex", repository }, user)).GetProperty("session").GetProperty("id").GetGuid();
        var claimed = (await db.CallAsync(WorkTools.NextWork, new { project = key, session_id = session }, user)).GetProperty("item");
        await db.CallAsync(WorkTools.UpdateWork, new { id = item.GetProperty("id").GetGuid(), session_id = session,
            expected_version = claimed.GetProperty("version").GetInt64(), status = "in_progress", touches = new[] { "Secret.cs" } }, user);
        await db.Store.AcquireAsync(new(project, "gpu:scope", 3600, secret, null, false, WorkPriority.Medium, null, null, null), new(user));
        var message = await db.Store.PostMessageAsync(new(project, secret, MessageKind.Info, null, null, null, null, true, false, null, null), new(user), null);
        await db.Store.RecordGitHubEvidenceAsync(new(Guid.NewGuid().ToString(), GitHubEvidenceKind.ReviewComment, [repository],
            [item.GetProperty("key").GetString()!], "reviewer", PullRequestUrl: $"https://github.com/example/{repository}/pull/1",
            PullRequestNumber: 1, ExternalId: Guid.NewGuid().ToString(), Body: secret));
        return (project, key, session, item.GetProperty("id").GetGuid(), message.Message.Id, secret);
    }

    private async Task<string> SnapshotAsync()
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        var tables = await connection.QueryAsync<string>("SELECT table_name FROM information_schema.tables WHERE table_schema=DATABASE() AND (table_name LIKE 'work_%' OR table_name LIKE 'knowledge_%' OR table_name LIKE 'lesson_%' OR table_name LIKE 'dream%') ORDER BY table_name");
        var snapshot = new SortedDictionary<string, string[]>();
        foreach (var table in tables)
            snapshot[table] = (await connection.QueryAsync($"SELECT * FROM `{table}`")).Select(row => JsonSerializer.Serialize((IDictionary<string, object>)row)).Order(StringComparer.Ordinal).ToArray();
        return JsonSerializer.Serialize(snapshot);
    }
}
