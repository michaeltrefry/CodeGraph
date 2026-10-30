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
        (await Should.ThrowAsync<WorkException>(() => service.SaveSettingsAsync(ImportSources.Shortcut,
            new ImportSettings(Key("HO"), Table(null, ["sceneworks", " "])), approve: false, "owner", CancellationToken.None))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
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

        // ---- The owner takes mlx-rs away from the project: the next sync does not attach it again ----
        await db.UpdateAsync((await GetAsync("sc-95003")).GetProperty("key").GetString()!, new { clear = new[] { "repository" } });
        await db.UpdateAsync(Key("SW"), new { repositories = new[] { new { repository = "sceneworks", role = "owned" } } });
        var delta = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        delta.Status.ShouldBe(ImportRunStatuses.Completed, delta.Error);
        delta.Report!.Written.RepositoriesAttached.ShouldBe(0);
        delta.Report.Warnings.ShouldContain(warning => warning.Contains("mlx-rs") && warning.Contains("not attached again"));
        (await GetAsync(Key("SW"))).GetProperty("repositories").EnumerateArray().Select(member => member.GetProperty("repository").GetString()).ShouldBe(["sceneworks"]);
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

        // An agent's session claims the imported in-progress item and works on it.
        var itemKey = (await GetAsync("sc-96001")).GetProperty("key").GetString()!;
        var session = (await db.CallAsync(WorkTools.StartSession, new { project = key, harness = "claude-code", host = "dev-mac" }, "michael"))
            .GetProperty("session").GetProperty("id").GetString()!;
        var claimed = await SessionUpdateAsync(itemKey, session, new { claim = new { } });
        claimed.GetProperty("claim").GetProperty("session_id").GetString().ShouldBe(session);
        claimed.GetProperty("attempts").EnumerateArray().Single().GetProperty("outcome").GetString().ShouldBe("active");

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

        // Closing it ended the claim like any finish (E1): claim cleared, the session's claim released, the attempt ended.
        closed.TryGetProperty("claim", out _).ShouldBeFalse();
        closed.GetProperty("attempts").EnumerateArray().Single().GetProperty("outcome").GetString().ShouldBe("closed");
        (await ImportFixtures.CountAsync(db, "SELECT COUNT(*) FROM work_session_claims WHERE session_id = @session AND released_at IS NULL", new { session })).ShouldBe(0);

        // The feature is flagged, its reconcile task closed; every write is in the feed as import:shortcut (E3, E7).
        (await GetAsync(feature)).GetProperty("imported").GetProperty("archived").GetBoolean().ShouldBeTrue();
        (await GetAsync(reconcile)).GetProperty("status").GetString().ShouldBe("closed");
        var changes = (await db.AllChangesAsync(key)).Where(change => change.GetProperty("created_at").GetDateTime() >= delta.StartedAt).ToList();
        changes.ShouldAllBe(change => change.GetProperty("actor").GetProperty("name").GetString() == "import:shortcut");
        var itemChange = changes.Single(change => change.GetProperty("entity_key").GetString() == itemKey).GetProperty("changes").EnumerateArray().ToList();
        itemChange.ShouldContain(change => change.GetProperty("field").GetString() == "status" && change.GetProperty("new").GetString() == "closed");
        itemChange.ShouldContain(change => change.GetProperty("field").GetString() == "claim_holder" && change.GetProperty("old").GetString() == "michael");
        itemChange.ShouldContain(change => change.GetProperty("field").GetString() == "attempt" && change.GetProperty("new").GetProperty("outcome").GetString() == "closed");
        itemChange.ShouldContain(change => change.GetProperty("field").GetString() == "imported");
        changes.Single(change => change.GetProperty("entity_key").GetString() == feature).GetProperty("changes").EnumerateArray()
            .ShouldContain(change => change.GetProperty("field").GetString() == "imported");
        changes.ShouldContain(change => change.GetProperty("entity_key").GetString() == reconcile);

        // The session no longer holds it, so its next write is an ordinary one, not claim_lost: claim_lost means a claim
        // taken from the session, and finishing an item ends every session's claim on it (E1, as for any finisher).
        var late = await SessionUpdateAsync(itemKey, session, new { comment = "Late note." });
        late.GetProperty("status").GetString().ShouldBe("closed");

        // ---- A second delta writes nothing --------------------------------------------------
        var feedBefore = (await db.AllChangesAsync(key)).Count;
        var again = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        again.Report!.Written.ItemsUpdated.ShouldBe(0);
        again.Report.Written.FeaturesUpdated.ShouldBe(0);
        (await db.AllChangesAsync(key)).Count.ShouldBe(feedBefore);

        // A person reopens the reconcile task: the archived epic does not close it again.
        await db.UpdateAsync(reconcile, new { status = "ready" });
        await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        (await GetAsync(reconcile)).GetProperty("status").GetString().ShouldBe("ready");

        // ---- A full run re-reads the imported stories it no longer lists -----------------------
        source.Stories[96003] = source.Stories[96003] with { Archived = true };
        var settled = (await db.AllChangesAsync(key)).Count;
        var full = await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None);
        full.Report!.Written.Failures.ShouldBe(0, string.Join("; ", full.Report.Warnings));
        full.Report.Stories.ArchivedClosed.ShouldBe(3);
        full.Report.Stories.ArchivedSkipped.ShouldBe(1);
        full.Report.Written.ItemsUpdated.ShouldBe(1); // 96003 only: 96001 and 96002 are unchanged
        (await GetAsync("sc-96003")).GetProperty("status").GetString().ShouldBe("closed");
        (await GetAsync("sc-96001")).GetProperty("status").GetString().ShouldBe("closed");
        var fullChanges = (await db.AllChangesAsync(key)).Skip(settled).ToList();
        fullChanges.Select(change => change.GetProperty("entity_key").GetString()).ShouldBe([(await GetAsync("sc-96003")).GetProperty("key").GetString()]);

        // ---- Unarchived in Shortcut: the flag goes; the reconcile task a person closed stays closed ----
        await db.UpdateAsync(reconcile, new { status = "closed" });
        source.Epics[27864] = source.Epics[27864] with { Archived = false, UpdatedAt = DateTime.UtcNow.AddMinutes(10) };
        var unarchived = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        unarchived.Report!.Written.FeaturesUpdated.ShouldBe(1);
        (await GetAsync(feature)).GetProperty("imported").TryGetProperty("archived", out _).ShouldBeFalse();
        (await GetAsync(reconcile)).GetProperty("status").GetString().ShouldBe("closed");
    }

    private async Task<JsonElement> SessionUpdateAsync(string id, string session, object fields)
    {
        var arguments = JsonSerializer.SerializeToNode(fields, WorkJson.Options)!.AsObject();
        arguments["id"] = id;
        arguments["session_id"] = session;
        arguments["expected_version"] = (await GetAsync(id)).GetProperty("version").GetInt64();
        return (await db.CallAsync(WorkTools.UpdateWork, arguments, "michael")).GetProperty("item");
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

        // A plan's item-key-shaped ref names an item: sc-N of an epic is not one.
        var notAnItem = await db.RefusedAsync(WorkTools.SyncPlan, new { feature = featureKey, plan = new { items = new object[] { new { @ref = "sc-27864", title = "Epic as a ref" } } } });
        notAnItem.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        notAnItem.Message.ShouldContain("is an item key");

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
        source.Teams.Add(new ShortcutTeam("t4", "S.C.", null));
        var options = Options.Create(new WorkImportOptions { AttachmentStoragePath = Path.Combine(Path.GetTempPath(), "cg-import-keys-" + suffix) });
        var importer = new ShortcutImporter(db.Store, db.Store, new WorkAttachmentStorage(options, Options.Create(new WikiOptions())), options);

        var proposal = await importer.ProposeAsync(source, CancellationToken.None);

        var keys = proposal.Teams.ToDictionary(row => row.TeamKey, row => row.ProjectKey);
        keys["t1"].ShouldBe("LONGTEAMN2"); // LONGTEAMNA is the existing project's
        keys["t2"].ShouldBe("LONGTEAMN3");
        keys["t3"].ShouldBe("LONGTEAMNA"); // named like the existing project: mapped to it
        keys["t4"].ShouldBe("SC2"); // SC is the sc-N alias prefix
        proposal.Teams.Where(row => row.TeamKey != "t3").Select(row => row.ProjectKey).ShouldBeUnique();
    }
}


