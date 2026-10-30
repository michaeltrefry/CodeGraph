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

/// <summary>Recorded Shortcut payloads cut down to what a test needs, shared by the importer fix tests (sc-27928..sc-27931).</summary>
internal static class ImportFixtures
{
    public const string CodeGraphTeam = "69f090da-4d9c-47cb-902d-d49fa77af7da";
    public const string DarkFactoryTeam = "6ac526c4-f2b5-4903-958f-d2d529d8bc8a";
    public const string SceneWorksTeam = "69f08f42-4c78-4015-8da6-0abb1fe0825f";

    public static readonly RecordedShortcutSource Recorded = RecordedShortcutSource.FromDirectory(WorkImportMappingTests.ShortcutFixtures);

    /// <summary>The workspace's teams, members and states, with no epics or stories.</summary>
    public static RecordedShortcutSource Empty()
    {
        var source = new RecordedShortcutSource();
        source.Teams.AddRange(Recorded.Teams);
        source.Members.AddRange(Recorded.Members);
        source.States.AddRange(Recorded.States);
        return source;
    }

    /// <summary>An open story with nothing linked to it.</summary>
    public static ShortcutStory Story(long id, string name, string? team, long? epic = null) =>
        Recorded.Stories[27876] with
        {
            Id = id, Name = name, TeamId = team, EpicId = epic, Description = null, StoryLinks = [], Blocked = false, Comments = [], PullRequests = [],
            Branches = [], Commits = [], ExternalLinks = [], Files = [], LinkedFiles = [], Tasks = [], SubTaskIds = [], ParentStoryId = null, Archived = false
        };

    public static long DoneState => Recorded.Stories[27866].WorkflowStateId;

    public static async Task<long> CountAsync(WorkDatabase db, string sql, object? parameters = null)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        return await connection.ExecuteScalarAsync<long>(sql, parameters);
    }
}

