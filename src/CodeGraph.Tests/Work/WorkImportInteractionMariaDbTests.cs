using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using CodeGraph.Services.Work.Imports;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Imported items next to the other writers of main (sc-27876), against a real MariaDB:
/// GitHub evidence (S6) ignores historical PR links the import wrote but follows in-flight ones;
/// until cutover a delta sync is a three-way merge (only fields Shortcut changed are written; Shortcut
/// wins a field both changed); agent dependency edges survive Shortcut adding and dropping the same
/// relation; imported comments keep the item's comment order; failed objects are retried by the next
/// delta; a missing attachment file is downloaded again; pruning and retention leave imported rows alone.
/// </summary>
public sealed class WorkImportInteractionMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private const string CodeGraphTeam = "69f090da-4d9c-47cb-902d-d49fa77af7da";
    private const string Key = "GHIMP";

    /// <summary>The recorded source with faults injected on demand.</summary>
    private sealed class FaultySource(RecordedShortcutSource inner) : IShortcutSource
    {
        public HashSet<long> Failing { get; } = [];
        public RecordedShortcutSource Inner => inner;
        public Task<IReadOnlyList<ShortcutTeam>> ListTeamsAsync(CancellationToken ct) => inner.ListTeamsAsync(ct);
        public Task<IReadOnlyList<ShortcutMember>> ListMembersAsync(CancellationToken ct) => inner.ListMembersAsync(ct);
        public Task<IReadOnlyList<ShortcutState>> ListStatesAsync(CancellationToken ct) => inner.ListStatesAsync(ct);
        public Task<IReadOnlyList<ShortcutEpic>> ListEpicsAsync(CancellationToken ct) => inner.ListEpicsAsync(ct);
        public Task<ShortcutEpic?> GetEpicAsync(long epicId, CancellationToken ct) => inner.GetEpicAsync(epicId, ct);
        public Task<IReadOnlyList<ShortcutDocument>> ListEpicDocumentsAsync(long epicId, CancellationToken ct) => inner.ListEpicDocumentsAsync(epicId, ct);
        public Task<IReadOnlyList<ShortcutStorySummary>> ListStoriesAsync(DateTime? updatedSince, CancellationToken ct) => inner.ListStoriesAsync(updatedSince, ct);
        public Task<int> CountArchivedStoriesAsync(CancellationToken ct) => inner.CountArchivedStoriesAsync(ct);
        public IReadOnlyList<string> Warnings => inner.Warnings;
        public Task<byte[]?> DownloadFileAsync(string url, long maxBytes, CancellationToken ct) => inner.DownloadFileAsync(url, maxBytes, ct);

        public Task<ShortcutStory?> GetStoryAsync(long storyId, CancellationToken ct) =>
            Failing.Contains(storyId)
                ? throw new TaskCanceledException("The request timed out.") // an HTTP timeout, not the run's cancellation
                : inner.GetStoryAsync(storyId, ct);
    }

    private Task<JsonElement> GetAsync(string id) => db.CallAsync(WorkTools.GetWork, new { id });

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        return await connection.ExecuteScalarAsync<long>(sql);
    }

    private static FaultySource Source()
    {
        var recorded = RecordedShortcutSource.FromDirectory(WorkImportMappingTests.ShortcutFixtures);
        var source = new RecordedShortcutSource();
        source.Teams.AddRange(recorded.Teams);
        source.Members.AddRange(recorded.Members);
        source.States.AddRange(recorded.States);
        source.Epics[27864] = recorded.Epics[27864];
        var template = recorded.Stories[27876] with { StoryLinks = [], Blocked = false, Comments = [], EpicId = 27864 };
        // 27866: done, with its merged PR #122 (a historical PR).
        source.Stories[27866] = recorded.Stories[27866] with { StoryLinks = [] };
        source.Stories[27876] = template with { WorkflowStateId = 500000007 };
        // 90010: in progress with a PR still open in Shortcut (in flight).
        source.Stories[90010] = template with
        {
            Id = 90010, Name = "In flight",
            PullRequests = [new ShortcutPullRequest(200, "https://github.com/michaeltrefry/CodeGraph/pull/200", "Work in flight", false, false)]
        };
        source.Stories[90011] = template with { Id = 90011, Name = "Blocked one" };
        source.Stories[90012] = template with { Id = 90012, Name = "Blocking one" };
        source.Stories[7931] = recorded.Stories[7931] with { TeamId = CodeGraphTeam, EpicId = 27864, StoryLinks = [] };
        // 90013: a checklist of three open tasks, and a file whose bytes turn out over the size bound.
        source.Stories[90013] = template with
        {
            Id = 90013, Name = "Checklist",
            Tasks = [new ShortcutTask(91, "First task", false, 0), new ShortcutTask(92, "Second task", false, 1)],
            Files = [new ShortcutFile(95, "big.bin", "application/octet-stream", 10, "https://media.app.shortcut.com/api/attachments/files/x/big.bin", null, DateTime.UtcNow)]
        };
        source.Files["https://media.app.shortcut.com/api/attachments/files/x/big.bin"] = new byte[2_500_000];
        foreach (var (url, bytes) in recorded.Files)
            source.Files[url] = bytes;
        return new FaultySource(source);
    }

    private static void Touch(FaultySource source, long id, Func<ShortcutStory, ShortcutStory> change) =>
        source.Inner.Stories[id] = change(source.Inner.Stories[id]) with { UpdatedAt = DateTime.UtcNow.AddMinutes(5) };

    [Fact]
    public async Task Imported_items_alongside_github_agents_pruning_and_delta_syncs()
    {
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key = Key, name = "Imported", repositories = new[] { new { repository = "codegraph", role = "owned" } } } });
        var source = Source();
        var root = Path.Combine(Path.GetTempPath(), "cg-import-gh-" + Guid.NewGuid().ToString("N")[..6]);
        var service = WorkImportMariaDbTests.Service(db, source, null, root, maxAttachmentBytes: 2_000_000);
        await service.SaveSettingsAsync(ImportSources.Shortcut, new ImportSettings("GHHOLD", [new ImportTeamMapping(CodeGraphTeam, Key, "CodeGraph")]), approve: true, "owner", CancellationToken.None);

        // A story the source cannot read is a failure of that story, not of the run.
        source.Failing.Add(90012);
        var first = await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None);
        first.Status.ShouldBe(ImportRunStatuses.Completed, first.Error);
        first.Report!.Written.Failures.ShouldBe(1);
        // A file over the bound mid-stream is linked, not downloaded, and is not a failure.
        first.Report.Attachments.OverLimit.ShouldBe(1);
        first.Report.Attachments.Failed.ShouldBe(0);
        first.Report.FailedIds.ShouldBe(["sc-90012"]);
        (await db.RefusedAsync(WorkTools.GetWork, new { id = "sc-90012" })).Code.ShouldBe(WorkErrorCodes.NotFound);
        // The next delta reads it again, though it did not change in Shortcut.
        source.Failing.Clear();
        var retried = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        retried.Report!.Written.ItemsCreated.ShouldBe(1);
        retried.Report.FailedIds.ShouldBeEmpty();
        var done = await GetAsync("sc-27866");
        (await GetAsync("sc-27876")).GetProperty("status").GetString().ShouldBe("ready");

        // ---- S6: a historical PR the import linked moves nothing; an in-flight one moves its item ----
        var historical = await db.Store.RecordGitHubEvidenceAsync(new GitHubEvidenceSpec("d-1", GitHubEvidenceKind.IssueComment, ["codegraph"], ["late comment"], "michael",
            PullRequestUrl: "https://github.com/michaeltrefry/CodeGraph/pull/122", PullRequestNumber: 122, ExternalId: "c-1", Body: "late comment"));
        historical.Items.ShouldBeEmpty();
        (await GetAsync("sc-27866")).GetProperty("version").GetInt64().ShouldBe(done.GetProperty("version").GetInt64());
        // An agent adding the same link again takes it over: it is no longer historical.
        await db.UpdateAsync(done.GetProperty("key").GetString()!, new { add_links = new[] { new { kind = "pr", url = "https://github.com/michaeltrefry/CodeGraph/pull/122" } } });
        (await db.Store.RecordGitHubEvidenceAsync(new GitHubEvidenceSpec("d-1b", GitHubEvidenceKind.IssueComment, ["codegraph"], ["later comment"], "michael",
            PullRequestUrl: "https://github.com/michaeltrefry/CodeGraph/pull/122", PullRequestNumber: 122, ExternalId: "c-2", Body: "later comment"))).Items.ShouldHaveSingleItem();
        var merged = await db.Store.RecordGitHubEvidenceAsync(new GitHubEvidenceSpec("d-2", GitHubEvidenceKind.PullRequestMerged, ["codegraph"], ["Work in flight"], "michael",
            PullRequestUrl: "https://github.com/michaeltrefry/CodeGraph/pull/200", PullRequestNumber: 200, Title: "Work in flight"));
        merged.Items.Single().Key.ShouldBe((await GetAsync("sc-90010")).GetProperty("key").GetString());
        (await GetAsync("sc-90010")).GetProperty("merged_awaiting_acceptance").GetBoolean().ShouldBeTrue();

        // A push naming the sc alias moves the imported item forward, like any item.
        var push = await db.Store.RecordGitHubEvidenceAsync(new GitHubEvidenceSpec("d-3", GitHubEvidenceKind.Push, ["codegraph"], ["claude/sc-27876-imports"], "michael",
            Branch: "claude/sc-27876-imports", BranchUrl: "https://github.com/michaeltrefry/CodeGraph/tree/claude/sc-27876-imports"));
        push.Items.Single().Key.ShouldBe((await GetAsync("sc-27876")).GetProperty("key").GetString());
        (await GetAsync("sc-27876")).GetProperty("status").GetString().ShouldBe("in_progress");

        // ---- Three-way merge: a rename in Shortcut keeps GitHub's status move ...
        Touch(source, 27876, story => story with { Name = "Renamed in Shortcut" });
        var rename = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        rename.Report!.Written.ItemsUpdated.ShouldBe(1);
        var renamed = await GetAsync("sc-27876");
        renamed.GetProperty("title").GetString().ShouldBe("Renamed in Shortcut");
        renamed.GetProperty("status").GetString().ShouldBe("in_progress");
        renamed.GetProperty("links").EnumerateArray().ShouldContain(link => link.GetProperty("kind").GetString() == "branch");
        // ... and a status change in Shortcut wins over it.
        Touch(source, 27876, story => story with { WorkflowStateId = 500000009 });
        await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        (await GetAsync("sc-27876")).GetProperty("status").GetString().ShouldBe("in_review");

        // ---- An agent's edge survives Shortcut adding and then dropping the same relation ------
        var blocked = await GetAsync("sc-90011");
        var blocker = await GetAsync("sc-90012");
        await db.UpdateAsync(blocked.GetProperty("key").GetString()!, new { add_blocked_by = new[] { blocker.GetProperty("key").GetString() } });
        var link = new ShortcutStoryLink(777001, 90012, 90011, "blocks");
        Touch(source, 90011, story => story with { StoryLinks = [link] });
        Touch(source, 90012, story => story with { StoryLinks = [link] });
        await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        Touch(source, 90011, story => story with { StoryLinks = [] });
        Touch(source, 90012, story => story with { StoryLinks = [] });
        await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        (await GetAsync("sc-90011")).GetProperty("blocked_by").EnumerateArray().Single().GetProperty("key").GetString().ShouldBe(blocker.GetProperty("key").GetString());
        // An edge the import made goes when Shortcut drops it; one an agent removed is not put back while Shortcut is unchanged.
        Touch(source, 90011, story => story with { StoryLinks = [new ShortcutStoryLink(777002, 27876, 90011, "blocks")] });
        await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        (await GetAsync("sc-90011")).GetProperty("blocked_by").GetArrayLength().ShouldBe(2);
        var s11 = (await GetAsync("sc-90011")).GetProperty("key").GetString()!;
        await db.UpdateAsync(s11, new { remove_blocked_by = new[] { renamed.GetProperty("key").GetString() } });
        Touch(source, 90011, story => story with { Name = "Blocked one, renamed" });
        await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        (await GetAsync("sc-90011")).GetProperty("blocked_by").GetArrayLength().ShouldBe(1);

        // ---- Agent AC and evidence on a field Shortcut left alone survive a delta --------------
        await db.UpdateAsync(s11, new { acceptance_criteria = new[] { new { text = "Agent criterion", state = "met", evidence = "test X" } } });
        Touch(source, 90011, story => story with { Name = "Blocked one, renamed again" });
        await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        var withAc = await GetAsync("sc-90011");
        withAc.GetProperty("acceptance_criteria")[0].GetProperty("evidence").GetString().ShouldBe("test X");
        withAc.GetProperty("title").GetString().ShouldBe("Blocked one, renamed again");

        // ---- Acceptance criteria merge criterion by criterion -----------------------------------
        var checklist = await GetAsync("sc-90013");
        var checklistKey = checklist.GetProperty("key").GetString()!;
        var criteria = checklist.GetProperty("acceptance_criteria").EnumerateArray()
            .Select(criterion => new { id = criterion.GetProperty("id").GetString(), text = criterion.GetProperty("text").GetString(), state = "unmet", evidence = (string?)null }).ToList();
        criteria[0] = criteria[0] with { evidence = "Checked by the agent" };
        criteria.Add(new { id = (string?)"AGENT1", text = (string?)"Agent criterion", state = "met", evidence = (string?)"test Y" });
        await db.UpdateAsync(checklistKey, new { acceptance_criteria = criteria });
        Touch(source, 90013, story => story with { Tasks = [story.Tasks[0], story.Tasks[1] with { Complete = true }] });
        await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        var merged3 = (await GetAsync("sc-90013")).GetProperty("acceptance_criteria").EnumerateArray().ToList();
        merged3.Count.ShouldBe(3);
        merged3[0].GetProperty("evidence").GetString().ShouldBe("Checked by the agent");
        merged3[0].GetProperty("state").GetString().ShouldBe("unmet");
        merged3[1].GetProperty("state").GetString().ShouldBe("met");
        merged3[2].GetProperty("text").GetString().ShouldBe("Agent criterion");
        merged3[2].GetProperty("evidence").GetString().ShouldBe("test Y");
        // The file over the bound is linked once and not tried again.
        var big = (await GetAsync("sc-90013")).GetProperty("attachments").EnumerateArray().Single();
        big.GetProperty("stored").GetBoolean().ShouldBeFalse();
        big.GetProperty("skipped_reason").GetString().ShouldStartWith("Over the size bound");

        // ---- A blocker skipped as a cycle stays pending and is reported again -----------------
        var cycleLink = new ShortcutStoryLink(777003, 90011, 90012, "blocks");
        Touch(source, 90012, story => story with { StoryLinks = [cycleLink] });
        var cycle1 = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        cycle1.Report!.UnmappedRelations.ShouldContain(issue => issue.From == "sc-90012" && issue.Reason == "cycle");
        Touch(source, 90012, story => story with { Name = "Blocking one, renamed" });
        var cycle2 = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        cycle2.Report!.UnmappedRelations.ShouldContain(issue => issue.From == "sc-90012" && issue.Reason == "cycle");
        (await GetAsync("sc-90012")).GetProperty("blocked_by").GetArrayLength().ShouldBe(0);

        // ---- Comment order: a source comment older than a CodeGraph comment lands after it -----
        await db.UpdateAsync(s11, new { comment = "A CodeGraph comment." });
        var old = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Touch(source, 90011, story => story with { Comments = [new ShortcutComment(880001, "69f08f42-a951-46b3-b1a0-897a12271e62", "An old Shortcut comment.", old)] });
        await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        var comments = (await GetAsync("sc-90011")).GetProperty("comments").EnumerateArray().ToList();
        comments[^1].GetProperty("body").GetString().ShouldBe("An old Shortcut comment.");
        comments[^1].GetProperty("created_at").GetDateTime().ShouldBeGreaterThan(comments[^2].GetProperty("created_at").GetDateTime());
        comments[^1].GetProperty("source_created_at").GetDateTime().ShouldBe(old);

        // ---- An attachment whose stored file went missing is downloaded again ------------------
        var file = (await GetAsync("sc-7931")).GetProperty("attachments")[0];
        var stored = await db.Store.GetAttachmentAsync(file.GetProperty("id").GetGuid());
        var storage = new WorkAttachmentStorage(Microsoft.Extensions.Options.Options.Create(new WorkImportOptions { AttachmentStoragePath = root }),
            Microsoft.Extensions.Options.Options.Create(new CodeGraph.Services.Configuration.WikiOptions()));
        storage.Exists(stored!.StoragePath!).ShouldBeTrue();
        File.Delete(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Single());
        storage.Exists(stored.StoragePath!).ShouldBeFalse();
        Touch(source, 7931, story => story with { Name = story.Name + " (touched)" });
        var again = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        again.Report!.Written.AttachmentsStored.ShouldBe(1);
        var restored = await db.Store.GetAttachmentAsync(file.GetProperty("id").GetGuid());
        storage.Exists(restored!.StoragePath!).ShouldBeTrue();

        // ---- Session pruning and message retention leave imported rows alone -------------------
        var importedComments = await CountAsync("SELECT COUNT(*) FROM work_item_comments WHERE external_id IS NOT NULL");
        await db.Store.PruneSessionsAsync(DateTime.UtcNow.AddDays(1), 100);
        await db.Store.PruneMessagesAsync(DateTime.UtcNow.AddDays(1), 1000);
        (await CountAsync("SELECT COUNT(*) FROM work_item_comments WHERE external_id IS NOT NULL")).ShouldBe(importedComments);
        (await GetAsync("sc-27866")).GetProperty("comments")[0].GetProperty("author").GetString().ShouldBe("claude");

        // ---- A run declared dead is not revived by its late completion -------------------------
        var stale = await db.Store.StartImportRunAsync(ImportSources.Kanban, ImportMode.DryRun, null, "owner", TimeSpan.FromMinutes(10));
        await using (var connection = new MySqlConnection(db.ConnectionString))
            await connection.ExecuteAsync("UPDATE work_import_runs SET status = 'failed' WHERE id = @stale", new { stale });
        (await db.Store.CompleteImportRunAsync(stale, ImportRunStatuses.Completed, new ImportReport(), null)).ShouldBeFalse();
        (await db.Store.GetImportRunAsync(stale))!.Status.ShouldBe(ImportRunStatuses.Failed);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Kanban_settings_refuse_any_epic_but_the_code_review_one()
    {
        var service = WorkImportMariaDbTests.Service(db, null, RecordedKanbanSource.FromDirectory(WorkImportMappingTests.KanbanFixtures), Path.GetTempPath());
        var refused = await Should.ThrowAsync<WorkException>(() => service.SaveSettingsAsync(ImportSources.Kanban,
            new ImportSettings("SW", [], KanbanEpicId: Guid.NewGuid().ToString()), approve: true, "owner", CancellationToken.None));
        refused.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        refused.Message.ShouldContain(KanbanImporter.CodeReviewEpicId);
    }
}