/// <summary>
/// sc-27929: a delta reads from the last import's start less an overlap, so a story Shortcut updated just before that
/// start (search-index lag, clock skew) is not lost.
/// </summary>
public sealed class WorkImportDeltaOverlapMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    [Fact]
    public async Task A_delta_reads_stories_updated_shortly_before_the_last_import_started()
    {
        var key = "OV" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key, name = "Overlap" } });
        var source = ImportFixtures.Empty();
        source.Stories[98001] = ImportFixtures.Story(98001, "Before", ImportFixtures.CodeGraphTeam) with { UpdatedAt = DateTime.UtcNow.AddDays(-2) };
        var service = WorkImportMariaDbTests.Service(db, source, null, Path.Combine(Path.GetTempPath(), "cg-import-overlap-" + key));
        await service.SaveSettingsAsync(ImportSources.Shortcut, new ImportSettings(key + "H", [new ImportTeamMapping(ImportFixtures.CodeGraphTeam, key, "CodeGraph")]),
            approve: true, "owner", CancellationToken.None);
        var import = await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None);

        // Updated in Shortcut a minute before the import started, but not yet searchable when it listed stories.
        source.Stories[98001] = source.Stories[98001] with { Name = "After", UpdatedAt = import.StartedAt.AddMinutes(-1) };
        var delta = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);

        delta.UpdatedSince.ShouldBe(import.StartedAt - WorkImportService.DeltaOverlap);
        delta.Report!.Written.ItemsUpdated.ShouldBe(1);
        (await db.CallAsync(WorkTools.GetWork, new { id = "sc-98001" })).GetProperty("title").GetString().ShouldBe("After");
    }
}