/// <summary>
/// sc-27928: the dry run proposes, per team, the indexed repositories its stories name; the table's repositories
/// are checked when saved (indexed, one project each, never another project's); the approved import makes them
/// owned repositories of their projects before it writes any item, so a story that names one gets it.
/// </summary>
public sealed class WorkImportRepositoryMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private Task<JsonElement> GetAsync(string id) => db.CallAsync(WorkTools.GetWork, new { id });

    [Fact]
    public async Task The_dry_run_proposes_repositories_per_team_and_the_approved_import_attaches_them_before_writing_items()
    {
        var suffix = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        string Key(string prefix) => prefix + suffix;
        // OT owns inference: never proposed elsewhere, never attached elsewhere.
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key = Key("OT"), name = "Other", repositories = new[] { new { repository = "inference", role = "owned" } } } });
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key = Key("SW"), name = "SceneWorks project" } });

        var source = ImportFixtures.Empty();
        source.Stories[95001] = ImportFixtures.Story(95001, "PR in CodeGraph", ImportFixtures.CodeGraphTeam) with
        {
            PullRequests = [new ShortcutPullRequest(1, "https://github.com/michaeltrefry/CodeGraph/pull/1", "x", true, false)]
        };
        source.Stories[95002] = ImportFixtures.Story(95002, "Branch in SceneWorks", ImportFixtures.SceneWorksTeam) with
        {
            Branches = [new ShortcutBranch("b", "https://github.com/SceneWorks/SceneWorks/tree/b")]
        };
        source.Stories[95003] = ImportFixtures.Story(95003, "Named in the text", ImportFixtures.SceneWorksTeam) with
        {
            Description = "The fix is in https://github.com/SceneWorks/mlx-rs/blob/main/src/lib.rs."
        };
        source.Stories[95004] = ImportFixtures.Story(95004, "PR in a repository another project owns", ImportFixtures.SceneWorksTeam) with
        {
            PullRequests = [new ShortcutPullRequest(2, "https://github.com/SceneWorks/inference/pull/2", "y", false, false)]
        };
        source.Stories[95005] = ImportFixtures.Story(95005, "An unindexed repository", ImportFixtures.SceneWorksTeam) with
        {
            Description = "See https://github.com/acme/not-indexed for the upstream."
        };
        var service = WorkImportMariaDbTests.Service(db, source, null, Path.Combine(Path.GetTempPath(), "cg-import-repo-" + suffix));
        List<ImportTeamMapping> Table(string[]? codegraph, string[]? sceneworks) =>
        [
            new(ImportFixtures.CodeGraphTeam, Key("CG"), "CodeGraph", Repositories: codegraph),
            new(ImportFixtures.SceneWorksTeam, Key("SW"), "SceneWorks", Repositories: sceneworks)
        ];
        await service.SaveSettingsAsync(ImportSources.Shortcut, new ImportSettings(Key("HO"), Table(null, null)), approve: false, "owner", CancellationToken.None);

        // ---- The dry run proposes repositories per team ----------------------------------------
        var first = (await service.RunAsync(ImportSources.Shortcut, "dry_run", null, "owner", CancellationToken.None)).Report!;
        ImportTeamRow Row(ImportReport report, string team) => report.Teams.Single(row => row.TeamKey == team);
        Row(first, ImportFixtures.CodeGraphTeam).ProposedRepositories.ShouldBe(["codegraph"]);
        Row(first, ImportFixtures.SceneWorksTeam).ProposedRepositories.ShouldBe(["mlx-rs", "sceneworks"]);
        Row(first, ImportFixtures.SceneWorksTeam).Repositories.ShouldBeNull();
        first.RepositoryMentions.ShouldContain(new ImportRepositoryMention("michaeltrefry/CodeGraph", 1, "codegraph", null, Key("CG")));
        first.RepositoryMentions.ShouldContain(new ImportRepositoryMention("SceneWorks/inference", 1, "inference", Key("OT"), null));
        first.RepositoryMentions.ShouldContain(new ImportRepositoryMention("acme/not-indexed", 1, null, null, null));
        // Nothing is attached yet: the dry run reports against the table, which has no repositories.
        first.NoRepositoryCount.ShouldBe(5);

        // ---- The table's repositories are checked when saved ------------------------------------
        var unknown = await Should.ThrowAsync<WorkException>(() => service.SaveSettingsAsync(ImportSources.Shortcut,
            new ImportSettings(Key("HO"), Table(null, ["not-indexed"])), approve: false, "owner", CancellationToken.None));
        unknown.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        unknown.Message.ShouldContain("'not-indexed'");
        unknown.Message.ShouldContain("Indexed repositories: codegraph, inference, mlx-rs, sceneworks");
        var owned = await Should.ThrowAsync<WorkException>(() => service.SaveSettingsAsync(ImportSources.Shortcut,
            new ImportSettings(Key("HO"), Table(null, ["inference"])), approve: false, "owner", CancellationToken.None));
        owned.Code.ShouldBe(WorkErrorCodes.Conflict);
        owned.Message.ShouldContain($"inference is owned by {Key("OT")}");
        var twice = await Should.ThrowAsync<WorkException>(() => service.SaveSettingsAsync(ImportSources.Shortcut,
            new ImportSettings(Key("HO"), Table(["codegraph"], ["CodeGraph"])), approve: false, "owner", CancellationToken.None));
        twice.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await service.GetSettingsAsync(ImportSources.Shortcut, CancellationToken.None)).Teams.ShouldAllBe(row => row.Repositories == null);

        // The owner takes the proposals, edited (spelling is CodeGraph's), and saves them.
        var saved = await service.SaveSettingsAsync(ImportSources.Shortcut, new ImportSettings(Key("HO"), Table(["codegraph"], ["SCENEWORKS", "mlx-rs"])),
            approve: false, "owner", CancellationToken.None);
        saved.Teams.Single(row => row.TeamKey == ImportFixtures.SceneWorksTeam).Repositories.ShouldBe(["sceneworks", "mlx-rs"]);

        // ---- The dry run reports against them ---------------------------------------------------
        var dry = (await service.RunAsync(ImportSources.Shortcut, "dry_run", null, "owner", CancellationToken.None)).Report!;
        dry.Projects[Key("SW")].Repositories.ShouldBe(["sceneworks", "mlx-rs"]);
        dry.Projects[Key("SW")].Exists.ShouldBeTrue();
        dry.Projects[Key("CG")].Repositories.ShouldBe(["codegraph"]);
        dry.Projects[Key("CG")].Exists.ShouldBeFalse();
        Row(dry, ImportFixtures.SceneWorksTeam).Repositories.ShouldBe(["sceneworks", "mlx-rs"]);
        Row(dry, ImportFixtures.SceneWorksTeam).ProposedRepositories.ShouldBeEmpty();
        dry.NoRepository.ShouldBe(["sc-95004", "sc-95005"], ignoreOrder: true);
        (await GetAsync(Key("SW"))).GetProperty("repositories").GetArrayLength().ShouldBe(0); // a dry run writes nothing

        // ---- The approved import attaches them, then writes the items ----------------------------
        await service.SaveSettingsAsync(ImportSources.Shortcut, saved, approve: true, "owner", CancellationToken.None);
        var run = await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None);
        run.Status.ShouldBe(ImportRunStatuses.Completed, run.Error);
        var report = run.Report!;
        report.Written.Failures.ShouldBe(0, string.Join("; ", report.Warnings));
        report.Written.RepositoriesAttached.ShouldBe(3);
        report.NoRepositoryCount.ShouldBe(dry.NoRepositoryCount);
        JsonSerializer.Serialize(report.Projects, WorkJson.Options).ShouldBe(JsonSerializer.Serialize(dry.Projects, WorkJson.Options));

        (await GetAsync("sc-95001")).GetProperty("repository").GetString().ShouldBe("codegraph");
        (await GetAsync("sc-95002")).GetProperty("repository").GetString().ShouldBe("sceneworks");
        (await GetAsync("sc-95003")).GetProperty("repository").GetString().ShouldBe("mlx-rs");
        (await GetAsync("sc-95004")).TryGetProperty("repository", out _).ShouldBeFalse();
        (await GetAsync("sc-95005")).TryGetProperty("repository", out _).ShouldBeFalse();

        var sceneWorks = await GetAsync(Key("SW"));
        sceneWorks.GetProperty("repositories").EnumerateArray().Select(member => (member.GetProperty("repository").GetString(), member.GetProperty("role").GetString()))
            .ShouldBe([("sceneworks", "owned"), ("mlx-rs", "owned")]);
        (await GetAsync(Key("CG"))).GetProperty("repositories")[0].GetProperty("repository").GetString().ShouldBe("codegraph");
        (await GetAsync(Key("OT"))).GetProperty("repositories")[0].GetProperty("repository").GetString().ShouldBe("inference");

        // The attachment is in the feed, attributed to the import, before the project's first item (E7).
        var changes = await db.AllChangesAsync(Key("SW"));
        var attached = changes.FindIndex(change => change.GetProperty("kind").GetString() == WorkChangeKinds.ProjectUpdated);
        attached.ShouldBeGreaterThanOrEqualTo(0);
        changes[attached].GetProperty("actor").GetProperty("name").GetString().ShouldBe("import:shortcut");
        attached.ShouldBeLessThan(changes.FindIndex(change => change.GetProperty("kind").GetString() == WorkChangeKinds.ItemCreated));
    }
}

