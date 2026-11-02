using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Work;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Work imported from Shortcut before the 2026-11-02 cutover, after the importers were retired (CG-147),
/// against a real MariaDB. The rows are written as the importer left them: a feature and items with their
/// imported provenance, sc-NNNN item aliases, comments with their source author and time, a historical PR
/// link and a stored attachment. get_work resolves sc-N of a story to its item and of an epic to its feature
/// (sc-27930) on every surface; GitHub evidence follows an sc alias and ignores a historical PR link; the
/// stored file is served with a token.
/// </summary>
public sealed class ImportedWorkMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private Task<JsonElement> GetAsync(string id) => db.CallAsync(WorkTools.GetWork, new { id });

    private sealed record Imported(string ProjectKey, string FeatureKey, string StoryKey, Guid StoryId, string DoneKey, string AttachmentRoot, Guid AttachmentId);

    private static string Json(object value) => JsonSerializer.Serialize(value, WorkJson.Options);

    /// <summary>
    /// A feature imported from epic sc-<paramref name="epic"/> with an open story (sc-<paramref name="story"/>) and a done one
    /// (sc-<paramref name="done"/>) carrying its merged PR #<paramref name="done"/>, an agent's comment and a stored file.
    /// The tests of the class share one database, so each seeds its own numbers in a project owning its own repository.
    /// </summary>
    private async Task<Imported> SeedAsync(int epic, int story, int done, string repository)
    {
        var pr = $"https://github.com/michaeltrefry/CodeGraph/pull/{done}";
        var key = "IM" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key, name = "Imported", repositories = new[] { new { repository, role = "owned" } } } });
        var feature = (await db.CallAsync(WorkTools.CreateWork, new { project = key, feature = new { title = "CodeGraph agent coordination" } })).GetProperty("feature");
        var featureKey = feature.GetProperty("key").GetString()!;
        var storyItem = (await db.CallAsync(WorkTools.CreateWork, new { project = key, item = new { title = "Imports", feature = featureKey, repository, status = "ready" } })).GetProperty("item");
        var doneItem = (await db.CallAsync(WorkTools.CreateWork, new { project = key, item = new { title = "Work tracking core", feature = featureKey, repository, status = "complete" } })).GetProperty("item");
        var storyId = storyItem.GetProperty("id").GetGuid();
        var doneId = doneItem.GetProperty("id").GetGuid();

        var root = Path.Combine(Path.GetTempPath(), "cg-imported-" + key);
        var storage = new AttachmentStorage(root);
        string storedPath;
        using (var content = new MemoryStream("imported file"u8.ToArray()))
            (storedPath, _) = await storage.CreateAsync(1, content);
        var attachmentId = Guid.NewGuid();
        var sourceTime = new DateTime(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);

        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.ExecuteAsync(
            "UPDATE work_features SET imported_json = @imported, requirements_unreconciled = 1 WHERE id = @id",
            new { id = feature.GetProperty("id").GetGuid(), imported = Json(new ImportedView("shortcut", $"sc-epic-{epic}", $"https://app.shortcut.com/trefry/epic/{epic}")) });
        await connection.ExecuteAsync(
            "UPDATE work_items SET imported_json = @imported WHERE id = @id",
            new[]
            {
                new { id = storyId, imported = Json(new ImportedView("shortcut", $"sc-{story}", $"https://app.shortcut.com/trefry/story/{story}", new ImportHistory(["michael"], State: "In Progress"))) },
                new { id = doneId, imported = Json(new ImportedView("shortcut", $"sc-{done}", $"https://app.shortcut.com/trefry/story/{done}")) }
            });
        await connection.ExecuteAsync(
            "INSERT INTO work_item_aliases (item_id, alias, created_by, created_at) VALUES (@itemId, @alias, 'import:shortcut', UTC_TIMESTAMP(6))",
            new[] { new { itemId = storyId, alias = $"sc-{story}" }, new { itemId = doneId, alias = $"sc-{done}" } });
        await connection.ExecuteAsync(
            "INSERT INTO work_item_comments (id, item_id, author, author_kind, body, created_at, source_created_at, external_id) " +
            "VALUES (@id, @itemId, 'claude', 'agent', 'Shipped in the merged PR.', UTC_TIMESTAMP(6), @sourceTime, '91001')",
            new { id = Guid.NewGuid(), itemId = doneId, sourceTime });
        await connection.ExecuteAsync(
            "INSERT INTO work_item_links (id, item_id, kind, url, url_hash, created_by, created_at, historical) " +
            "VALUES (@id, @itemId, 'pr', @url, SHA2(@url, 256), 'import:shortcut', UTC_TIMESTAMP(6), 1)",
            new { id = Guid.NewGuid(), itemId = doneId, url = pr });
        await connection.ExecuteAsync(
            "INSERT INTO work_item_attachments (id, item_id, external_id, filename, content_type, size_bytes, storage_path, source_url, created_by, created_at) " +
            "VALUES (@id, @itemId, '7942', 'notes.txt', 'text/plain', 13, @storedPath, 'https://media.app.shortcut.com/notes.txt', 'import:shortcut', UTC_TIMESTAMP(6))",
            new { id = attachmentId, itemId = doneId, storedPath });

        return new Imported(key, featureKey, storyItem.GetProperty("key").GetString()!, storyId, doneItem.GetProperty("key").GetString()!, root, attachmentId);
    }

    [Fact]
    public async Task Sc_n_of_an_imported_epic_resolves_to_its_feature_and_of_a_story_to_its_item()
    {
        var imported = await SeedAsync(epic: 27864, story: 27876, done: 27866, repository: "inference");

        (await GetAsync("sc-27876")).GetProperty("key").GetString().ShouldBe(imported.StoryKey);
        foreach (var name in new[] { "sc-27864", "SC-27864", "sc-027864" })
        {
            var epic = await GetAsync(name);
            epic.GetProperty("key").GetString().ShouldBe(imported.FeatureKey, name);
            epic.GetProperty("imported").GetProperty("external_id").GetString().ShouldBe("sc-epic-27864");
        }

        (await db.RefusedAsync(WorkTools.GetWork, new { id = "sc-99999999" })).Code.ShouldBe(WorkErrorCodes.NotFound);

        // A plan's item-key-shaped ref names an item: sc-N of an epic is not one.
        var notAnItem = await db.RefusedAsync(WorkTools.SyncPlan, new { feature = imported.FeatureKey, plan = new { items = new object[] { new { @ref = "sc-27864", title = "Epic as a ref" } } } });
        notAnItem.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        notAnItem.Message.ShouldContain("is an item key");

        // update_work names the feature the same way.
        (await db.UpdateAsync("sc-27864", new { goals = new[] { "Agents coordinate through CodeGraph" } })).GetProperty("key").GetString().ShouldBe(imported.FeatureKey);

        // REST resolves it alike.
        await using var host = await WorkRestHost.StartAsync(db.Service);
        var token = await host.CreateTokenAsync("rest-user");
        var (status, body) = await host.SendAsync(HttpMethod.Get, "/api/work/sc-27864", token: token);
        status.ShouldBe(200);
        body.GetProperty("key").GetString().ShouldBe(imported.FeatureKey);
        var (itemStatus, item) = await host.SendAsync(HttpMethod.Get, "/api/work/sc-27866", token: token);
        itemStatus.ShouldBe(200);
        item.GetProperty("key").GetString().ShouldBe(imported.DoneKey);
        Directory.Delete(imported.AttachmentRoot, recursive: true);
    }

    [Fact]
    public async Task An_imported_item_keeps_its_provenance_comments_and_attachments()
    {
        var imported = await SeedAsync(epic: 1001, story: 1002, done: 1003, repository: "sceneworks");

        var done = await GetAsync(imported.DoneKey);
        done.GetProperty("imported").GetProperty("source").GetString().ShouldBe("shortcut");
        done.GetProperty("imported").GetProperty("external_id").GetString().ShouldBe("sc-1003");
        var comment = done.GetProperty("comments").EnumerateArray().Single();
        comment.GetProperty("author").GetString().ShouldBe("claude");
        comment.GetProperty("author_kind").GetString().ShouldBe("agent");
        comment.GetProperty("source_created_at").GetDateTime().ShouldBe(new DateTime(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc));
        var attachment = done.GetProperty("attachments").EnumerateArray().Single();
        attachment.GetProperty("id").GetGuid().ShouldBe(imported.AttachmentId);
        attachment.GetProperty("stored").GetBoolean().ShouldBeTrue();
        (await GetAsync(imported.StoryKey)).GetProperty("imported").GetProperty("history").GetProperty("state").GetString().ShouldBe("In Progress");

        // The stored file is served to a token holder.
        await using var host = await WorkRestHost.StartAsync(db.Service, configureServices: services =>
        {
            services.AddSingleton<IWorkAttachmentStore>(db.Store);
            services.AddSingleton<IWorkAttachmentStorage>(new WorkAttachmentStorage(
                Options.Create(new WorkAttachmentOptions { StoragePath = imported.AttachmentRoot }), Options.Create(new WikiOptions())));
        });
        var token = await host.CreateTokenAsync("agent");
        var (status, file) = await host.SendAsync(HttpMethod.Get, $"/api/work/attachments/{imported.AttachmentId}", token: token);
        status.ShouldBe(200);
        file.GetProperty("Raw").GetString().ShouldBe("imported file");
        (await host.SendAsync(HttpMethod.Get, $"/api/work/attachments/{Guid.NewGuid()}", token: token)).Status.ShouldBe(404);
        Directory.Delete(imported.AttachmentRoot, recursive: true);
    }

    [Fact]
    public async Task GitHub_evidence_follows_an_sc_alias_and_ignores_a_historical_pr_link()
    {
        var imported = await SeedAsync(epic: 2001, story: 2002, done: 2003, repository: "codegraph");
        const string pr = "https://github.com/michaeltrefry/CodeGraph/pull/2003";
        var done = await GetAsync(imported.DoneKey);

        // The PR the import linked was merged before the import: an event on it moves nothing ...
        var historical = await db.Store.RecordGitHubEvidenceAsync(new GitHubEvidenceSpec("d-" + imported.ProjectKey + "-1", GitHubEvidenceKind.IssueComment, ["codegraph"], ["late comment"], "michael",
            PullRequestUrl: pr, PullRequestNumber: 2003, ExternalId: "c-" + imported.ProjectKey + "-1", Body: "late comment"));
        historical.Items.ShouldBeEmpty();
        (await GetAsync(imported.DoneKey)).GetProperty("version").GetInt64().ShouldBe(done.GetProperty("version").GetInt64());
        // ... until an agent adds the same link again and takes it over.
        await db.UpdateAsync(imported.DoneKey, new { add_links = new[] { new { kind = "pr", url = pr } } });
        (await db.Store.RecordGitHubEvidenceAsync(new GitHubEvidenceSpec("d-" + imported.ProjectKey + "-2", GitHubEvidenceKind.IssueComment, ["codegraph"], ["later comment"], "michael",
            PullRequestUrl: pr, PullRequestNumber: 2003, ExternalId: "c-" + imported.ProjectKey + "-2", Body: "later comment"))).Items.ShouldHaveSingleItem();

        // A push naming the sc alias moves the imported item forward, like any item.
        var push = await db.Store.RecordGitHubEvidenceAsync(new GitHubEvidenceSpec("d-" + imported.ProjectKey + "-3", GitHubEvidenceKind.Push, ["codegraph"], ["claude/sc-2002-imports"], "michael",
            Branch: "claude/sc-2002-imports", BranchUrl: "https://github.com/michaeltrefry/CodeGraph/tree/claude/sc-2002-imports"));
        push.Items.Single().Key.ShouldBe(imported.StoryKey);
        (await GetAsync(imported.StoryKey)).GetProperty("status").GetString().ShouldBe("in_progress");
        Directory.Delete(imported.AttachmentRoot, recursive: true);
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
        await using var connection = new MySqlConnection(db.ConnectionString);
        var before = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM knowledge_entries");

        var preview = await db.CallAsync(WorkTools.ImportKnowledge, new { project = key, candidates = candidates.Select(candidate => candidate.Candidate).ToList() });

        preview.GetProperty("stored").GetBoolean().ShouldBeFalse();
        preview.GetProperty("candidates").EnumerateArray().Select(candidate => candidate.GetProperty("outcome").GetString()).ShouldBe(["would_store", "personal"]);
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM knowledge_entries")).ShouldBe(before);
    }
}