/// <summary>sc-27928: when attaching the table's repositories fails, the report says what the project owns, with a warning.</summary>
public sealed class WorkImportAttachFailureMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    /// <summary>The work store, with every project update refused (another writer won the race).</summary>
    public class RefusingProjectUpdates : System.Reflection.DispatchProxy
    {
        public IWorkStore Inner { get; set; } = null!;

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkStore.UpdateProjectAsync))
                return Task.FromException<ProjectView>(new WorkException(WorkErrorCodes.Conflict, "The project changed meanwhile."));
            try
            {
                return method.Invoke(Inner, args);
            }
            catch (System.Reflection.TargetInvocationException exception)
            {
                throw exception.InnerException!;
            }
        }
    }

    private sealed class Sources(IShortcutSource shortcut) : IWorkImportSourceFactory
    {
        public Task<IShortcutSource> ShortcutAsync(CancellationToken ct) => Task.FromResult(shortcut);
        public Task<IKanbanSource> KanbanAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_failed_attachment_is_not_reported_as_owned()
    {
        var key = "AF" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key, name = "Attach failure" } });
        var source = ImportFixtures.Empty();
        source.Stories[99001] = ImportFixtures.Story(99001, "PR in CodeGraph", ImportFixtures.CodeGraphTeam) with
        {
            PullRequests = [new ShortcutPullRequest(1, "https://github.com/michaeltrefry/CodeGraph/pull/1", "x", true, false)]
        };
        var work = System.Reflection.DispatchProxy.Create<IWorkStore, RefusingProjectUpdates>();
        ((RefusingProjectUpdates)(object)work).Inner = db.Store;
        var options = Options.Create(new WorkImportOptions { AttachmentStoragePath = Path.Combine(Path.GetTempPath(), "cg-import-af-" + key) });
        var service = new WorkImportService(db.Store, new ShortcutImporter(work, db.Store, new WorkAttachmentStorage(options, Options.Create(new WikiOptions())), options),
            new KanbanImporter(db.Store, db.Store), new Sources(source));
        await service.SaveSettingsAsync(ImportSources.Shortcut,
            new ImportSettings(key + "H", [new ImportTeamMapping(ImportFixtures.CodeGraphTeam, key, "CodeGraph", Repositories: ["codegraph"])]), approve: true, "owner", CancellationToken.None);

        var report = (await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None)).Report!;

        report.Written.RepositoriesAttached.ShouldBe(0);
        report.Projects[key].Repositories.ShouldBeEmpty();
        report.Warnings.ShouldContain(warning => warning.Contains("codegraph were not attached") && warning.Contains("The project changed meanwhile."));
        report.NoRepository.ShouldBe(["sc-99001"]);
        (await db.CallAsync(WorkTools.GetWork, new { id = key })).GetProperty("repositories").GetArrayLength().ShouldBe(0);
    }
}


