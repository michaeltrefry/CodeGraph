using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using CodeGraph.Services.Work.Imports;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The staging validation of sc-27876, run on demand (they are skipped unless their variable is set):
/// a full dry run and import at the workspace's scale (~400 epics, ~7,300 stories synthesised from
/// the recorded payloads) against a local MariaDB, with count reconciliation, a re-run and a delta
/// sync; and the native-memory candidate report for a memory directory (dry run only, nothing stored).
/// Reports are written to CODEGRAPH_IMPORT_REPORT_DIR.
/// </summary>
public sealed class WorkImportStagingRunTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static string? ReportDirectory => Environment.GetEnvironmentVariable("CODEGRAPH_IMPORT_REPORT_DIR");

    private static void Write(string name, object value)
    {
        if (ReportDirectory is not { } directory)
            return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, value.GetType(), new JsonSerializerOptions(WorkJson.Options) { WriteIndented = true }));
    }

    private static readonly string[] OpenStates = ["Backlog", "To Do", "In Progress", "In Review"];

    /// <summary>~400 epics and ~7,300 stories with the workspace's open/done split, built from the recorded payloads.</summary>
    public static RecordedShortcutSource Synthesise(int epics = 399, int stories = 7300, int openEpics = 156, int openStories = 1523)
    {
        var recorded = RecordedShortcutSource.FromDirectory(WorkImportMappingTests.ShortcutFixtures);
        var source = new RecordedShortcutSource();
        source.Teams.AddRange(recorded.Teams);
        source.Members.AddRange(recorded.Members);
        source.States.AddRange(recorded.States);
        var stateIds = recorded.States.ToDictionary(state => state.Name, state => state.Id);
        var teams = recorded.Teams.Take(4).Select(team => team.Id).ToArray();
        var epicTemplates = recorded.Epics.Values.OrderBy(epic => epic.Id).ToArray();
        for (var index = 0; index < epics + 10; index++)
        {
            var template = epicTemplates[index % epicTemplates.Length];
            var open = index < openEpics;
            var id = 200_000 + index;
            source.Epics[id] = template with
            {
                Id = id, Name = $"{template.Name} #{index}", Archived = index >= epics, Done = !open, State = open ? "in progress" : "done",
                TeamIds = [teams[index % teams.Length]], AppUrl = $"https://app.shortcut.com/trefry/epic/{id}",
                UpdatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index)
            };
        }

        var storyTemplates = recorded.Stories.Values.OrderBy(story => story.Id).ToArray();
        var lastInEpic = new Dictionary<long, long>();
        for (var index = 0; index < stories + 50; index++)
        {
            var template = storyTemplates[index % storyTemplates.Length];
            var id = 100_000L + index;
            var archived = index >= stories;
            // About one story in fifty has neither team nor epic: the holding project.
            var orphan = index % 50 == 49;
            long? epicId = orphan ? null : 200_000 + index % epics;
            var open = index < openStories;
            var state = open ? OpenStates[index % OpenStates.Length] : "Done";
            var links = new List<ShortcutStoryLink>();
            // Every seventh story is blocked by the previous story of its epic.
            if (epicId is { } epic && index % 7 == 0 && lastInEpic.TryGetValue(epic, out var previous))
                links.Add(new ShortcutStoryLink(900_000 + index, previous, id, "blocks"));
            if (index % 11 == 0)
                links.Add(new ShortcutStoryLink(950_000 + index, id, id - 1, "relates to"));
            var files = template.Files.Select(file => file with { Id = 800_000 + index, Url = $"https://media.app.shortcut.com/api/attachments/files/synthetic/{id}.png", Size = 1024 + index }).ToList();
            foreach (var file in files)
                source.Files[file.Url] = Encoding.UTF8.GetBytes($"synthetic file {id}");
            source.Stories[id] = template with
            {
                Id = id, Name = $"{template.Name} #{index}", Archived = archived, EpicId = epicId,
                TeamId = orphan ? null : source.Epics[epicId!.Value].TeamIds[0], WorkflowStateId = stateIds[state],
                StoryLinks = links, Blocked = false, Files = files, SubTaskIds = index % 100 == 0 && index + 1 < stories ? [id + 1] : [],
                Comments = template.Comments.Select(comment => comment with { Id = 700_000L * 10 + index * 10 + comment.Id % 10 }).ToList(),
                Tasks = template.Tasks.Select(task => task with { Id = 600_000L * 10 + index * 10 + task.Position }).ToList(),
                AppUrl = $"https://app.shortcut.com/trefry/story/{id}",
                UpdatedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index)
            };
            if (epicId is { } placed)
                lastInEpic[placed] = id;
        }

        return source;
    }

    [Fact]
    public async Task Full_dry_run_and_import_at_workspace_scale_reconcile()
    {
        if (Environment.GetEnvironmentVariable("CODEGRAPH_IMPORT_SCALE_RUN") != "1")
            return;

        var source = Synthesise();
        var root = Path.Combine(Path.GetTempPath(), "cg-import-scale-" + Guid.NewGuid().ToString("N")[..6]);
        var service = WorkImportMariaDbTests.Service(db, source, null, root);
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key = "SCENE", name = "SceneWorks", repositories = new[] { new { repository = "sceneworks", role = "owned" } } } });
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key = "CGRAPH", name = "CodeGraph", repositories = new[] { new { repository = "codegraph", role = "owned" } } } });
        var timings = new Dictionary<string, double>();
        var clock = Stopwatch.StartNew();

        // The owner's table as the dry run proposes it, with two keys changed to existing projects.
        var proposal = await service.GetSettingsAsync(ImportSources.Shortcut, CancellationToken.None);
        var teams = proposal.Teams.Where(team => team.TeamKey != ShortcutImporter.NoTeam).Select(team => team.TeamName switch
        {
            "SceneWorks" => team with { ProjectKey = "SCENE" },
            "CodeGraph" => team with { ProjectKey = "CGRAPH" },
            _ => team
        }).ToList();
        await service.SaveSettingsAsync(ImportSources.Shortcut, proposal with { Teams = teams }, approve: false, "owner", CancellationToken.None);

        var dry = await service.RunAsync(ImportSources.Shortcut, "dry_run", null, "owner", CancellationToken.None);
        timings["dry_run_seconds"] = clock.Elapsed.TotalSeconds;
        dry.Status.ShouldBe(ImportRunStatuses.Completed, dry.Error);
        Write("shortcut-dry-run.json", dry);
        (await CountAsync("SELECT COUNT(*) FROM work_items")).ShouldBe(0);

        await service.SaveSettingsAsync(ImportSources.Shortcut, proposal with { Teams = teams }, approve: true, "owner", CancellationToken.None);
        clock.Restart();
        var import = await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None);
        timings["import_seconds"] = clock.Elapsed.TotalSeconds;
        import.Status.ShouldBe(ImportRunStatuses.Completed, import.Error);
        Write("shortcut-import.json", import);

        // Count reconciliation: the import's counts are the dry run's, and the database holds them.
        var report = dry.Report!;
        var written = import.Report!.Written;
        JsonSerializer.Serialize(import.Report.Projects, WorkJson.Options).ShouldBe(JsonSerializer.Serialize(report.Projects, WorkJson.Options));
        written.Failures.ShouldBe(0, string.Join("; ", import.Report.Warnings.Take(5)));
        written.ItemsCreated.ShouldBe(report.Stories.Mapped);
        written.FeaturesCreated.ShouldBe(report.Epics.Mapped);
        var reconciliation = new Dictionary<string, object>
        {
            ["epics_non_archived"] = source.Epics.Values.Count(epic => !epic.Archived),
            ["stories_non_archived"] = source.Stories.Values.Count(story => !story.Archived),
            ["dry_run_epics_mapped"] = report.Epics.Mapped,
            ["dry_run_stories_mapped"] = report.Stories.Mapped,
            ["imported_features"] = await CountAsync("SELECT COUNT(*) FROM work_import_records WHERE source = 'shortcut' AND external_kind = 'epic'"),
            ["imported_items"] = await CountAsync("SELECT COUNT(*) FROM work_import_records WHERE source = 'shortcut' AND external_kind = 'story'"),
            ["sc_aliases"] = await CountAsync("SELECT COUNT(*) FROM work_item_aliases WHERE alias LIKE 'sc-%'"),
            ["work_items_rows"] = await CountAsync("SELECT COUNT(*) FROM work_items"),
            ["reconcile_tasks"] = await CountAsync("SELECT COUNT(*) FROM work_import_records WHERE source = 'shortcut' AND external_kind = 'reconcile'"),
            ["dependencies"] = await CountAsync("SELECT COUNT(*) FROM work_item_dependencies"),
            ["attachments_stored"] = await CountAsync("SELECT COUNT(*) FROM work_item_attachments WHERE storage_path IS NOT NULL"),
            ["feed_entries_by_import"] = await CountAsync("SELECT COUNT(*) FROM work_changes WHERE actor_name = 'import:shortcut'")
        };
        ((long)reconciliation["imported_items"]).ShouldBe(report.Stories.Mapped);
        ((long)reconciliation["imported_features"]).ShouldBe(report.Epics.Mapped);
        ((long)reconciliation["sc_aliases"]).ShouldBe(report.Stories.Mapped);
        ((int)reconciliation["stories_non_archived"]).ShouldBe(report.Stories.Mapped);
        ((int)reconciliation["epics_non_archived"]).ShouldBe(report.Epics.Mapped);
        ((long)reconciliation["work_items_rows"]).ShouldBe(report.Stories.Mapped + report.ReconcileTasks);
        (await db.CallAsync(WorkTools.GetWork, new { id = "sc-100777" })).GetProperty("title").GetString().ShouldEndWith("#777");

        // A re-run writes nothing; a delta sync after one change in Shortcut updates one item.
        clock.Restart();
        var rerun = await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None);
        timings["rerun_seconds"] = clock.Elapsed.TotalSeconds;
        rerun.Report!.Written.ItemsUnchanged.ShouldBe(report.Stories.Mapped);
        var feed = await CountAsync("SELECT COUNT(*) FROM work_changes");
        source.Stories[100777] = source.Stories[100777] with { Name = "Changed in Shortcut", UpdatedAt = DateTime.UtcNow.AddMinutes(5) };
        clock.Restart();
        var delta = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        timings["delta_seconds"] = clock.Elapsed.TotalSeconds;
        delta.Report!.Written.ItemsUpdated.ShouldBe(1);
        (await CountAsync("SELECT COUNT(*) FROM work_items")).ShouldBe((long)reconciliation["work_items_rows"]);
        (await CountAsync("SELECT COUNT(*) FROM work_changes")).ShouldBe(feed + 1);
        (await db.CallAsync(WorkTools.GetWork, new { id = "sc-100777" })).GetProperty("title").GetString().ShouldBe("Changed in Shortcut");
        Write("shortcut-reconciliation.json", new { reconciliation, timings, rerun = rerun.Report.Written, delta = new { delta.UpdatedSince, delta.Report.Stories, delta.Report.Written } });
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Native_memory_candidate_report_dry_run()
    {
        if (Environment.GetEnvironmentVariable("CODEGRAPH_MEMORY_DIR") is not { } memoryDirectory)
            return;

        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        // Read only: the files are read and never written.
        var files = Directory.EnumerateFiles(memoryDirectory, "*.md").Select(path => new MemoryFile(path, File.ReadAllText(path))).ToList();
        var candidates = NativeMemoryParser.ParseClaude(files, repositoryRoot).ToList();
        if (Environment.GetEnvironmentVariable("CODEGRAPH_CODEX_MEMORY") is { } codex && File.Exists(codex))
            candidates.AddRange(NativeMemoryParser.ParseCodex(new MemoryFile(codex, File.ReadAllText(codex)), repositoryRoot, "CodeGraph"));

        var key = await db.NewProjectAsync("MEMDEMO", "inference");
        var before = await CountAsync("SELECT COUNT(*) FROM knowledge_entries");
        var outcomes = new List<JsonElement>();
        foreach (var batch in candidates.Chunk(KnowledgeLimits.MaxImportCandidates))
        {
            var preview = await db.CallAsync(WorkTools.ImportKnowledge, new { project = key, candidates = batch.Select(candidate => candidate.Candidate).ToList() });
            preview.GetProperty("stored").GetBoolean().ShouldBeFalse();
            outcomes.AddRange(preview.GetProperty("candidates").EnumerateArray());
        }

        (await CountAsync("SELECT COUNT(*) FROM knowledge_entries")).ShouldBe(before);
        Write("memory-candidates.json", new
        {
            memory_directory = memoryDirectory,
            repository_root = repositoryRoot,
            counts = candidates.GroupBy(candidate => $"{candidate.Harness}:{JsonNamingPolicy.SnakeCaseLower.ConvertName(candidate.Candidate.Verification.ToString())}")
                .ToDictionary(group => group.Key, group => group.Count()),
            candidates = candidates.Select((candidate, index) => new
            {
                index,
                candidate.Harness,
                memory_type = candidate.MemoryType,
                source_file = candidate.Candidate.SourceFile,
                kind = candidate.Candidate.Entry.Kind,
                summary = candidate.Candidate.Entry.Summary,
                pre_check = candidate.Candidate.Verification,
                note = candidate.Candidate.VerificationNote,
                candidate.Referenced,
                candidate.Missing,
                s5_dry_run = index < outcomes.Count ? outcomes[index] : default
            }).ToList()
        });
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        return await connection.ExecuteScalarAsync<long>(sql);
    }
}