/// <summary>
/// sc-27929: a story or epic archived in Shortcut after it was imported reaches CodeGraph on the next delta sync
/// (and on a full re-run): the item is closed (a finished one stays finished) and the feature flagged, attributed to
/// import:shortcut. An archived story that was never imported is still skipped.
/// </summary>
public sealed class WorkImportArchivedMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private Task<JsonElement> GetAsync(string id) => db.CallAsync(WorkTools.GetWork, new { id });

    [Fact]
    public async Task Archiving_an_imported_story_or_epic_closes_or_flags_it_on_the_next_sync()
    {
        var key = "AR" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key, name = "Archive", repositories = new[] { new { repository = "codegraph", role = "owned" } } } });
        var source = ImportFixtures.Empty();
        source.Epics[27864] = ImportFixtures.Recorded.Epics[27864] with { TeamIds = [ImportFixtures.CodeGraphTeam] };
        source.Stories[96001] = ImportFixtures.Story(96001, "Open, archived later", ImportFixtures.CodeGraphTeam, 27864);
        source.Stories[96002] = ImportFixtures.Story(96002, "Done, archived later", ImportFixtures.CodeGraphTeam, 27864) with { WorkflowStateId = ImportFixtures.DoneState };
        source.Stories[96003] = ImportFixtures.Story(96003, "Archived, caught by a full run", ImportFixtures.CodeGraphTeam, 27864);
        source.Stories[96004] = ImportFixtures.Story(96004, "Archived before any import", ImportFixtures.CodeGraphTeam) with { Archived = true };
        var service = WorkImportMariaDbTests.Service(db, source, null, Path.Combine(Path.GetTempPath(), "cg-import-arch-" + key));
        await service.SaveSettingsAsync(ImportSources.Shortcut, new ImportSettings(key + "H", [new ImportTeamMapping(ImportFixtures.CodeGraphTeam, key, "CodeGraph")]),
            approve: true, "owner", CancellationToken.None);

        var imported = await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None);
        imported.Report!.Written.ItemsCreated.ShouldBe(3);
        imported.Report.Stories.ArchivedSkipped.ShouldBe(1);
        (await GetAsync("sc-96001")).GetProperty("status").GetString().ShouldBe("in_progress");
        var feature = (await GetAsync("sc-96001")).GetProperty("feature").GetProperty("key").GetString()!;
        var reconcile = (await db.CallAsync(WorkTools.FindWork, new { project = key, label = "import-reconcile" })).GetProperty("items")[0].GetProperty("key").GetString()!;

        // ---- Shortcut archives two stories and the epic; a delta sync follows --------------------
        var later = DateTime.UtcNow.AddMinutes(5);
        source.Stories[96001] = source.Stories[96001] with { Archived = true, UpdatedAt = later };
        source.Stories[96002] = source.Stories[96002] with { Archived = true, UpdatedAt = later };
        source.Stories[96004] = source.Stories[96004] with { UpdatedAt = later };
        source.Epics[27864] = source.Epics[27864] with { Archived = true, UpdatedAt = later };
        var delta = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        delta.Status.ShouldBe(ImportRunStatuses.Completed, delta.Error);
        delta.Report!.Written.Failures.ShouldBe(0, string.Join("; ", delta.Report.Warnings));
        delta.Report.Stories.ArchivedClosed.ShouldBe(2);
        delta.Report.Stories.ArchivedSkipped.ShouldBe(1); // 96004: never imported, still skipped
        delta.Report.Epics.ArchivedClosed.ShouldBe(1);
        delta.Report.Written.ItemsUpdated.ShouldBe(2);
        (await db.RefusedAsync(WorkTools.GetWork, new { id = "sc-96004" })).Code.ShouldBe(WorkErrorCodes.NotFound);

        var closed = await GetAsync("sc-96001");
        closed.GetProperty("status").GetString().ShouldBe("closed");
        closed.GetProperty("imported").GetProperty("archived").GetBoolean().ShouldBeTrue();
        var note = closed.GetProperty("comments").EnumerateArray().Single(comment => comment.GetProperty("body").GetString()!.StartsWith("Archived in Shortcut"));
        note.GetProperty("author").GetString().ShouldBe("import:shortcut");
        note.GetProperty("body").GetString().ShouldBe("Archived in Shortcut: the import closed this item.");
        var done = await GetAsync("sc-96002");
        done.GetProperty("status").GetString().ShouldBe("complete");
        done.GetProperty("imported").GetProperty("archived").GetBoolean().ShouldBeTrue();

        // The feature is flagged, its reconcile task closed; every write is in the feed as import:shortcut (E3, E7).
        (await GetAsync(feature)).GetProperty("imported").GetProperty("archived").GetBoolean().ShouldBeTrue();
        (await GetAsync(reconcile)).GetProperty("status").GetString().ShouldBe("closed");
        var changes = (await db.AllChangesAsync(key)).Where(change => change.GetProperty("created_at").GetDateTime() >= delta.StartedAt).ToList();
        changes.ShouldAllBe(change => change.GetProperty("actor").GetProperty("name").GetString() == "import:shortcut");
        var itemChange = changes.Single(change => change.GetProperty("entity_key").GetString() == closed.GetProperty("key").GetString());
        itemChange.GetProperty("changes").EnumerateArray().ShouldContain(change => change.GetProperty("field").GetString() == "status" && change.GetProperty("new").GetString() == "closed");
        changes.Single(change => change.GetProperty("entity_key").GetString() == feature).GetProperty("changes").EnumerateArray()
            .ShouldContain(change => change.GetProperty("field").GetString() == "imported");
        changes.ShouldContain(change => change.GetProperty("entity_key").GetString() == reconcile);

        // ---- A second delta changes nothing --------------------------------------------------
        var again = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        again.Report!.Written.ItemsUpdated.ShouldBe(0);
        again.Report.Written.FeaturesUpdated.ShouldBe(0);

        // ---- A full run re-reads the imported stories it no longer lists -----------------------
        source.Stories[96003] = source.Stories[96003] with { Archived = true };
        var full = await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None);
        full.Report!.Written.Failures.ShouldBe(0, string.Join("; ", full.Report.Warnings));
        full.Report.Stories.ArchivedClosed.ShouldBe(3);
        full.Report.Stories.ArchivedSkipped.ShouldBe(1);
        (await GetAsync("sc-96003")).GetProperty("status").GetString().ShouldBe("closed");
        (await GetAsync("sc-96001")).GetProperty("status").GetString().ShouldBe("closed");
    }
}