/// <summary>
/// sc-27929: a story done in Shortcut that CodeGraph reopened and an agent claimed is closed when Shortcut archives it
/// (the mapped status did not change, the archive still closes it), with its claim, session claims and attempt ended.
/// </summary>
public sealed class WorkImportArchiveReopenedMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    [Fact]
    public async Task Archiving_closes_an_item_done_in_shortcut_that_codegraph_reopened_and_claimed()
    {
        var key = "RO" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key, name = "Reopened" } });
        var source = ImportFixtures.Empty();
        source.Stories[96101] = ImportFixtures.Story(96101, "Done in Shortcut", ImportFixtures.CodeGraphTeam) with { WorkflowStateId = ImportFixtures.DoneState };
        var service = WorkImportMariaDbTests.Service(db, source, null, Path.Combine(Path.GetTempPath(), "cg-import-reopen-" + key));
        await service.SaveSettingsAsync(ImportSources.Shortcut, new ImportSettings(key + "H", [new ImportTeamMapping(ImportFixtures.CodeGraphTeam, key, "CodeGraph")]),
            approve: true, "owner", CancellationToken.None);
        await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None);
        var item = await db.CallAsync(WorkTools.GetWork, new { id = "sc-96101" });
        item.GetProperty("status").GetString().ShouldBe("complete");
        var itemKey = item.GetProperty("key").GetString()!;

        // CodeGraph reopens it and an agent's session claims it.
        var session = (await db.CallAsync(WorkTools.StartSession, new { project = key, harness = "claude-code", host = "dev-mac" }, "michael"))
            .GetProperty("session").GetProperty("id").GetString()!;
        var reopened = (await db.CallAsync(WorkTools.UpdateWork, new { id = itemKey, session_id = session, expected_version = item.GetProperty("version").GetInt64(), status = "in_progress", claim = new { } },
            "michael")).GetProperty("item");
        reopened.GetProperty("claim").GetProperty("session_id").GetString().ShouldBe(session);

        source.Stories[96101] = source.Stories[96101] with { Archived = true, UpdatedAt = DateTime.UtcNow.AddMinutes(5) };
        var delta = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        delta.Report!.Written.Failures.ShouldBe(0, string.Join("; ", delta.Report.Warnings));

        var closed = await db.CallAsync(WorkTools.GetWork, new { id = itemKey });
        closed.GetProperty("status").GetString().ShouldBe("closed");
        closed.GetProperty("imported").GetProperty("archived").GetBoolean().ShouldBeTrue();
        closed.TryGetProperty("claim", out _).ShouldBeFalse();
        closed.GetProperty("attempts").EnumerateArray().Single().GetProperty("outcome").GetString().ShouldBe("closed");
        (await ImportFixtures.CountAsync(db, "SELECT COUNT(*) FROM work_session_claims WHERE session_id = @session AND released_at IS NULL", new { session })).ShouldBe(0);

        // Archived already: the next delta changes nothing.
        var again = await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        again.Report!.Written.ItemsUpdated.ShouldBe(0);
    }
}

