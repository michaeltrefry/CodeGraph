using System.Text;
using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// GitHub evidence on work items (sc-27870) against a real MariaDB, fed the recorded payloads under
/// Fixtures/GitHub exactly as the webhook receives them: forward-only moves, merge flags,
/// closed-unmerged returns, review evidence, superseded attempts, aliases, redelivery.
/// </summary>
public sealed class WorkGitHubEvidenceMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private readonly GitHubWorkEvidenceService service = GitHubWorkEvidenceService.ForStore(db.Store);

    private static string Key(JsonElement element) => element.GetProperty("key").GetString()!;

    private static string Status(JsonElement element) => element.GetProperty("status").GetString()!;

    // Every test uses pull request numbers of its own: the PR URL is a target of its own.
    private static int nextPullRequest = 100;

    private static int NewPullRequest() => Interlocked.Increment(ref nextPullRequest);

    private static string Payload(string fixture, string key, int pull)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GitHub", fixture + ".json"));
        return text.Replace("__KEY__", key)
            .Replace("/pull/7", $"/pull/{pull}").Replace("/pulls/7", $"/pulls/{pull}")
            .Replace("\"number\": 7", $"\"number\": {pull}");
    }

    private async Task<GitHubEvidenceResult> SendAsync(string eventName, string fixture, string key, int pull, string? delivery = null, Func<string, string>? edit = null)
    {
        var text = Payload(fixture, key, pull);
        return await service.HandleAsync(eventName, Encoding.UTF8.GetBytes(edit?.Invoke(text) ?? text), delivery ?? Guid.NewGuid().ToString(), []);
    }

    private async Task<JsonElement> GetAsync(string key) => await db.CallAsync(WorkTools.GetWork, new { id = key });

    // A repository is owned by one project, so the tests share the project that owns the payloads' repository.
    private static readonly SemaphoreSlim ProjectGate = new(1, 1);
    private static string? sharedProject;

    private async Task<string> SharedProjectAsync()
    {
        await ProjectGate.WaitAsync();
        try
        {
            return sharedProject ??= await db.NewProjectAsync("GH", "sceneworks");
        }
        finally
        {
            ProjectGate.Release();
        }
    }

    private async Task<(string Project, string Item, Guid Id)> NewReadyItemAsync(string status = "ready", string? project = null)
    {
        project ??= await SharedProjectAsync();
        var item = await db.NewItemAsync(project, "Record evidence", status);
        return (project, Key(item), Guid.Parse(item.GetProperty("id").GetString()!));
    }

    private static List<JsonElement> Links(JsonElement item) =>
        item.TryGetProperty("links", out var links) ? links.EnumerateArray().ToList() : [];

    [Fact]
    public async Task A_pushed_branch_and_an_opened_PR_move_a_Ready_item_to_In_Review_with_both_links_and_a_merge_flags_it_without_completing()
    {
        var (project, key, id) = await NewReadyItemAsync();
        var pull = NewPullRequest();

        var push = await SendAsync("push", "push", key, pull);
        push.Items.Single().From.ShouldBe(WorkStatus.Ready);
        push.Items.Single().To.ShouldBe(WorkStatus.InProgress);
        Status(await GetAsync(key)).ShouldBe("in_progress");

        var opened = await SendAsync("pull_request", "pull_request.opened", key, pull);
        opened.Items.Single().To.ShouldBe(WorkStatus.InReview);

        var item = await GetAsync(key);
        Status(item).ShouldBe("in_review");
        var links = Links(item);
        links.Select(link => link.GetProperty("kind").GetString()).ShouldBe(["branch", "pr"], ignoreOrder: true);
        links.Single(link => link.GetProperty("kind").GetString() == "pr").GetProperty("url").GetString()
            .ShouldBe($"https://github.com/michaeltrefry/sceneworks/pull/{pull}");
        links.Single(link => link.GetProperty("kind").GetString() == "branch").GetProperty("url").GetString()
            .ShouldBe($"https://github.com/michaeltrefry/sceneworks/tree/claude/{key}-evidence");

        var merged = await SendAsync("pull_request", "pull_request.closed.merged", key, pull);
        merged.Items.Single().FlaggedMerged.ShouldBeTrue();
        item = await GetAsync(key);
        Status(item).ShouldBe("in_review");
        item.GetProperty("merged_awaiting_acceptance").GetBoolean().ShouldBeTrue();

        // Every write is one feed entry attributed to the GitHub login, in order.
        var feed = (await db.AllChangesAsync(project)).Where(entry => entry.GetProperty("entity_key").GetString() == key).ToList();
        feed.Select(entry => entry.GetProperty("kind").GetString()).ShouldBe(["item_created", "item_updated", "item_updated", "item_updated"]);
        var written = feed.Skip(1).ToList();
        written.ShouldAllBe(entry => entry.GetProperty("actor").GetProperty("kind").GetString() == "system");
        written.Select(entry => entry.GetProperty("actor").GetProperty("name").GetString()).ShouldBe(["github:claude-agent", "github:claude-agent", "github:michaeltrefry"]);
        (await db.Store.ListGitHubEvidenceAsync(id, 10)).Single().Kind.ShouldBe("merge");
    }

    [Fact]
    public async Task A_redelivery_changes_nothing_and_adds_no_second_feed_entry()
    {
        var (project, key, _) = await NewReadyItemAsync();
        var pull = NewPullRequest();
        var delivery = Guid.NewGuid().ToString();

        (await SendAsync("push", "push", key, pull, delivery)).Items.Single().Result.ShouldBe("applied");
        var feedAfterFirst = (await db.AllChangesAsync(project)).Count;
        var version = (await GetAsync(key)).GetProperty("version").GetInt64();

        (await SendAsync("push", "push", key, pull, delivery)).Items.Single().Result.ShouldBe("duplicate");
        // The same push under a new delivery id finds the work done already.
        (await SendAsync("push", "push", key, pull)).Items.Single().Result.ShouldBe("unchanged");

        (await db.AllChangesAsync(project)).Count.ShouldBe(feedAfterFirst);
        (await GetAsync(key)).GetProperty("version").GetInt64().ShouldBe(version);
        Links(await GetAsync(key)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_redeliveries_of_one_delivery_write_once()
    {
        var (project, key, _) = await NewReadyItemAsync();
        var pull = NewPullRequest();
        var delivery = Guid.NewGuid().ToString();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => SendAsync("pull_request", "pull_request.opened", key, pull, delivery)));

        results.Count(result => result.Items.Single().Result == "applied").ShouldBe(1);
        results.Count(result => result.Items.Single().Result == "duplicate").ShouldBe(5);
        (await db.AllChangesAsync(project)).Count(entry => entry.GetProperty("entity_key").GetString() == key && entry.GetProperty("kind").GetString() == "item_updated").ShouldBe(1);
    }

    [Fact]
    public async Task Evidence_moves_items_forward_only_and_never_completes()
    {
        var (_, inReview, _) = await NewReadyItemAsync("in_review");
        var pull = NewPullRequest();
        await SendAsync("push", "push", inReview, pull);
        Status(await GetAsync(inReview)).ShouldBe("in_review");

        var (_, inProgressForMerge, _) = await NewReadyItemAsync("in_progress");
        await SendAsync("pull_request", "pull_request.closed.merged", inProgressForMerge, NewPullRequest());
        var merged = await GetAsync(inProgressForMerge);
        Status(merged).ShouldBe("in_review");
        merged.GetProperty("merged_awaiting_acceptance").GetBoolean().ShouldBeTrue();

        var (_, complete, _) = await NewReadyItemAsync("complete");
        var completePull = NewPullRequest();
        await SendAsync("push", "push", complete, completePull);
        await SendAsync("pull_request", "pull_request.opened", complete, completePull);
        await SendAsync("pull_request", "pull_request.closed.merged", complete, completePull);
        var done = await GetAsync(complete);
        Status(done).ShouldBe("complete");
        done.TryGetProperty("merged_awaiting_acceptance", out _).ShouldBeFalse();
        Links(done).Count.ShouldBe(2);

        var (_, backlog, _) = await NewReadyItemAsync("backlog");
        await SendAsync("push", "push", backlog, NewPullRequest());
        Status(await GetAsync(backlog)).ShouldBe("in_progress");
    }

    [Fact]
    public async Task A_PR_closed_unmerged_returns_an_In_Review_item_to_Ready_and_ends_its_attempt_with_the_PR_description()
    {
        var (_, key, id) = await NewReadyItemAsync();
        var pull = NewPullRequest();
        await db.UpdateAsync(key, new { status = "in_progress", claim = new { marker = "run-1" } }, "michael");
        await SendAsync("push", "push", key, pull);
        await SendAsync("pull_request", "pull_request.opened", key, pull);
        Status(await GetAsync(key)).ShouldBe("in_review");

        var closed = await SendAsync("pull_request", "pull_request.closed.unmerged", key, pull);

        closed.Items.Single().To.ShouldBe(WorkStatus.Ready);
        var item = await GetAsync(key);
        Status(item).ShouldBe("ready");
        item.TryGetProperty("claim", out _).ShouldBeFalse();
        var attempt = item.GetProperty("attempts").EnumerateArray().Single();
        attempt.GetProperty("outcome").GetString().ShouldBe("abandoned");
        attempt.GetProperty("reason").GetString()!.ShouldContain("closed without merging");
        attempt.GetProperty("reason").GetString()!.ShouldContain("the approach moved to a webhook queue");
        var evidence = (await db.Store.ListGitHubEvidenceAsync(id, 10)).Single();
        evidence.Kind.ShouldBe("pr_closed");
        evidence.Body!.ShouldContain("Abandoned: the approach moved to a webhook queue.");
        // The item is claimable again: another agent can take it.
        var claimed = await db.UpdateAsync(key, new { claim = new { marker = "run-2" } }, "bob");
        claimed.GetProperty("claim").GetProperty("holder").GetString().ShouldBe("bob");
    }

    [Fact]
    public async Task A_PR_closed_unmerged_without_an_attempt_drafts_an_abandoned_one_and_leaves_an_item_that_is_not_In_Review()
    {
        var (_, key, _) = await NewReadyItemAsync("in_review");
        var pull = NewPullRequest();
        await SendAsync("pull_request", "pull_request.closed.unmerged", key, pull);

        var item = await GetAsync(key);
        Status(item).ShouldBe("ready");
        var attempt = item.GetProperty("attempts").EnumerateArray().Single();
        attempt.GetProperty("outcome").GetString().ShouldBe("abandoned");
        attempt.GetProperty("holder").GetProperty("identity").GetString().ShouldBe("github:michaeltrefry");

        var (_, inProgress, _) = await NewReadyItemAsync("in_progress");
        await SendAsync("pull_request", "pull_request.closed.unmerged", inProgress, NewPullRequest());
        var untouched = await GetAsync(inProgress);
        Status(untouched).ShouldBe("in_progress");
        untouched.TryGetProperty("attempts", out var attempts).ShouldBe(false, attempts.ToString());
    }

    [Fact]
    public async Task A_PR_of_a_superseded_attempt_is_recorded_and_moves_nothing()
    {
        var (_, key, id) = await NewReadyItemAsync();
        var pull = NewPullRequest();
        await db.UpdateAsync(key, new { status = "in_progress", claim = new { marker = "run-1" } }, "michael");
        await SendAsync("push", "push", key, pull);
        await SendAsync("pull_request", "pull_request.opened", key, pull);
        // A re-plan supersedes the attempt that made the branch and the PR.
        await using (var connection = new MySqlConnection(db.ConnectionString))
            await connection.ExecuteAsync("UPDATE work_attempts SET outcome = 'superseded' WHERE item_id = @id", new { id });
        // The item is put back to work under the revised plan.
        await db.UpdateAsync(key, new { status = "in_progress" }, "michael");
        var before = await GetAsync(key);

        var merged = await SendAsync("pull_request", "pull_request.closed.merged", key, pull);
        var review = await SendAsync("pull_request_review", "pull_request_review.submitted", key, pull);
        var closed = await SendAsync("pull_request", "pull_request.closed.unmerged", key, pull);

        merged.Items.Single().Superseded.ShouldBeTrue();
        review.Items.Single().Superseded.ShouldBeTrue();
        closed.Items.Single().Superseded.ShouldBeTrue();
        var item = await GetAsync(key);
        Status(item).ShouldBe("in_progress");
        item.TryGetProperty("merged_awaiting_acceptance", out _).ShouldBeFalse();
        item.GetProperty("attempts").EnumerateArray().Single().GetProperty("outcome").GetString().ShouldBe("superseded");
        var evidence = await db.Store.ListGitHubEvidenceAsync(id, 10);
        evidence.Select(entry => entry.Kind).ShouldBe(["merge", "review", "pr_closed"], ignoreOrder: true);
        evidence.ShouldAllBe(entry => entry.Superseded);
        before.GetProperty("version").GetInt64().ShouldBeLessThan(item.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Keys_match_on_word_boundaries_in_the_branch_title_or_body_and_a_PR_can_name_several_items()
    {
        var project = await db.NewProjectAsync("GK", "codegraph");
        var items = new List<string>();
        for (var number = 1; number <= 13; number++)
            items.Add(Key(await db.NewItemAsync(project, $"Item {number}")));
        var lower = project.ToLowerInvariant();

        // A branch for item 12 leaves items 1 and 2 alone; a longer prefix is not the project's key.
        static string Repo(string text) => text.Replace("\"name\": \"sceneworks\"", "\"name\": \"codegraph\"");
        await SendAsync("push", "push", items[11], NewPullRequest(), edit: Repo);
        Status(await GetAsync(items[11])).ShouldBe("in_progress");
        Status(await GetAsync(items[0])).ShouldBe("ready");
        Status(await GetAsync(items[1])).ShouldBe("ready");
        Status(await GetAsync(items[12])).ShouldBe("ready");
        var notAKey = await service.HandleAsync("push", Encoding.UTF8.GetBytes(Repo(Payload("push", "x", 1)).Replace("claude/x-evidence", $"claude/X{project}-1-and-{lower}-1x")), "d-" + Guid.NewGuid(), []);
        notAKey.Items.ShouldBeEmpty();
        Status(await GetAsync(items[0])).ShouldBe("ready");

        // Title and body name items; one PR can name several.
        var pull = NewPullRequest();
        var result = await SendAsync("pull_request", "pull_request.opened", "none", pull,
            edit: text => text.Replace("none: record GitHub evidence", $"{items[2]} and {lower}-4 together").Replace("Closes none.", $"Part of {items[3].ToLowerInvariant()}.").Replace("claude/none-evidence", "claude/plain").Replace("\"name\": \"sceneworks\"", "\"name\": \"codegraph\""));
        result.Items.Select(item => item.Key).ShouldBe([items[2], items[3]], ignoreOrder: true);
        Status(await GetAsync(items[2])).ShouldBe("in_review");
        Status(await GetAsync(items[3])).ShouldBe("in_review");
        Status(await GetAsync(items[0])).ShouldBe("ready");
    }

    [Fact]
    public async Task A_repository_outside_the_items_project_moves_nothing()
    {
        var (_, key, _) = await NewReadyItemAsync(project: await db.NewProjectAsync("GO", "inference"));

        var result = await SendAsync("push", "push", key, NewPullRequest());

        result.Items.ShouldBeEmpty();
        Status(await GetAsync(key)).ShouldBe("ready");
    }

    [Fact]
    public async Task An_external_alias_resolves_like_a_key_and_matches_in_branches_and_titles()
    {
        var (_, key, id) = await NewReadyItemAsync();
        var alias = $"sc-{Random.Shared.Next(100000, 999999)}";
        var other = Key(await db.NewItemAsync(key[..key.IndexOf('-')], "Other"));

        var aliased = await db.Store.AddItemAliasesAsync(id, [alias.ToUpperInvariant()], new WorkActor("importer"));
        aliased.Key.ShouldBe(key);
        (await db.Store.AddItemAliasesAsync(id, [alias], new WorkActor("importer"))).Version.ShouldBe(aliased.Version);
        (await GetAsync(alias)).GetProperty("key").GetString().ShouldBe(key);
        (await db.Store.ResolveAsync(alias.ToUpperInvariant()))!.Id.ShouldBe(id);
        var otherItem = (await db.Store.ResolveAsync(other))!;
        (await Should.ThrowAsync<WorkException>(() => db.Store.AddItemAliasesAsync(otherItem.Id, [alias], new WorkActor("importer")))).Code.ShouldBe(WorkErrorCodes.Conflict);
        (await Should.ThrowAsync<WorkException>(() => db.Store.AddItemAliasesAsync(id, ["not an alias"], new WorkActor("importer")))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);

        var pull = NewPullRequest();
        await SendAsync("push", "push", alias, pull);
        Status(await GetAsync(key)).ShouldBe("in_progress");
        await SendAsync("pull_request", "pull_request.opened", alias.ToUpperInvariant(), pull);
        Status(await GetAsync(key)).ShouldBe("in_review");
        Status(await GetAsync(other)).ShouldBe("ready");
    }

    [Fact]
    public async Task Reviews_and_comments_are_stored_as_evidence_with_source_and_priority_and_move_no_status()
    {
        var (project, key, id) = await NewReadyItemAsync("in_review");
        var pull = NewPullRequest();
        await SendAsync("pull_request", "pull_request.opened", key, pull);
        var feedBefore = (await db.AllChangesAsync(project)).Count;

        await SendAsync("pull_request_review", "pull_request_review.submitted", key, pull);
        await SendAsync("pull_request_review_comment", "pull_request_review_comment.created", key, pull);
        await SendAsync("issue_comment", "issue_comment.created", key, pull);

        Status(await GetAsync(key)).ShouldBe("in_review");
        var evidence = await db.Store.ListGitHubEvidenceAsync(id, 10);
        evidence.Count.ShouldBe(3);
        var review = evidence.Single(entry => entry.Kind == "review");
        (review.Author, review.Source, review.Priority, review.ReviewState).ShouldBe(("michaeltrefry", "human", "high", "changes_requested"));
        var bot = evidence.Single(entry => entry.Kind == "review_comment");
        (bot.Author, bot.Source, bot.Priority, bot.Location).ShouldBe(("coderabbitai[bot]", "bot", "normal", "src/Evidence.cs:42"));
        var comment = evidence.Single(entry => entry.Kind == "issue_comment");
        (comment.Source, comment.Priority).ShouldBe(("human", "high"));

        // Each lands once in the feed, attributed to its author; a repeat under a new delivery adds nothing.
        var feed = (await db.AllChangesAsync(project)).Skip(feedBefore).ToList();
        feed.Select(entry => entry.GetProperty("actor").GetProperty("name").GetString()).ShouldBe(["github:michaeltrefry", "github:coderabbitai[bot]", "github:michaeltrefry"]);
        await SendAsync("pull_request_review", "pull_request_review.submitted", key, pull);
        (await db.AllChangesAsync(project)).Count.ShouldBe(feedBefore + 3);
        (await db.Store.ListGitHubEvidenceAsync(id, 10)).Count.ShouldBe(3);

        // An edited review is the same evidence, updated once.
        await SendAsync("pull_request_review", "pull_request_review.submitted", key, pull,
            edit: text => text.Replace("\"submitted\"", "\"edited\"").Replace("do not restate", "never restate"));
        (await db.Store.ListGitHubEvidenceAsync(id, 10)).Single(entry => entry.Kind == "review").Body!.ShouldContain("never restate");

        // A comment on a plain issue is not evidence.
        (await SendAsync("issue_comment", "issue_comment.on_issue", key, pull)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Events_that_are_not_evidence_record_nothing()
    {
        var (_, key, _) = await NewReadyItemAsync();
        var pull = NewPullRequest();

        (await SendAsync("push", "push.branch_deleted", key, pull)).Items.ShouldBeEmpty();
        (await SendAsync("ping", "push", key, pull)).Items.ShouldBeEmpty();
        (await SendAsync("pull_request", "pull_request.opened", key, pull, edit: text => text.Replace("\"opened\"", "\"labeled\""))).Items.ShouldBeEmpty();
        Status(await GetAsync(key)).ShouldBe("ready");
        (await service.HandleAsync("push", Encoding.UTF8.GetBytes("not json"), "x", [])).Items.ShouldBeEmpty();
    }

    [Fact]
    public void References_are_found_on_word_boundaries()
    {
        WorkReferences.Find("claude/SW-12-thing").ShouldBe([("sw", 12)]);
        WorkReferences.Find("SW-1 and sw-1, SW-12").ShouldBe([("sw", 1), ("sw", 12)]);
        WorkReferences.Find("feature_sc-27870_x").ShouldBe([("sc", 27870)]);
        WorkReferences.Find("XSW-1a SW-1a SW-F12 v1-2x").ShouldBeEmpty();
        WorkReferences.Find("").ShouldBeEmpty();
    }
}
