using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using CodeGraph.Services.Work.Imports;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Imported items next to the other writers of main (sc-27876): GitHub evidence (S6) ignores the PR
/// links an import wrote and moves an imported item only when the event names it (sc-NNNN), forward
/// only; until cutover Shortcut wins (a delta sync of a changed story overwrites its status, an
/// unchanged story keeps what GitHub did); session pruning and message retention leave imported rows,
/// attributed to the system actor, alone.
/// </summary>
public sealed class WorkImportInteractionMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private Task<System.Text.Json.JsonElement> GetAsync(string id) => db.CallAsync(WorkTools.GetWork, new { id });

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        return await connection.ExecuteScalarAsync<long>(sql);
    }

    [Fact]
    public async Task GitHub_evidence_retention_and_delta_sync_with_imported_items()
    {
        const string key = "GHIMP";
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key, name = "Imported", repositories = new[] { new { repository = "codegraph", role = "owned" } } } });
        var recorded = RecordedShortcutSource.FromDirectory(WorkImportMappingTests.ShortcutFixtures);
        var source = new RecordedShortcutSource();
        source.Teams.AddRange(recorded.Teams);
        source.Members.AddRange(recorded.Members);
        source.States.AddRange(recorded.States);
        source.Epics[27864] = recorded.Epics[27864];
        // 27866: done, with its merged PR #122. 27876: To Do, no links.
        source.Stories[27866] = recorded.Stories[27866] with { StoryLinks = [] };
        source.Stories[27876] = recorded.Stories[27876] with { StoryLinks = [], WorkflowStateId = 500000007, Blocked = false };
        var service = WorkImportMariaDbTests.Service(db, source, null, Path.Combine(Path.GetTempPath(), "cg-import-gh-" + Guid.NewGuid().ToString("N")[..6]));
        await service.SaveSettingsAsync(ImportSources.Shortcut,
            new ImportSettings("GHHOLD", [new ImportTeamMapping("69f090da-4d9c-47cb-902d-d49fa77af7da", key, "CodeGraph")]), approve: true, "owner", CancellationToken.None);
        (await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None)).Status.ShouldBe(ImportRunStatuses.Completed);
        var done = await GetAsync("sc-27866");
        var todo = await GetAsync("sc-27876");
        todo.GetProperty("status").GetString().ShouldBe("ready");

        // An event on a PR the import linked (the historical #122) moves nothing.
        var historical = await db.Store.RecordGitHubEvidenceAsync(new GitHubEvidenceSpec("d-1", GitHubEvidenceKind.IssueComment, ["codegraph"], ["late comment"], "michael",
            PullRequestUrl: "https://github.com/michaeltrefry/CodeGraph/pull/122", PullRequestNumber: 122, ExternalId: "c-1", Body: "late comment"));
        historical.Items.ShouldBeEmpty();
        (await GetAsync("sc-27866")).GetProperty("version").GetInt64().ShouldBe(done.GetProperty("version").GetInt64());

        // A push naming the sc alias moves the imported item forward, like any item.
        var push = await db.Store.RecordGitHubEvidenceAsync(new GitHubEvidenceSpec("d-2", GitHubEvidenceKind.Push, ["codegraph"], ["claude/sc-27876-imports"], "michael",
            Branch: "claude/sc-27876-imports", BranchUrl: "https://github.com/michaeltrefry/CodeGraph/tree/claude/sc-27876-imports"));
        push.Items.Single().Key.ShouldBe(todo.GetProperty("key").GetString());
        (await GetAsync("sc-27876")).GetProperty("status").GetString().ShouldBe("in_progress");

        // Session pruning and message retention leave imported rows alone.
        var comments = await CountAsync("SELECT COUNT(*) FROM work_item_comments WHERE external_id IS NOT NULL");
        comments.ShouldBeGreaterThan(0);
        await db.Store.PruneSessionsAsync(DateTime.UtcNow.AddDays(1), 100);
        await db.Store.PruneMessagesAsync(DateTime.UtcNow.AddDays(1), 1000);
        (await CountAsync("SELECT COUNT(*) FROM work_item_comments WHERE external_id IS NOT NULL")).ShouldBe(comments);
        (await CountAsync("SELECT COUNT(*) FROM work_items WHERE created_by = 'import:shortcut'")).ShouldBe(3); // two stories and the reconcile task
        (await GetAsync("sc-27866")).GetProperty("comments")[0].GetProperty("author").GetString().ShouldBe("claude");

        // Until cutover Shortcut wins: an unchanged story keeps what GitHub did ...
        var rerun = await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None);
        rerun.Report!.Written.ItemsUnchanged.ShouldBe(2);
        (await GetAsync("sc-27876")).GetProperty("status").GetString().ShouldBe("in_progress");

        // ... and a story changed in Shortcut takes Shortcut's state; the branch GitHub linked stays.
        source.Stories[27876] = source.Stories[27876] with { Name = "Renamed in Shortcut", UpdatedAt = DateTime.UtcNow.AddMinutes(5) };
        var delta = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        delta.Report!.Written.ItemsUpdated.ShouldBe(1);
        var after = await GetAsync("sc-27876");
        after.GetProperty("status").GetString().ShouldBe("ready");
        after.GetProperty("links").EnumerateArray().ShouldContain(link => link.GetProperty("kind").GetString() == "branch");
    }
}