/// <summary>
/// sc-27929: the reconcile task of an archived epic moves before the feature's flag is written, so a failed move is
/// retried; unarchiving reopens it only when the archive is what closed it.
/// </summary>
public sealed class WorkImportReconcileMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    /// <summary>The work store, refusing item updates while <see cref="Refuse"/> is set (an agent won the race).</summary>
    public class RefusingItemUpdates : System.Reflection.DispatchProxy
    {
        public IWorkStore Inner { get; set; } = null!;
        public bool Refuse { get; set; }

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (Refuse && method!.Name == nameof(IWorkStore.UpdateItemAsync))
                return Task.FromException<ItemView>(new WorkException(WorkErrorCodes.Conflict, "The task changed meanwhile."));
            try
            {
                return method!.Invoke(Inner, args);
            }
            catch (System.Reflection.TargetInvocationException exception)
            {
                throw exception.InnerException!;
            }
        }
    }

    private sealed class Sources(IShortcutSource shortcut) : IWorkImportSourceFactory
    {
        public Task<IShortcutSource> ShortcutAsync(CancellationToken ct) => Task.FromResult(shortcut);
        public Task<IKanbanSource> KanbanAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private Task<JsonElement> GetAsync(string id) => db.CallAsync(WorkTools.GetWork, new { id });

    [Fact]
    public async Task The_reconcile_task_follows_the_archive_and_a_failed_move_is_retried()
    {
        var key = "RC" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key, name = "Reconcile" } });
        var source = ImportFixtures.Empty();
        source.Epics[27864] = ImportFixtures.Recorded.Epics[27864] with { TeamIds = [ImportFixtures.CodeGraphTeam] };
        source.Stories[97101] = ImportFixtures.Story(97101, "In the epic", ImportFixtures.CodeGraphTeam, 27864);
        var work = System.Reflection.DispatchProxy.Create<IWorkStore, RefusingItemUpdates>();
        var refusing = (RefusingItemUpdates)(object)work;
        refusing.Inner = db.Store;
        var options = Options.Create(new WorkImportOptions { AttachmentStoragePath = Path.Combine(Path.GetTempPath(), "cg-import-rc-" + key) });
        var service = new WorkImportService(db.Store, new ShortcutImporter(work, db.Store, new WorkAttachmentStorage(options, Options.Create(new WikiOptions())), options),
            new KanbanImporter(db.Store, db.Store), new Sources(source));
        await service.SaveSettingsAsync(ImportSources.Shortcut, new ImportSettings(key + "H", [new ImportTeamMapping(ImportFixtures.CodeGraphTeam, key, "CodeGraph")]),
            approve: true, "owner", CancellationToken.None);
        await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None);
        var feature = (await GetAsync("sc-97101")).GetProperty("feature").GetProperty("key").GetString()!;
        var reconcile = (await db.CallAsync(WorkTools.FindWork, new { project = key, label = "import-reconcile" })).GetProperty("items")[0].GetProperty("key").GetString()!;
        var minutes = 0;
        async Task<ImportReport> SetArchivedAsync(bool archived)
        {
            source.Epics[27864] = source.Epics[27864] with { Archived = archived, UpdatedAt = DateTime.UtcNow.AddMinutes(++minutes) };
            return (await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None)).Report!;
        }

        bool Flagged(JsonElement view) => view.GetProperty("imported").TryGetProperty("archived", out var flag) && flag.GetBoolean();

        // ---- The move fails: the flag is not written, so the next delta retries it -------------
        refusing.Refuse = true;
        var failed = await SetArchivedAsync(true);
        failed.FailedIds.ShouldContain("epic-27864");
        Flagged(await GetAsync(feature)).ShouldBeFalse();
        (await GetAsync(reconcile)).GetProperty("status").GetString().ShouldBe("ready");
        refusing.Refuse = false;
        var retried = (await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None)).Report!;
        retried.Written.Failures.ShouldBe(0, string.Join("; ", retried.Warnings));
        Flagged(await GetAsync(feature)).ShouldBeTrue();
        (await GetAsync(reconcile)).GetProperty("status").GetString().ShouldBe("closed");

        // ---- Unarchived: the archive closed it, so it opens again ------------------------------
        await SetArchivedAsync(false);
        Flagged(await GetAsync(feature)).ShouldBeFalse();
        (await GetAsync(reconcile)).GetProperty("status").GetString().ShouldBe("ready");

        // ---- A person closes it, then the epic is archived and unarchived: it stays closed ----
        await db.UpdateAsync(reconcile, new { status = "closed" });
        await SetArchivedAsync(true);
        Flagged(await GetAsync(feature)).ShouldBeTrue();
        await SetArchivedAsync(false);
        Flagged(await GetAsync(feature)).ShouldBeFalse();
        (await GetAsync(reconcile)).GetProperty("status").GetString().ShouldBe("closed");
    }
}