/// <summary>
/// sc-27930: Shortcut's epics and stories share one number space, so sc-N of an imported epic resolves to its
/// feature, on every surface that names work (get_work and update_work over MCP, GET /api/work/{id} over REST);
/// sc-N of a story keeps resolving to its item.
/// </summary>
public sealed class WorkImportEpicAliasMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    [Fact]
    public async Task Sc_n_of_an_imported_epic_resolves_to_its_feature_and_of_a_story_to_its_item()
    {
        var key = "EA" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key, name = "Epic alias", repositories = new[] { new { repository = "codegraph", role = "owned" } } } });
        var source = ImportFixtures.Empty();
        source.Epics[27864] = ImportFixtures.Recorded.Epics[27864] with { TeamIds = [ImportFixtures.CodeGraphTeam] };
        source.Stories[27866] = ImportFixtures.Recorded.Stories[27866] with { StoryLinks = [], TeamId = ImportFixtures.CodeGraphTeam };
        var service = WorkImportMariaDbTests.Service(db, source, null, Path.Combine(Path.GetTempPath(), "cg-import-alias-" + key));
        await service.SaveSettingsAsync(ImportSources.Shortcut, new ImportSettings(key + "H", [new ImportTeamMapping(ImportFixtures.CodeGraphTeam, key, "CodeGraph")]),
            approve: true, "owner", CancellationToken.None);
        (await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None)).Status.ShouldBe(ImportRunStatuses.Completed);

        var story = await db.CallAsync(WorkTools.GetWork, new { id = "sc-27866" });
        story.GetProperty("key").GetString().ShouldStartWith(key + "-");
        var featureKey = story.GetProperty("feature").GetProperty("key").GetString()!;

        foreach (var name in new[] { "sc-27864", "SC-27864", "sc-027864" })
        {
            var epic = await db.CallAsync(WorkTools.GetWork, new { id = name });
            epic.GetProperty("key").GetString().ShouldBe(featureKey, name);
            epic.GetProperty("imported").GetProperty("external_id").GetString().ShouldBe("sc-epic-27864");
        }

        (await db.RefusedAsync(WorkTools.GetWork, new { id = "sc-99999999" })).Code.ShouldBe(WorkErrorCodes.NotFound);

        // update_work names the feature the same way.
        var updated = await db.UpdateAsync("sc-27864", new { goals = new[] { "Agents coordinate through CodeGraph" } });
        updated.GetProperty("key").GetString().ShouldBe(featureKey);

        // REST resolves it alike.
        await using var host = await WorkRestHost.StartAsync(db.Service);
        var token = await host.CreateTokenAsync("rest-user");
        var (status, body) = await host.SendAsync(HttpMethod.Get, "/api/work/sc-27864", token: token);
        status.ShouldBe(200);
        body.GetProperty("key").GetString().ShouldBe(featureKey);
        var (itemStatus, item) = await host.SendAsync(HttpMethod.Get, "/api/work/sc-27866", token: token);
        itemStatus.ShouldBe(200);
        item.GetProperty("key").GetString().ShouldBe(story.GetProperty("key").GetString());
    }
}

