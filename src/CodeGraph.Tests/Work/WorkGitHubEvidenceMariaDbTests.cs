using System.Text;
using System.Text.Json;
using CodeGraph.Data.MariaDb;
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

    // The tests below are about merges, closes and reviews; pushes have a test of their own.
    private async Task<IReadOnlyList<GitHubEvidenceView>> WithoutPushesAsync(Guid id) =>
        (await db.Store.ListGitHubEvidenceAsync(id, 10)).Where(entry => entry.Kind != "push").ToList();

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
        (await db.Store.ListGitHubEvidenceAsync(id, 10)).Select(entry => entry.Kind).OrderBy(kind => kind).ToList().ShouldBe(["merge", "push"]);
    }

    [Fact]
    public async Task A_push_stores_its_head_SHA_and_commits_and_a_redelivery_stays_one_row()
    {
        var (_, key, id) = await NewReadyItemAsync();
        var pull = NewPullRequest();
        var delivery = Guid.NewGuid().ToString();

        await SendAsync("push", "push", key, pull, delivery);
        await SendAsync("push", "push", key, pull, delivery);
        await SendAsync("push", "push", key, pull, Guid.NewGuid().ToString());

        var push = (await db.Store.ListGitHubEvidenceAsync(id, 10)).Single();
        push.Kind.ShouldBe("push");
        push.ExternalId.ShouldBe("1111111111111111111111111111111111111111");
        push.Author.ShouldBe("claude-agent");
        push.Url!.ShouldEndWith("/commit/1111111111111111111111111111111111111111");
        push.Body!.Split('\n').ShouldBe(["0000000000000000000000000000000000000abc Add the evidence table", "1111111111111111111111111111111111111111 Record GitHub evidence"]);
        push.AttemptId.ShouldBe((await db.Store.GetItemAsync(id, false))!.Attempts?.FirstOrDefault()?.Id);
        Status(await GetAsync(key)).ShouldBe("in_progress");
    }

    [Fact]
    public async Task A_new_push_is_fed_like_a_review_without_bumping_the_version_and_a_repeat_changes_nothing()
    {
        var (project, key, id) = await NewReadyItemAsync();
        var pull = NewPullRequest();
        await SendAsync("push", "push", key, pull);
        var version = (await GetAsync(key)).GetProperty("version").GetInt64();
        var entries = (await db.AllChangesAsync(project)).Count(entry => entry.GetProperty("entity_key").GetString() == key);
        string Newer(string text) => text.Replace("1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222");

        var second = await SendAsync("push", "push", key, pull, edit: Newer);
        second.Items.Single().Result.ShouldBe("applied");
        (await GetAsync(key)).GetProperty("version").GetInt64().ShouldBe(version);
        (await db.AllChangesAsync(project)).Count(entry => entry.GetProperty("entity_key").GetString() == key).ShouldBe(entries + 1);
        (await db.Store.ListGitHubEvidenceAsync(id, 10)).Count(entry => entry.Kind == "push").ShouldBe(2);

        var repeat = await SendAsync("push", "push", key, pull, edit: Newer);
        repeat.Items.Single().Result.ShouldBe("unchanged");
        (await db.AllChangesAsync(project)).Count(entry => entry.GetProperty("entity_key").GetString() == key).ShouldBe(entries + 1);
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
        var evidence = (await WithoutPushesAsync(id)).Single();
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
        await SendAsync("pull_request", "pull_request.opened", key, pull);
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
        var evidence = await WithoutPushesAsync(id);
        evidence.Select(entry => entry.Kind).ShouldBe(["merge", "review", "pr_closed"], ignoreOrder: true);
        evidence.ShouldAllBe(entry => entry.Superseded);
        // Superseded evidence is recorded and fed, but the item itself did not change: no version bump.
        item.GetProperty("version").GetInt64().ShouldBe(before.GetProperty("version").GetInt64());
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
        var evidence = await WithoutPushesAsync(id);
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
        (await WithoutPushesAsync(id)).Count.ShouldBe(3);

        // An edited review is the same evidence, updated once.
        await SendAsync("pull_request_review", "pull_request_review.submitted", key, pull,
            edit: text => text.Replace("\"submitted\"", "\"edited\"").Replace("do not restate", "never restate"));
        (await WithoutPushesAsync(id)).Single(entry => entry.Kind == "review").Body!.ShouldContain("never restate");

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
    public async Task A_PR_closed_unmerged_clears_the_claim_but_leaves_the_sessions_claim_open_so_its_next_write_is_refused_claim_lost()
    {
        var project = "GS" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        await db.CallAsync(WorkTools.CreateWork, new
        {
            new_project = new { key = project, name = "Session project", repositories = new[] { new { repository = "sceneworks", role = "dependency" } } }
        });
        var item = Key(await db.NewItemAsync(project, "Held by a session"));
        var started = await db.CallAsync(WorkTools.StartSession, new { project, harness = "claude-code", host = "dev-mac" }, "michael");
        var session = started.GetProperty("session").GetProperty("id").GetString()!;
        var current = await GetAsync(item);
        await db.CallAsync(WorkTools.UpdateWork, new
        {
            id = item, expected_version = current.GetProperty("version").GetInt64(), session_id = session, status = "in_progress", claim = new { }
        }, "michael");
        var pull = NewPullRequest();
        await SendAsync("push", "push", item, pull);
        await SendAsync("pull_request", "pull_request.opened", item, pull);
        Status(await GetAsync(item)).ShouldBe("in_review");

        await SendAsync("pull_request", "pull_request.closed.unmerged", item, pull);

        var returned = await GetAsync(item);
        Status(returned).ShouldBe("ready");
        returned.TryGetProperty("claim", out _).ShouldBeFalse();
        var lost = await db.RefusedAsync(WorkTools.UpdateWork, new
        {
            id = item, expected_version = returned.GetProperty("version").GetInt64(), session_id = session, status = "in_progress"
        }, "michael");
        lost.Code.ShouldBe(WorkErrorCodes.ClaimLost);
    }

    [Fact]
    public async Task A_stale_PR_of_an_abandoned_attempt_closing_moves_nothing_while_a_later_attempt_is_In_Review()
    {
        var (_, key, id) = await NewReadyItemAsync();
        var first = NewPullRequest();
        var second = NewPullRequest();
        await db.UpdateAsync(key, new { status = "in_progress", claim = new { marker = "run-1" } }, "michael");
        await SendAsync("push", "push", key, first);
        await SendAsync("pull_request", "pull_request.opened", key, first);
        // The first attempt ends abandoned and a second agent takes the item with a PR of its own.
        await db.UpdateAsync(key, new { release_claim = new { marker = "run-1" } }, "michael");
        await db.UpdateAsync(key, new { status = "in_progress", claim = new { marker = "run-2" } }, "bob");
        await SendAsync("pull_request", "pull_request.opened", key, second, edit: text => text.Replace("claude/", "other/"));
        Status(await GetAsync(key)).ShouldBe("in_review");

        var stale = await SendAsync("pull_request", "pull_request.closed.unmerged", key, first);

        stale.Items.Single().To.ShouldBe(WorkStatus.InReview);
        var item = await GetAsync(key);
        Status(item).ShouldBe("in_review");
        item.GetProperty("claim").GetProperty("holder").GetString().ShouldBe("bob");
        item.GetProperty("attempts").EnumerateArray().Select(attempt => attempt.GetProperty("outcome").GetString()).ShouldBe(["active", "abandoned"]);
        (await WithoutPushesAsync(id)).ShouldContain(entry => entry.Kind == "pr_closed");

        // The current attempt's own PR does return it.
        (await SendAsync("pull_request", "pull_request.closed.unmerged", key, second)).Items.Single().To.ShouldBe(WorkStatus.Ready);
    }

    [Fact]
    public async Task A_foreign_PR_that_mentions_the_key_and_closes_unmerged_moves_nothing()
    {
        var (_, key, id) = await NewReadyItemAsync("in_review");
        await db.UpdateAsync(key, new { claim = new { marker = "run-1" } }, "michael");

        var result = await SendAsync("pull_request", "pull_request.closed.unmerged", key, NewPullRequest());

        result.Items.Single().To.ShouldBe(WorkStatus.InReview);
        var item = await GetAsync(key);
        Status(item).ShouldBe("in_review");
        item.GetProperty("claim").GetProperty("holder").GetString().ShouldBe("michael");
        item.GetProperty("attempts").EnumerateArray().Single().GetProperty("outcome").GetString().ShouldBe("active");
        (await WithoutPushesAsync(id)).Single().Kind.ShouldBe("pr_closed");
    }

    [Fact]
    public async Task Evidence_that_changes_no_link_or_status_is_fed_without_bumping_the_version_so_a_bot_comment_edit_causes_no_conflict()
    {
        var (project, key, id) = await NewReadyItemAsync();
        var pull = NewPullRequest();
        await SendAsync("push", "push", key, pull);
        await SendAsync("pull_request", "pull_request.opened", key, pull);
        var version = (await GetAsync(key)).GetProperty("version").GetInt64();
        var feed = (await db.AllChangesAsync(project)).Count;

        await SendAsync("pull_request_review_comment", "pull_request_review_comment.created", key, pull);
        await SendAsync("pull_request_review_comment", "pull_request_review_comment.created", key, pull,
            edit: text => text.Replace("\"created\"", "\"edited\"").Replace("Possible null dereference here.", "Possible null dereference, now fixed."));

        // Both are in the feed once each, attributed to the bot; the version did not move.
        var entries = (await db.AllChangesAsync(project)).Skip(feed).ToList();
        entries.Count.ShouldBe(2);
        entries.ShouldAllBe(entry => entry.GetProperty("actor").GetProperty("name").GetString() == "github:coderabbitai[bot]");
        entries.ShouldAllBe(entry => entry.GetProperty("changes").EnumerateArray().All(change => change.GetProperty("field").GetString() == "evidence"));
        (await GetAsync(key)).GetProperty("version").GetInt64().ShouldBe(version);
        (await WithoutPushesAsync(id)).Single().Body.ShouldBe("Possible null dereference, now fixed.");

        // The agent working on the item writes with the version it read.
        var updated = await db.CallAsync(WorkTools.UpdateWork, new { id = key, expected_version = version, comment = "Addressed the review." }, "alice");
        updated.GetProperty("item").GetProperty("version").GetInt64().ShouldBe(version + 1);
    }

    [Fact]
    public async Task The_push_actor_is_the_sender_not_the_pusher_name()
    {
        var (project, key, _) = await NewReadyItemAsync();

        await SendAsync("push", "push", key, NewPullRequest(), edit: text => text.Replace("\"name\": \"claude-agent\"", "\"name\": \"someone-else\""));

        (await db.AllChangesAsync(project)).Last(entry => entry.GetProperty("entity_key").GetString() == key)
            .GetProperty("actor").GetProperty("name").GetString().ShouldBe("github:claude-agent");
    }

    [Fact]
    public async Task An_alias_cannot_be_an_item_key_and_leading_zeros_do_not_make_another_alias()
    {
        var (_, key, id) = await NewReadyItemAsync();
        var number = Random.Shared.Next(100000, 999999);
        var actor = new WorkActor("importer");

        (await Should.ThrowAsync<WorkException>(() => db.Store.AddItemAliasesAsync(id, [key], actor))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await Should.ThrowAsync<WorkException>(() => db.Store.AddItemAliasesAsync(id, [key.ToLowerInvariant()], actor))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await Should.ThrowAsync<WorkException>(() => db.Store.AddItemAliasesAsync(id, ["sc-0"], actor))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);

        var padded = $"SC-00{number}";
        await db.Store.AddItemAliasesAsync(id, [padded], actor);
        var version = (await db.Store.AddItemAliasesAsync(id, [$"sc-{number}"], actor)).Version;
        (await db.Store.AddItemAliasesAsync(id, [$"sc-000{number}"], actor)).Version.ShouldBe(version);
        (await db.Store.ResolveAsync($"sc-{number}"))!.Id.ShouldBe(id);
        (await db.Store.ResolveAsync($"SC-000{number}"))!.Id.ShouldBe(id);

        // A branch named with the padded form matches the one stored alias.
        var result = await SendAsync("push", "push", $"sc-0{number}", NewPullRequest());
        result.Items.Single().Key.ShouldBe(key);
    }

    [Fact]
    public async Task Transient_failures_are_retried_three_times_and_other_failures_are_not()
    {
        var calls = 0;
        var paused = new List<TimeSpan>();
        Task Pause(TimeSpan wait) { paused.Add(wait); return Task.CompletedTask; }

        var recovered = await MySqlWorkStore.RetryTransientAsync(() =>
            ++calls < 3 ? throw WorkException.Busy("locked") : Task.FromResult("done"), 3, Pause, default);
        recovered.ShouldBe("done");
        calls.ShouldBe(3);
        paused.Count.ShouldBe(2);

        calls = 0;
        (await Should.ThrowAsync<WorkException>(() => MySqlWorkStore.RetryTransientAsync<string>(() => { calls++; throw WorkException.Busy("locked"); }, 3, Pause, default)))
            .Code.ShouldBe(WorkErrorCodes.Busy);
        calls.ShouldBe(3);

        calls = 0;
        await Should.ThrowAsync<WorkException>(() => MySqlWorkStore.RetryTransientAsync<string>(() => { calls++; throw WorkException.Invalid("no"); }, 3, Pause, default));
        calls.ShouldBe(1);
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