/// <summary>sc-27928: a table repository the project lists as a dependency becomes owned in its place, not listed twice.</summary>
public sealed class WorkImportDependencyUpgradeMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    [Fact]
    public async Task A_dependency_named_by_the_table_becomes_owned()
    {
        var key = "DU" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        await db.CallAsync(WorkTools.CreateWork, new
        {
            new_project = new { key, name = "Dependency", repositories = new[] { new { repository = "inference", role = "owned" }, new { repository = "codegraph", role = "dependency" } } }
        });
        var source = ImportFixtures.Empty();
        source.Stories[99101] = ImportFixtures.Story(99101, "PR in CodeGraph", ImportFixtures.CodeGraphTeam) with
        {
            PullRequests = [new ShortcutPullRequest(1, "https://github.com/michaeltrefry/CodeGraph/pull/1", "x", true, false)]
        };
        var service = WorkImportMariaDbTests.Service(db, source, null, Path.Combine(Path.GetTempPath(), "cg-import-du-" + key));
        await service.SaveSettingsAsync(ImportSources.Shortcut,
            new ImportSettings(key + "H", [new ImportTeamMapping(ImportFixtures.CodeGraphTeam, key, "CodeGraph", Repositories: ["codegraph"])]), approve: true, "owner", CancellationToken.None);

        var dry = (await service.RunAsync(ImportSources.Shortcut, "dry_run", null, "owner", CancellationToken.None)).Report!;
        var report = (await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None)).Report!;

        report.Written.Failures.ShouldBe(0, string.Join("; ", report.Warnings));
        report.Written.RepositoriesAttached.ShouldBe(1);
        report.Projects[key].Repositories.ShouldBe(dry.Projects[key].Repositories);
        report.Projects[key].Repositories.ShouldBe(["inference", "codegraph"]);
        (await db.CallAsync(WorkTools.GetWork, new { id = key })).GetProperty("repositories").EnumerateArray()
            .Select(member => (member.GetProperty("repository").GetString(), member.GetProperty("role").GetString()))
            .ShouldBe([("inference", "owned"), ("codegraph", "owned")]);
        (await db.CallAsync(WorkTools.GetWork, new { id = "sc-99101" })).GetProperty("repository").GetString().ShouldBe("codegraph");
    }
}


