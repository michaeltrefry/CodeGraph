using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Work;
using CodeGraph.Services.Work.Imports;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The imports (sc-27876) over a real MariaDB: the Shortcut dry run writes nothing; the approved
/// import brings in every non-archived epic and story with the dry run's counts, through the work
/// store (feed entries attributed to import:shortcut, original authors and times kept); get_work
/// resolves sc-NNNN; a re-run changes nothing; a delta sync after a change in Shortcut updates that
/// item without duplicating anything. Then the KanbanBoard epic and the native-memory candidates.
/// </summary>
public sealed class WorkImportMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private sealed class Sources(IShortcutSource? shortcut, IKanbanSource? kanban) : IWorkImportSourceFactory
    {
        public Task<IShortcutSource> ShortcutAsync(CancellationToken ct) => Task.FromResult(shortcut!);
        public Task<IKanbanSource> KanbanAsync(CancellationToken ct) => Task.FromResult(kanban!);
    }

    public static WorkImportService Service(WorkDatabase db, IShortcutSource? shortcut, IKanbanSource? kanban, string attachmentRoot)
    {
        var options = Options.Create(new WorkImportOptions { AttachmentStoragePath = attachmentRoot });
        var storage = new WorkAttachmentStorage(options, Options.Create(new WikiOptions()));
        return new WorkImportService(db.Store, new ShortcutImporter(db.Store, db.Store, storage, options), new KanbanImporter(db.Store, db.Store), new Sources(shortcut, kanban));
    }

    private async Task<long> CountAsync(string sql, object? parameters = null)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        return await connection.ExecuteScalarAsync<long>(sql, parameters);
    }

    private async Task<Dictionary<string, long>> TableCountsAsync()
    {
        var counts = new Dictionary<string, long>();
        foreach (var table in new[] { "work_projects", "work_features", "work_items", "work_item_comments", "work_item_links", "work_item_dependencies", "work_item_aliases", "work_changes", "work_import_records", "work_item_attachments" })
            counts[table] = await CountAsync($"SELECT COUNT(*) FROM {table}");
        return counts;
    }

    private Task<JsonElement> GetAsync(string id) => db.CallAsync(WorkTools.GetWork, new { id });

    private async Task CreateProjectAsync(string key, string repository) =>
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key, name = $"Project {key}", repositories = new[] { new { repository, role = "owned" } } } });

    /// <summary>The recorded payloads plus the cases they lack, derived from them.</summary>
    private static RecordedShortcutSource Source()
    {
        var source = RecordedShortcutSource.FromDirectory(WorkImportMappingTests.ShortcutFixtures);
        var template = source.Stories[27876] with { StoryLinks = [], Blocked = false, EpicId = null, Comments = [] };
        const string sceneWorks = "69f08f42-4c78-4015-8da6-0abb1fe0825f";
        // No team and no epic: the holding project.
        source.Stories[90001] = template with { Id = 90001, Name = "Untriaged bug", StoryType = "bug", WorkflowStateId = 500000007, TeamId = null };
        source.Stories[90002] = template with { Id = 90002, Name = "Archived", Archived = true };
        // A parent with a sub-task, in review, with a comment by the Codex account.
        source.Stories[90003] = template with
        {
            Id = 90003, Name = "Parent", TeamId = sceneWorks, WorkflowStateId = 500000009, SubTaskIds = [90004],
            Comments = [new ShortcutComment(91001, "69f0ba01-0687-4592-ab25-6d74829d63d9", "Review notes.", new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc))]
        };
        source.Stories[90004] = template with { Id = 90004, Name = "Sub-task", TeamId = sceneWorks, WorkflowStateId = 500000006, ParentStoryId = 90003 };
        // Blocked in Shortcut with nothing blocking it (D26).
        source.Stories[90005] = template with { Id = 90005, Name = "Blocked without blockers", TeamId = sceneWorks, Blocked = true };
        source.Epics[90100] = source.Epics[5558] with { Id = 90100, Name = "Archived epic", Archived = true };
        return source;
    }

    [Fact]
    public async Task Shortcut_dry_run_import_rerun_and_delta_sync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        string Key(string prefix) => prefix + suffix;
        await CreateProjectAsync(Key("CG"), "codegraph");
        await CreateProjectAsync(Key("SW"), "sceneworks");
        var source = Source();
        var root = Path.Combine(Path.GetTempPath(), "cg-import-" + suffix);
        var service = Service(db, source, null, root);
        var teams = new List<ImportTeamMapping>
        {
            new("69f090da-4d9c-47cb-902d-d49fa77af7da", Key("CG"), "CodeGraph"),
            new("6ac526c4-f2b5-4903-958f-d2d529d8bc8a", Key("DF"), "DarkFactory", "Dark Factory"),
            new("69f08f42-4c78-4015-8da6-0abb1fe0825f", Key("SW"), "SceneWorks")
        };

        // The proposal maps every team and holds work without a team.
        var proposal = await service.GetSettingsAsync(ImportSources.Shortcut, CancellationToken.None);
        proposal.Teams.Count.ShouldBe(6);
        proposal.Teams.Single(team => team.TeamKey == ShortcutImporter.NoTeam).ProjectKey.ShouldBe(ShortcutImporter.DefaultHoldingProject);
        proposal.Members!.Count(member => member.Kind == WorkActor.AgentKind).ShouldBe(2);

        await service.SaveSettingsAsync(ImportSources.Shortcut, new ImportSettings(Key("HO"), teams), approve: false, "owner", CancellationToken.None);
        var before = await TableCountsAsync();

        // ---- Dry run: the mapping report, nothing written ------------------------------------
        var dry = await service.RunAsync(ImportSources.Shortcut, "dry_run", null, "owner", CancellationToken.None);
        dry.Status.ShouldBe(ImportRunStatuses.Completed, dry.Error);
        (await TableCountsAsync()).ShouldBe(before);
        var report = dry.Report!;
        report.SettingsApproved.ShouldBeFalse();
        report.Stories.Mapped.ShouldBe(8);
        report.Stories.ArchivedSkipped.ShouldBe(1);
        report.Epics.Mapped.ShouldBe(4);
        report.Epics.ArchivedSkipped.ShouldBe(1);
        report.Projects[Key("CG")].Items.ShouldBe(2);
        report.Projects[Key("SW")].Items.ShouldBe(5);
        report.Projects[Key("HO")].Items.ShouldBe(1);
        report.Projects[Key("DF")].Features.ShouldBe(1);
        report.Projects[Key("SW")].ByStatus["complete"].ShouldBe(2);
        report.Teams.Single(team => team.TeamName == "LensWorks").Mapping.ShouldBe("holding");
        report.Teams.Single(team => team.TeamName == "CodeGraph").Mapping.ShouldBe("proposed");
        report.NoRepository.ShouldBe(["sc-90001"]);
        report.Attachments.Files.ShouldBe(1);
        report.Attachments.TotalBytes.ShouldBe(1735535);
        report.ReconcileTasks.ShouldBe(3);
        report.BlockedWithoutEdges.ShouldBe(1);
        report.AcceptanceCriteria.ShouldBe(5);
        report.UnmappedRelations.ShouldContain(new ImportRelationIssue("sc-27876", "blocked_by", "sc-27869", "not_imported"));
        report.UnmappedRelations.ShouldContain(new ImportRelationIssue("sc-10354", "relates to", "sc-10355", "not_imported"));
        report.UnmappedRelations.ShouldNotContain(issue => issue.To == "sc-27866");
        (await service.GetSettingsAsync(ImportSources.Shortcut, CancellationToken.None)).ApprovedAt.ShouldBeNull();

        // ---- Import refused until the table is approved --------------------------------------
        (await Should.ThrowAsync<WorkException>(() => service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None)))
            .Message.ShouldContain("not approved");
        await service.SaveSettingsAsync(ImportSources.Shortcut, new ImportSettings(Key("HO"), teams), approve: true, "owner", CancellationToken.None);

        // ---- The approved import: the dry run's counts ---------------------------------------
        var imported = await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None);
        imported.Status.ShouldBe(ImportRunStatuses.Completed, imported.Error);
        var written = imported.Report!.Written;
        JsonSerializer.Serialize(imported.Report.Projects, WorkJson.Options).ShouldBe(JsonSerializer.Serialize(report.Projects, WorkJson.Options));
        written.Failures.ShouldBe(0, string.Join("; ", imported.Report.Warnings));
        written.ItemsCreated.ShouldBe(report.Stories.Mapped);
        written.FeaturesCreated.ShouldBe(report.Epics.Mapped);
        written.ReconcileTasksCreated.ShouldBe(report.ReconcileTasks);
        written.ProjectsCreated.ShouldBe(2); // DF and the holding project; LensWorks and ShirtPost are held, not created.
        written.DependenciesAdded.ShouldBe(2); // 27876 by 27866; the parent 90003 by its sub-task 90004
        written.AttachmentsStored.ShouldBe(1);
        (await CountAsync("SELECT COUNT(*) FROM work_items") - before["work_items"]).ShouldBe(report.Stories.Mapped + report.ReconcileTasks);

        // get_work resolves sc-NNNN; the item keeps the source's text, times, authors and links.
        var s2 = await GetAsync("sc-27866");
        s2.GetProperty("key").GetString().ShouldStartWith(Key("CG") + "-");
        s2.GetProperty("status").GetString().ShouldBe("complete");
        s2.GetProperty("type").GetString().ShouldBe("story");
        s2.GetProperty("repository").GetString().ShouldBe("codegraph");
        s2.GetProperty("created_at").GetDateTime().ShouldBe(new DateTime(2026, 10, 29, 13, 21, 28, DateTimeKind.Utc));
        s2.GetProperty("created_by").GetString().ShouldBe("import:shortcut");
        s2.GetProperty("imported").GetProperty("source").GetString().ShouldBe("shortcut");
        s2.GetProperty("imported").GetProperty("history").GetProperty("owners")[0].GetString().ShouldBe("michaeltrefry1818");
        s2.GetProperty("feature").GetProperty("key").GetString().ShouldStartWith(Key("CG") + "-F");
        var comment = s2.GetProperty("comments").EnumerateArray().First();
        comment.GetProperty("author").GetString().ShouldBe("claude");
        comment.GetProperty("author_kind").GetString().ShouldBe("agent");
        comment.GetProperty("created_at").GetDateTime().ShouldBe(new DateTime(2026, 10, 29, 16, 9, 37, DateTimeKind.Utc));
        s2.GetProperty("links").EnumerateArray().Select(link => link.GetProperty("kind").GetString()).ShouldBe(["pr", "branch"], ignoreOrder: true);

        var s12 = await GetAsync("sc-27876");
        s12.GetProperty("blocked").GetBoolean().ShouldBeFalse(); // its imported blocker is complete
        s12.GetProperty("blocked_by").EnumerateArray().Single().GetProperty("key").GetString().ShouldBe(s2.GetProperty("key").GetString());
        s12.GetProperty("status").GetString().ShouldBe("in_progress");

        var parent = await GetAsync("sc-90003");
        parent.GetProperty("blocked").GetBoolean().ShouldBeTrue();
        parent.GetProperty("blocked_by").EnumerateArray().Single().GetProperty("key").GetString().ShouldBe((await GetAsync("sc-90004")).GetProperty("key").GetString());
        parent.GetProperty("status").GetString().ShouldBe("in_review");
        parent.GetProperty("comments")[0].GetProperty("author").GetString().ShouldBe("codex");

        var offline = await GetAsync("sc-10354");
        offline.GetProperty("acceptance_criteria").EnumerateArray().Select(criterion => criterion.GetProperty("state").GetString()).ShouldBe(Enumerable.Repeat("met", 5));
        offline.GetProperty("comments").EnumerateArray().Count(note => note.GetProperty("body").GetString()!.StartsWith("Relates to sc-")).ShouldBe(2);

        var held = await GetAsync("sc-90005");
        held.GetProperty("status").GetString().ShouldBe("backlog");
        held.GetProperty("comments").EnumerateArray().ShouldContain(note => note.GetProperty("body").GetString()!.Contains("D26"));
        (await GetAsync("sc-90001")).GetProperty("project_key").GetString().ShouldBe(Key("HO"));
        (await GetAsync("sc-90001")).GetProperty("status").GetString().ShouldBe("ready");
        (await db.RefusedAsync(WorkTools.GetWork, new { id = "sc-90002" })).Code.ShouldBe(WorkErrorCodes.NotFound);

        var pid = await GetAsync("sc-7931");
        var attachment = pid.GetProperty("attachments").EnumerateArray().Single();
        attachment.GetProperty("stored").GetBoolean().ShouldBeTrue();
        var stored = await db.Store.GetAttachmentAsync(attachment.GetProperty("id").GetGuid());
        using (var reader = new StreamReader(new WorkAttachmentStorage(Options.Create(new WorkImportOptions { AttachmentStoragePath = root }), Options.Create(new WikiOptions())).OpenRead(stored!.StoragePath!)))
            (await reader.ReadToEndAsync()).ShouldStartWith("placeholder bytes");

        // Open epics are unreconciled with a reconcile task; a done epic is neither.
        var gate = await GetAsync((await GetAsync("sc-27876")).GetProperty("feature").GetProperty("key").GetString()!);
        gate.GetProperty("requirements_unreconciled").GetBoolean().ShouldBeTrue();
        gate.GetProperty("imported").GetProperty("url").GetString().ShouldBe("https://app.shortcut.com/trefry/epic/27864");
        var done = await GetAsync(offline.GetProperty("feature").GetProperty("key").GetString()!);
        done.TryGetProperty("requirements_unreconciled", out _).ShouldBeFalse();
        var darkFactory = await db.CallAsync(WorkTools.FindWork, new { project = Key("DF"), label = "import-reconcile" });
        darkFactory.GetProperty("items").GetArrayLength().ShouldBe(1);
        var epicDocuments = await db.CallAsync(WorkTools.FindWork, new { project = Key("DF"), kind = "feature", detail = "full" });
        var feature = epicDocuments.GetProperty("items")[0];
        feature.GetProperty("description").GetString().ShouldContain("**E1 Placement.**");
        feature.GetProperty("documents").EnumerateArray().Single().GetProperty("title").GetString().ShouldBe("Shortcut epic comments");

        // Every write is in the feed, attributed to the import (E3, E7).
        var changes = await db.AllChangesAsync(Key("CG"));
        changes.Where(change => change.GetProperty("entity").GetString() != "project")
            .ShouldAllBe(change => change.GetProperty("actor").GetProperty("name").GetString() == "import:shortcut" && change.GetProperty("actor").GetProperty("kind").GetString() == "system");
        changes.Count(change => change.GetProperty("kind").GetString() == "item_created").ShouldBe(3); // two stories and the reconcile task

        // ---- A re-run changes nothing --------------------------------------------------------
        var afterImport = await TableCountsAsync();
        var rerun = await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None);
        rerun.Report!.Written.ItemsUnchanged.ShouldBe(report.Stories.Mapped);
        rerun.Report.Written.ItemsCreated.ShouldBe(0);
        rerun.Report.Written.FeaturesUnchanged.ShouldBe(report.Epics.Mapped);
        (await TableCountsAsync()).ShouldBe(afterImport);

        // ---- A change in Shortcut, then a delta sync: that item is updated, nothing duplicated -
        var version = s2.GetProperty("version").GetInt64();
        var later = DateTime.UtcNow.AddMinutes(5);
        source.Stories[27866] = source.Stories[27866] with
        {
            Name = "S2 — Sessions, briefing and cg CLI (renamed in Shortcut)",
            UpdatedAt = later,
            Comments = [.. source.Stories[27866].Comments, new ShortcutComment(27999, "69f08f42-a951-46b3-b1a0-897a12271e62", "A follow-up note.", later)]
        };
        var delta = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        delta.Status.ShouldBe(ImportRunStatuses.Completed, delta.Error);
        delta.UpdatedSince.ShouldNotBeNull();
        delta.Report!.Stories.Seen.ShouldBe(1);
        delta.Report.Written.ItemsUpdated.ShouldBe(1, JsonSerializer.Serialize(delta.Report, WorkJson.Options));
        var renamed = await GetAsync("sc-27866");
        renamed.GetProperty("id").GetString().ShouldBe(s2.GetProperty("id").GetString());
        renamed.GetProperty("title").GetString().ShouldBe("S2 — Sessions, briefing and cg CLI (renamed in Shortcut)");
        renamed.GetProperty("version").GetInt64().ShouldBeGreaterThan(version);
        renamed.GetProperty("comments").GetArrayLength().ShouldBe(s2.GetProperty("comments").GetArrayLength() + 1);
        var afterDelta = await TableCountsAsync();
        afterDelta["work_items"].ShouldBe(afterImport["work_items"]);
        afterDelta["work_item_aliases"].ShouldBe(afterImport["work_item_aliases"]);
        afterDelta["work_item_comments"].ShouldBe(afterImport["work_item_comments"] + 1);
        afterDelta["work_changes"].ShouldBe(afterImport["work_changes"] + 1);
        (await db.AllChangesAsync(Key("CG"))).Last().GetProperty("kind").GetString().ShouldBe("item_updated");

        // The runs are recorded with their reports.
        var runs = await db.Store.ListImportRunsAsync(ImportSources.Shortcut, 10);
        runs.Select(run => run.Mode).ShouldBe([ImportMode.Import, ImportMode.Import, ImportMode.Import, ImportMode.DryRun]);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Kanban_code_review_epic_imports_with_comments_links_and_dependencies()
    {
        var key = "KB" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await CreateProjectAsync(key, "inference");
        var source = RecordedKanbanSource.FromDirectory(WorkImportMappingTests.KanbanFixtures);
        var items = source.Items[KanbanImporter.CodeReviewEpicId];
        var first = items[0];
        items.Add(first with
        {
            Id = "6f0c1d2e-0000-4000-8000-000000000002", Title = "[F-002] MCP wildcard bind", Type = "Task", Status = "Ready", Priority = "Medium",
            Blockers = [first.Id], Links = [new KanbanLink("pr", "https://github.com/SceneWorks/SceneWorks/pull/3220")],
            Comments = [new KanbanComment("c1", "claude", "Fix with F-001.", new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc))]
        });
        var service = Service(db, null, source, Path.Combine(Path.GetTempPath(), "cg-kb-" + key));

        var dry = await service.RunAsync(ImportSources.Kanban, "dry_run", null, "owner", CancellationToken.None);
        dry.Report!.Stories.Mapped.ShouldBe(2);
        await service.SaveSettingsAsync(ImportSources.Kanban, new ImportSettings(key, [], KanbanEpicId: KanbanImporter.CodeReviewEpicId), approve: true, "owner", CancellationToken.None);
        var run = await service.RunAsync(ImportSources.Kanban, "import", null, "owner", CancellationToken.None);
        run.Status.ShouldBe(ImportRunStatuses.Completed, run.Error);
        run.Report!.Written.ItemsCreated.ShouldBe(2);
        run.Report.Written.DependenciesAdded.ShouldBe(1);

        var found = await db.CallAsync(WorkTools.FindWork, new { project = key, text = "wildcard", detail = "full" });
        var second = found.GetProperty("items")[0];
        second.GetProperty("type").GetString().ShouldBe("task");
        second.GetProperty("status").GetString().ShouldBe("ready");
        second.GetProperty("blocked").GetBoolean().ShouldBeTrue();
        second.GetProperty("repository").GetString().ShouldBe("inference");
        second.GetProperty("comments")[0].GetProperty("author_kind").GetString().ShouldBe("agent");
        second.GetProperty("links")[0].GetProperty("kind").GetString().ShouldBe("pr");
        var bug = (await db.CallAsync(WorkTools.FindWork, new { project = key, text = "F-001", detail = "full" })).GetProperty("items")[0];
        bug.GetProperty("type").GetString().ShouldBe("bug");
        bug.GetProperty("priority").GetString().ShouldBe("high");
        bug.GetProperty("labels").GetArrayLength().ShouldBe(3);
        bug.TryGetProperty("claim", out _).ShouldBeFalse();
        var feature = await GetAsync(bug.GetProperty("feature").GetProperty("key").GetString()!);
        feature.GetProperty("title").GetString().ShouldBe("SceneWorks Code Review 2026-10-27");
        feature.GetProperty("documents").GetArrayLength().ShouldBe(2);

        var again = await service.RunAsync(ImportSources.Kanban, "import", null, "owner", CancellationToken.None);
        again.Report!.Written.ItemsUnchanged.ShouldBe(2);
        again.Report.Written.ReconcileTasksCreated.ShouldBe(0);
    }

    [Fact]
    public async Task Memory_candidates_go_through_the_import_knowledge_dry_run_and_store_nothing()
    {
        var key = await db.NewProjectAsync("MEM", "mlx-rs");
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var files = new[]
        {
            new MemoryFile("/memory/commands.md", "---\nname: Build\ndescription: Build the solution with dotnet build CodeGraph.sln\ntype: reference\n---\nRun `dotnet build CodeGraph.sln`; migrations are in sql/migrations."),
            new MemoryFile("/memory/flow.md", "---\nname: flow\ndescription: Prefers continuous work\ntype: feedback\n---\nKeep going until the epic is done.")
        };
        var candidates = NativeMemoryParser.ParseClaude(files, new RepositoryCheckout(repositoryRoot));
        candidates.Select(candidate => candidate.Candidate.Verification).ShouldBe([ImportVerification.Verified, ImportVerification.Personal]);
        var before = await CountAsync("SELECT COUNT(*) FROM knowledge_entries");

        var preview = await db.CallAsync(WorkTools.ImportKnowledge, new { project = key, candidates = candidates.Select(candidate => candidate.Candidate).ToList() });

        preview.GetProperty("stored").GetBoolean().ShouldBeFalse();
        preview.GetProperty("candidates").EnumerateArray().Select(candidate => candidate.GetProperty("outcome").GetString()).ShouldBe(["would_store", "personal"]);
        (await CountAsync("SELECT COUNT(*) FROM knowledge_entries")).ShouldBe(before);
    }
}