/// <summary>
/// sc-27931: the dry-run report says where each team's work actually goes (a story follows its epic's project), its
/// per-project counts are what the import creates, proposed keys never collide, and teams the table maps to one
/// project are flagged before approval.
/// </summary>
public sealed class WorkImportRoutingMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    [Fact]
    public async Task The_dry_run_reports_where_stories_land_and_its_counts_match_the_import()
    {
        var suffix = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        string Key(string prefix) => prefix + suffix;
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key = Key("CG"), name = "Routing" } });
        var source = ImportFixtures.Empty();
        source.Epics[27864] = ImportFixtures.Recorded.Epics[27864] with { TeamIds = [ImportFixtures.CodeGraphTeam] };
        // A DarkFactory story under the CodeGraph epic goes to the epic's project, not DarkFactory's.
        source.Stories[97001] = ImportFixtures.Story(97001, "DarkFactory story in a CodeGraph epic", ImportFixtures.DarkFactoryTeam, 27864);
        source.Stories[97002] = ImportFixtures.Story(97002, "DarkFactory story", ImportFixtures.DarkFactoryTeam);
        source.Stories[97003] = ImportFixtures.Story(97003, "No team", null);
        var lensWorks = ImportFixtures.Recorded.Teams.Single(team => team.Name == "LensWorks").Id;
        source.Stories[97004] = ImportFixtures.Story(97004, "LensWorks (not in the table)", lensWorks);
        source.Stories[97005] = ImportFixtures.Story(97005, "SceneWorks story", ImportFixtures.SceneWorksTeam);
        var service = WorkImportMariaDbTests.Service(db, source, null, Path.Combine(Path.GetTempPath(), "cg-import-route-" + suffix));
        var table = new ImportSettings(Key("HO"),
        [
            new ImportTeamMapping(ImportFixtures.CodeGraphTeam, Key("CG"), "CodeGraph"),
            new ImportTeamMapping(ImportFixtures.DarkFactoryTeam, Key("DF"), "DarkFactory"),
            // SceneWorks mapped to CodeGraph's key: flagged.
            new ImportTeamMapping(ImportFixtures.SceneWorksTeam, Key("CG"), "SceneWorks"),
            new ImportTeamMapping(ShortcutImporter.NoTeam, Key("HO"), "(no team)")
        ]);
        await service.SaveSettingsAsync(ImportSources.Shortcut, table, approve: false, "owner", CancellationToken.None);

        var dry = (await service.RunAsync(ImportSources.Shortcut, "dry_run", null, "owner", CancellationToken.None)).Report!;
        ImportTeamRow Row(string team) => dry.Teams.Single(row => row.TeamKey == team);
        var darkFactory = Row(ImportFixtures.DarkFactoryTeam);
        darkFactory.ProjectKey.ShouldBe(Key("DF"));
        darkFactory.Stories.ShouldBe(2);
        darkFactory.StoriesByProject.ShouldBe(new SortedDictionary<string, int>(StringComparer.Ordinal) { [Key("CG")] = 1, [Key("DF")] = 1 });
        Row(ImportFixtures.CodeGraphTeam).EpicsByProject.ShouldBe(new SortedDictionary<string, int>(StringComparer.Ordinal) { [Key("CG")] = 1 });
        Row(lensWorks).StoriesByProject.ShouldBe(new SortedDictionary<string, int>(StringComparer.Ordinal) { [Key("HO")] = 1 });

        // The per-project counts are the sum of where the teams' work goes.
        foreach (var (project, counts) in dry.Projects)
            counts.Items.ShouldBe(dry.Teams.Sum(row => row.StoriesByProject.GetValueOrDefault(project)), project);
        dry.Projects[Key("CG")].Items.ShouldBe(2);
        dry.Projects[Key("CG")].Exists.ShouldBeTrue();
        dry.Projects[Key("DF")].Items.ShouldBe(1);
        dry.Projects[Key("DF")].Exists.ShouldBeFalse();
        dry.Projects[Key("HO")].Items.ShouldBe(2);

        // Two teams on one project key are flagged before approval; the holding project is shared by design.
        Row(ImportFixtures.CodeGraphTeam).SharesProjectWith.ShouldBe(["SceneWorks"]);
        Row(ImportFixtures.SceneWorksTeam).SharesProjectWith.ShouldBe(["CodeGraph"]);
        Row(ImportFixtures.DarkFactoryTeam).SharesProjectWith.ShouldBeEmpty();
        Row(ShortcutImporter.NoTeam).SharesProjectWith.ShouldBeEmpty();
        dry.Warnings.ShouldContain($"Teams CodeGraph, SceneWorks all map to project {Key("CG")}. If they belong apart, give each its own project key before approving.");

        // ---- The import creates what the dry run counted, project by project ------------------
        await service.SaveSettingsAsync(ImportSources.Shortcut, table, approve: true, "owner", CancellationToken.None);
        var run = (await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None)).Report!;
        run.Written.Failures.ShouldBe(0, string.Join("; ", run.Warnings));
        JsonSerializer.Serialize(run.Projects, WorkJson.Options).ShouldBe(JsonSerializer.Serialize(dry.Projects, WorkJson.Options));
        run.Written.ProjectsCreated.ShouldBe(2); // DF and the holding project
        foreach (var (project, counts) in dry.Projects)
        {
            (await ImportFixtures.CountAsync(db, "SELECT COUNT(*) FROM work_import_records r JOIN work_projects p ON p.id = r.project_id WHERE r.external_kind = 'story' AND p.project_key = @project",
                new { project })).ShouldBe(counts.Items, project);
            (await ImportFixtures.CountAsync(db, "SELECT COUNT(*) FROM work_features f JOIN work_projects p ON p.id = f.project_id WHERE p.project_key = @project",
                new { project })).ShouldBe(counts.Features, project);
        }
    }

    [Fact]
    public async Task Proposed_keys_never_collide_with_each_other_or_an_existing_project()
    {
        var suffix = Guid.NewGuid().ToString("N")[..3].ToUpperInvariant();
        // Teams whose names cut to the same ten characters, and one cutting to an existing project's key.
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key = "LONGTEAMNA", name = "Existing " + suffix } });
        var source = ImportFixtures.Empty();
        source.Teams.Clear();
        source.Teams.Add(new ShortcutTeam("t1", "Long Team Name Alpha", null));
        source.Teams.Add(new ShortcutTeam("t2", "Long Team Name Beta", null));
        source.Teams.Add(new ShortcutTeam("t3", "Existing " + suffix, null));
        var options = Options.Create(new WorkImportOptions { AttachmentStoragePath = Path.Combine(Path.GetTempPath(), "cg-import-keys-" + suffix) });
        var importer = new ShortcutImporter(db.Store, db.Store, new WorkAttachmentStorage(options, Options.Create(new WikiOptions())), options);

        var proposal = await importer.ProposeAsync(source, CancellationToken.None);

        var keys = proposal.Teams.ToDictionary(row => row.TeamKey, row => row.ProjectKey);
        keys["t1"].ShouldBe("LONGTEAMN2"); // LONGTEAMNA is the existing project's
        keys["t2"].ShouldBe("LONGTEAMN3");
        keys["t3"].ShouldBe("LONGTEAMNA"); // named like the existing project: mapped to it
        proposal.Teams.Where(row => row.TeamKey != "t3").Select(row => row.ProjectKey).ShouldBeUnique();
    }
}