/// <summary>
/// sc-27929: an archive and unarchive in Shortcut, with no change of the story's state, leave work CodeGraph finished
/// as it is; an item the archive closed goes back to the status it had; one a person moved since stays where it is.
/// </summary>
public sealed class WorkImportUnarchiveMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private Task<JsonElement> GetAsync(string id) => db.CallAsync(WorkTools.GetWork, new { id });

    private async Task<string> StatusAsync(string id) => (await GetAsync(id)).GetProperty("status").GetString()!;

    [Fact]
    public async Task Unarchiving_restores_only_what_the_archive_closed()
    {
        var key = "UA" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key, name = "Unarchive" } });
        var source = ImportFixtures.Empty();
        var state = ImportFixtures.Recorded.Stories[27876].WorkflowStateId; // In Progress: the state never changes
        source.Stories[97301] = ImportFixtures.Story(97301, "Completed in CodeGraph", ImportFixtures.CodeGraphTeam);
        source.Stories[97302] = ImportFixtures.Story(97302, "Closed by the archive", ImportFixtures.CodeGraphTeam);
        source.Stories[97303] = ImportFixtures.Story(97303, "Moved by a person after the archive", ImportFixtures.CodeGraphTeam);
        var service = WorkImportMariaDbTests.Service(db, source, null, Path.Combine(Path.GetTempPath(), "cg-import-ua-" + key));
        await service.SaveSettingsAsync(ImportSources.Shortcut, new ImportSettings(key + "H", [new ImportTeamMapping(ImportFixtures.CodeGraphTeam, key, "CodeGraph")]),
            approve: true, "owner", CancellationToken.None);
        await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None);
        (await StatusAsync("sc-97301")).ShouldBe("in_progress");
        await db.UpdateAsync((await GetAsync("sc-97301")).GetProperty("key").GetString()!, new { status = "complete" });

        var minutes = 0;
        async Task<ImportReport> SetArchivedAsync(bool archived)
        {
            var at = DateTime.UtcNow.AddMinutes(++minutes);
            foreach (var id in new long[] { 97301, 97302, 97303 })
                source.Stories[id] = source.Stories[id] with { Archived = archived, UpdatedAt = at, WorkflowStateId = state };
            var report = (await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None)).Report!;
            report.Written.Failures.ShouldBe(0, string.Join("; ", report.Warnings));
            return report;
        }

        await SetArchivedAsync(true);
        (await StatusAsync("sc-97301")).ShouldBe("complete");
        (await StatusAsync("sc-97302")).ShouldBe("closed");
        (await StatusAsync("sc-97303")).ShouldBe("closed");
        await db.UpdateAsync((await GetAsync("sc-97303")).GetProperty("key").GetString()!, new { status = "backlog" });

        await SetArchivedAsync(false);
        (await StatusAsync("sc-97301")).ShouldBe("complete");    // finished in CodeGraph: never reopened by the cycle
        (await StatusAsync("sc-97302")).ShouldBe("in_progress"); // the archive closed it: back to its status before
        (await StatusAsync("sc-97303")).ShouldBe("backlog");     // a person moved it since: left there
        (await GetAsync("sc-97302")).GetProperty("imported").TryGetProperty("archived", out _).ShouldBeFalse();

        // A later sync with nothing changed in Shortcut keeps all three.
        var again = (await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None)).Report!;
        again.Written.ItemsUpdated.ShouldBe(0);
        (await StatusAsync("sc-97301")).ShouldBe("complete");

        // A rename in Shortcut (its state unchanged) is merged without touching the status.
        source.Stories[97301] = source.Stories[97301] with { Name = "Renamed", UpdatedAt = DateTime.UtcNow.AddMinutes(30) };
        await service.RunAsync(ImportSources.Shortcut, "delta", null, "owner", CancellationToken.None);
        (await GetAsync("sc-97301")).GetProperty("title").GetString().ShouldBe("Renamed");
        (await StatusAsync("sc-97301")).ShouldBe("complete");
    }
}

/// <summary>
/// sc-27928: the table aliases retired repositories to the indexed repository their code moved to. Aliases are checked
/// when saved (each names an unindexed repository and points at an indexed one), kept with the table, and a story
/// naming a retired repository imports with the repository it moved to; an owner/name alias matches that owner only.
/// </summary>
public sealed class WorkImportRepositoryAliasMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private Task<JsonElement> GetAsync(string id) => db.CallAsync(WorkTools.GetWork, new { id });

    [Fact]
    public async Task A_story_naming_a_retired_repository_imports_with_the_repository_its_code_moved_to()
    {
        var key = "AL" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var source = ImportFixtures.Empty();
        source.Stories[98101] = ImportFixtures.Story(98101, "Named in the text", ImportFixtures.SceneWorksTeam) with
        {
            Description = "Fixed in https://github.com/SceneWorks/mlx-gen/blob/main/src/vae.rs."
        };
        source.Stories[98102] = ImportFixtures.Story(98102, "PR in the retired repository", ImportFixtures.SceneWorksTeam) with
        {
            PullRequests = [new ShortcutPullRequest(3, "https://github.com/SceneWorks/candle-gen/pull/3", "z", true, false)]
        };
        source.Stories[98103] = ImportFixtures.Story(98103, "Another owner's candle-gen", ImportFixtures.SceneWorksTeam) with
        {
            Description = "Upstream: https://github.com/acme/candle-gen/issues/1"
        };
        var service = WorkImportMariaDbTests.Service(db, source, null, Path.Combine(Path.GetTempPath(), "cg-import-alias-" + key));
        List<ImportTeamMapping> table = [new(ImportFixtures.SceneWorksTeam, key, "SceneWorks", Repositories: ["sceneworks", "inference"])];
        ImportSettings Settings(Dictionary<string, string> aliases) => new(key + "H", table, RepositoryAliases: aliases);

        // ---- Aliases are checked when saved ------------------------------------------------------
        async Task<WorkException> RefusedAsync(Dictionary<string, string> aliases) =>
            await Should.ThrowAsync<WorkException>(() => service.SaveSettingsAsync(ImportSources.Shortcut, Settings(aliases), approve: false, "owner", CancellationToken.None));
        (await RefusedAsync(new() { ["sceneworks"] = "inference" })).Message.ShouldContain("is the repository sceneworks CodeGraph indexes");
        (await RefusedAsync(new() { ["mlx-gen"] = "not-indexed" })).Message.ShouldContain("'not-indexed'");
        (await RefusedAsync(new() { ["has space"] = "inference" })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);

        var saved = await service.SaveSettingsAsync(ImportSources.Shortcut,
            Settings(new() { [" mlx-gen "] = "INFERENCE", ["SceneWorks/candle-gen"] = "inference" }), approve: true, "owner", CancellationToken.None);
        saved.RepositoryAliases!.ShouldBe(new Dictionary<string, string> { ["mlx-gen"] = "inference", ["SceneWorks/candle-gen"] = "inference" });
        (await service.GetSettingsAsync(ImportSources.Shortcut, CancellationToken.None)).RepositoryAliases!.Count.ShouldBe(2);

        // ---- The dry run reads the retired names as the repository they moved to ----------------
        var dry = (await service.RunAsync(ImportSources.Shortcut, "dry_run", null, "owner", CancellationToken.None)).Report!;
        dry.RepositoryMentions.Single(mention => mention.Name == "SceneWorks/mlx-gen").Repository.ShouldBe("inference");
        dry.RepositoryMentions.Single(mention => mention.Name == "acme/candle-gen").Repository.ShouldBeNull();
        dry.NoRepository.ShouldBe(["sc-98103"]);

        // ---- The import writes them -------------------------------------------------------------
        var run = await service.RunAsync(ImportSources.Shortcut, "import", null, "owner", CancellationToken.None);
        run.Status.ShouldBe(ImportRunStatuses.Completed, run.Error);
        (await GetAsync("sc-98101")).GetProperty("repository").GetString().ShouldBe("inference");
        (await GetAsync("sc-98102")).GetProperty("repository").GetString().ShouldBe("inference");
        (await GetAsync("sc-98103")).TryGetProperty("repository", out _).ShouldBeFalse(); // the alias names SceneWorks's candle-gen only
    }
}
