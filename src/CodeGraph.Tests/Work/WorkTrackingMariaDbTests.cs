using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The work tracker against a real MariaDB (sc-27865): plans, keys, next_work retry and
/// Complete-only dependencies, claims, versioned updates, projects with repositories, and the
/// change feed covering projects, features and items.
/// </summary>
public sealed class WorkTrackingMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static string Key(JsonElement element) => element.GetProperty("key").GetString()!;

    private static object Plan(string project, string idempotencyKey) => new
    {
        project,
        idempotency_key = idempotencyKey,
        plan = new
        {
            features = new object[]
            {
                new { @ref = "core", title = "Core", documents = new[] { new { title = "Design", body = "The design." } } },
                new { @ref = "ui", title = "UI" }
            },
            items = new object[]
            {
                new { @ref = "schema", title = "Schema", status = "ready", feature = "core", repository = "sceneworks", priority = "high" },
                new { @ref = "store", title = "Store", status = "ready", feature = "core", blocked_by = new[] { "schema" } },
                new { @ref = "page", title = "Page", status = "ready", feature = "ui", blocked_by = new[] { "store", "schema" }, labels = new[] { "ui", "UI", "web" } }
            }
        }
    };

    [Fact]
    public async Task A_plan_is_imported_in_one_call_and_an_item_is_driven_from_ready_to_complete_with_every_write_once_in_order_in_the_feed()
    {
        var project = await db.NewProjectAsync("PL", "sceneworks");

        var import = await db.CallAsync(WorkTools.CreateWork, Plan(project, "plan-1"));

        import.GetProperty("created").GetBoolean().ShouldBeTrue();
        import.GetProperty("features").GetProperty("core").GetProperty("key").GetString().ShouldBe($"{project}-F1");
        import.GetProperty("features").GetProperty("ui").GetProperty("key").GetString().ShouldBe($"{project}-F2");
        var items = import.GetProperty("items");
        items.GetProperty("schema").GetProperty("key").GetString().ShouldBe($"{project}-1");
        items.GetProperty("store").GetProperty("key").GetString().ShouldBe($"{project}-2");
        items.GetProperty("page").GetProperty("key").GetString().ShouldBe($"{project}-3");

        var page = await db.CallAsync(WorkTools.GetWork, new { id = $"{project}-3" });
        page.GetProperty("blocked").GetBoolean().ShouldBeTrue();
        page.GetProperty("blocked_reason").GetString().ShouldBe("waiting");
        page.GetProperty("blocked_by").EnumerateArray().Select(Key).ShouldBe([$"{project}-1", $"{project}-2"]);
        page.GetProperty("labels").EnumerateArray().Select(label => label.GetString()).ShouldBe(["ui", "web"]);
        page.GetProperty("feature").GetProperty("key").GetString().ShouldBe($"{project}-F2");
        var feature = await db.CallAsync(WorkTools.GetWork, new { id = $"{project}-F1" });
        feature.GetProperty("documents").EnumerateArray().Single().GetProperty("title").GetString().ShouldBe("Design");

        // Only the unblocked item is handed out.
        var next = await db.CallAsync(WorkTools.NextWork, new { project, marker = "run-1" });
        var item = next.GetProperty("item");
        Key(item).ShouldBe($"{project}-1");
        next.GetProperty("reused").GetBoolean().ShouldBeFalse();
        item.GetProperty("claim").GetProperty("holder").GetString().ShouldBe("alice");

        var key = Key(item);
        var inProgress = await db.UpdateAsync(key, new { status = "in_progress", comment = "Starting." });
        inProgress.GetProperty("status").GetString().ShouldBe("in_progress");
        var inReview = await db.UpdateAsync(key, new { status = "in_review", add_links = new[] { new { kind = "pr", url = "https://github.com/o/r/pull/1" } } });
        inReview.GetProperty("links").EnumerateArray().Single().GetProperty("kind").GetString().ShouldBe("pr");
        var complete = await db.UpdateAsync(key, new { status = "complete" });
        complete.GetProperty("status").GetString().ShouldBe("complete");
        // Finishing an item releases its claim.
        complete.TryGetProperty("claim", out _).ShouldBeFalse();

        // Its dependent is now handed out (the other one still waits on it).
        var second = await db.CallAsync(WorkTools.NextWork, new { project, marker = "run-2" });
        Key(second.GetProperty("item")).ShouldBe($"{project}-2");

        var entries = await db.AllChangesAsync(project);
        entries.Select(entry => (entry.GetProperty("kind").GetString(), entry.GetProperty("entity_key").GetString())).ShouldBe(
        [
            ("project_created", project),
            ("feature_created", $"{project}-F1"),
            ("feature_created", $"{project}-F2"),
            ("item_created", $"{project}-1"),
            ("item_created", $"{project}-2"),
            ("item_created", $"{project}-3"),
            ("claimed", $"{project}-1"),
            ("item_updated", $"{project}-1"),
            ("item_updated", $"{project}-1"),
            ("item_updated", $"{project}-1"),
            ("claimed", $"{project}-2")
        ]);
        var sequences = entries.Select(entry => entry.GetProperty("sequence").GetInt64()).ToList();
        sequences.ShouldBe(sequences.Order().ToList());
        sequences.Distinct().Count().ShouldBe(sequences.Count);
        entries.ShouldAllBe(entry => entry.GetProperty("actor").GetProperty("name").GetString() == "alice");

        // The feed says what changed: the status move with its version, the comment, the link.
        var started = entries[7].GetProperty("changes").EnumerateArray().ToList();
        started.ShouldContain(change => change.GetProperty("field").GetString() == "status" && change.GetProperty("new").GetString() == "in_progress");
        started.ShouldContain(change => change.GetProperty("field").GetString() == "comment" && change.GetProperty("new").GetProperty("body").GetString() == "Starting.");
        entries[8].GetProperty("changes").EnumerateArray().ShouldContain(change => change.GetProperty("field").GetString() == "link");
        entries[5].GetProperty("changes").EnumerateArray().Count(change => change.GetProperty("field").GetString() == "blocked_by").ShouldBe(2);
    }

    [Fact]
    public async Task A_repeated_plan_key_creates_nothing_and_answers_the_first_import()
    {
        var project = await db.NewProjectAsync("RP", "inference");
        var plan = Plan(project, "plan-repeat");
        var planJson = JsonSerializer.Serialize(plan, WorkJson.Options).Replace("sceneworks", "inference");
        var arguments = JsonSerializer.Deserialize<JsonElement>(planJson);

        var first = await db.CallAsync(WorkTools.CreateWork, arguments);
        var feedAfterFirst = (await db.AllChangesAsync(project)).Count;
        var repeat = await db.CallAsync(WorkTools.CreateWork, arguments);

        repeat.GetProperty("created").GetBoolean().ShouldBeFalse();
        repeat.GetProperty("items").GetRawText().ShouldBe(first.GetProperty("items").GetRawText());
        repeat.GetProperty("features").GetRawText().ShouldBe(first.GetProperty("features").GetRawText());
        (await db.AllChangesAsync(project)).Count.ShouldBe(feedAfterFirst);
        var listed = await db.CallAsync(WorkTools.FindWork, new { project });
        listed.GetProperty("items").GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task A_plan_with_a_cycle_or_a_bad_ref_creates_nothing_and_takes_no_number()
    {
        var project = await db.NewProjectAsync("CY");

        var cycle = await db.RefusedAsync(WorkTools.CreateWork, new
        {
            project,
            idempotency_key = "cyclic",
            plan = new
            {
                items = new object[]
                {
                    new { @ref = "a", title = "A", blocked_by = new[] { "c" } },
                    new { @ref = "b", title = "B", blocked_by = new[] { "a" } },
                    new { @ref = "c", title = "C", blocked_by = new[] { "b" } }
                }
            }
        });
        cycle.Code.ShouldBe(WorkErrorCodes.Cycle);

        var badRef = await db.RefusedAsync(WorkTools.CreateWork, new
        {
            project,
            idempotency_key = "bad-ref",
            plan = new { items = new object[] { new { @ref = "a", title = "A", blocked_by = new[] { "nope" } } } }
        });
        badRef.Code.ShouldBe(WorkErrorCodes.InvalidArgument);

        var notOwned = await db.RefusedAsync(WorkTools.CreateWork, new
        {
            project,
            idempotency_key = "not-owned",
            plan = new { features = new[] { new { @ref = "f", title = "F" } }, items = new object[] { new { @ref = "a", title = "A", feature = "f", repository = "sceneworks" } } }
        });
        notOwned.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        notOwned.Message.ShouldContain("not an owned repository");

        var noKey = await db.RefusedAsync(WorkTools.CreateWork, new { project, plan = new { items = new object[] { new { @ref = "a", title = "A" } } } });
        noKey.Message.ShouldContain("idempotency_key");

        (await db.CallAsync(WorkTools.FindWork, new { project })).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await db.CallAsync(WorkTools.FindWork, new { project, kind = "feature" })).GetProperty("items").GetArrayLength().ShouldBe(0);
        // Nothing was allocated: the next item is number 1, the next feature F1.
        Key(await db.NewItemAsync(project, "First")).ShouldBe($"{project}-1");
        var feature = await db.CallAsync(WorkTools.CreateWork, new { project, feature = new { title = "First feature" } });
        Key(feature.GetProperty("feature")).ShouldBe($"{project}-F1");
    }

    [Fact]
    public async Task Next_work_retried_returns_the_same_claim_by_key_and_by_marker_and_another_claims_a_second()
    {
        var project = await db.NewProjectAsync("NW");
        await db.NewItemAsync(project, "One", priority: "low");
        await db.NewItemAsync(project, "Two", priority: "critical");
        await db.NewItemAsync(project, "Three");

        var first = await db.CallAsync(WorkTools.NextWork, new { project, marker = "run-1", idempotency_key = "alloc-1" });
        // Priority first: the critical item.
        Key(first.GetProperty("item")).ShouldBe($"{project}-2");

        // The response was lost: the same key answers the same allocation.
        var byKey = await db.CallAsync(WorkTools.NextWork, new { project, marker = "run-1", idempotency_key = "alloc-1" });
        Key(byKey.GetProperty("item")).ShouldBe($"{project}-2");
        byKey.GetProperty("reused").GetBoolean().ShouldBeTrue();

        // Without a key, and without another: the caller's existing claim under its marker.
        var byMarker = await db.CallAsync(WorkTools.NextWork, new { project, marker = "run-1" });
        Key(byMarker.GetProperty("item")).ShouldBe($"{project}-2");
        byMarker.GetProperty("reused").GetBoolean().ShouldBeTrue();

        var another = await db.CallAsync(WorkTools.NextWork, new { project, marker = "run-1", another = true });
        Key(another.GetProperty("item")).ShouldBe($"{project}-3");

        // Another identity with the same marker is another caller.
        var bob = await db.CallAsync(WorkTools.NextWork, new { project, marker = "run-1" }, user: "bob");
        Key(bob.GetProperty("item")).ShouldBe($"{project}-1");

        var none = await db.CallAsync(WorkTools.NextWork, new { project, marker = "run-3" }, user: "carol");
        none.TryGetProperty("item", out _).ShouldBeFalse();
        none.GetProperty("reason").GetString().ShouldBe("no_ready_work");

        // One claimed entry per allocation: the retries wrote nothing.
        (await db.AllChangesAsync(project)).Count(entry => entry.GetProperty("kind").GetString() == "claimed").ShouldBe(3);
    }

    [Fact]
    public async Task A_closed_blocker_keeps_its_dependents_out_of_next_work_and_only_complete_releases_them()
    {
        var project = await db.NewProjectAsync("CL");
        var auth = await db.NewItemAsync(project, "Implement authentication");
        var api = await db.NewItemAsync(project, "Expose authenticated API", blockedBy: [Key(auth)]);

        await db.UpdateAsync(Key(auth), new { status = "closed" });

        var dependent = await db.CallAsync(WorkTools.GetWork, new { id = Key(api) });
        dependent.GetProperty("blocked").GetBoolean().ShouldBeTrue();
        dependent.GetProperty("blocked_reason").GetString().ShouldBe("cancelled_prerequisite");
        var next = await db.CallAsync(WorkTools.NextWork, new { project, marker = "m" });
        next.TryGetProperty("item", out _).ShouldBeFalse();
        (await db.CallAsync(WorkTools.FindWork, new { project, ready = true })).GetProperty("items").GetArrayLength().ShouldBe(0);

        await db.UpdateAsync(Key(auth), new { status = "complete" });

        var released = await db.CallAsync(WorkTools.NextWork, new { project, marker = "m" });
        Key(released.GetProperty("item")).ShouldBe(Key(api));
    }

    [Fact]
    public async Task Updates_are_guarded_by_expected_version_and_a_stale_one_is_refused_with_the_current_state()
    {
        var project = await db.NewProjectAsync("VR");
        var item = await db.NewItemAsync(project, "Versioned");
        var version = item.GetProperty("version").GetInt64();

        var renamed = await db.CallAsync(WorkTools.UpdateWork, new { id = Key(item), expected_version = version, title = "Renamed" });
        renamed.GetProperty("item").GetProperty("version").GetInt64().ShouldBe(version + 1);

        var stale = await db.RefusedAsync(WorkTools.UpdateWork, new { id = Key(item), expected_version = version, title = "Lost" });
        stale.Code.ShouldBe(WorkErrorCodes.Conflict);
        var current = JsonSerializer.SerializeToElement(stale.Current, stale.Current!.GetType(), WorkJson.Options);
        current.GetProperty("title").GetString().ShouldBe("Renamed");
        current.GetProperty("version").GetInt64().ShouldBe(version + 1);
        (await db.CallAsync(WorkTools.GetWork, new { id = Key(item) })).GetProperty("title").GetString().ShouldBe("Renamed");
    }

    [Fact]
    public async Task Update_work_refuses_fields_of_another_kind_and_clear_conflicts()
    {
        var project = await db.NewProjectAsync("KF");
        var item = await db.NewItemAsync(project, "Item");

        var onItem = await db.RefusedAsync(WorkTools.UpdateWork, new { id = Key(item), expected_version = 1, goals = new[] { "g" } });
        onItem.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        onItem.Message.ShouldContain("takes no goals");
        onItem.Message.ShouldContain("add_blocked_by");

        var onProject = await db.RefusedAsync(WorkTools.UpdateWork, new { id = project, expected_version = 1, status = "ready" });
        onProject.Message.ShouldContain("takes no status");

        var both = await db.RefusedAsync(WorkTools.UpdateWork, new { id = Key(item), expected_version = 1, description = "d", clear = new[] { "description" } });
        both.Code.ShouldBe(WorkErrorCodes.InvalidArgument);

        var nothing = await db.RefusedAsync(WorkTools.UpdateWork, new { id = Key(item), expected_version = 1 });
        nothing.Message.ShouldContain("nothing to change");

        var empty = await db.RefusedAsync(WorkTools.UpdateWork, new { id = Key(item), expected_version = 1, description = "  " });
        empty.Message.ShouldContain("clear");
    }

    [Fact]
    public async Task Claims_through_update_work_follow_identity_and_marker()
    {
        var project = await db.NewProjectAsync("CM");
        var item = await db.NewItemAsync(project, "Claimable");
        var key = Key(item);

        var claimed = await db.UpdateAsync(key, new { claim = new { marker = "a-run", ttl_seconds = 600 } });
        claimed.GetProperty("claim").GetProperty("marker").GetString().ShouldBe("a-run");

        var taken = await Should.ThrowAsync<WorkException>(() => db.UpdateAsync(key, new { claim = new { marker = "b-run" } }, user: "bob"));
        taken.Code.ShouldBe(WorkErrorCodes.AlreadyClaimed);
        var otherMarker = await Should.ThrowAsync<WorkException>(() => db.UpdateAsync(key, new { claim = new { marker = "other" } }));
        otherMarker.Code.ShouldBe(WorkErrorCodes.AlreadyClaimed);

        // Claims are advisory: another identity can still change the item.
        (await db.UpdateAsync(key, new { priority = "high" }, user: "bob")).GetProperty("priority").GetString().ShouldBe("high");

        var renewed = await db.UpdateAsync(key, new { renew_claim = new { marker = "a-run", ttl_seconds = 1200 } });
        renewed.GetProperty("claim").GetProperty("expires_at").GetDateTime()
            .ShouldBeGreaterThan(claimed.GetProperty("claim").GetProperty("expires_at").GetDateTime());

        var released = await db.UpdateAsync(key, new { release_claim = new { marker = "a-run" } });
        released.TryGetProperty("claim", out _).ShouldBeFalse();
        var notHeld = await Should.ThrowAsync<WorkException>(() => db.UpdateAsync(key, new { release_claim = new { marker = "a-run" } }));
        notHeld.Code.ShouldBe(WorkErrorCodes.Conflict);

        var feed = await db.AllChangesAsync(project);
        feed.Last().GetProperty("changes").EnumerateArray()
            .ShouldContain(change => change.GetProperty("field").GetString() == "claim_holder" && change.GetProperty("new").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_lapsed_claim_returns_the_item_to_the_queue()
    {
        var project = await db.NewProjectAsync("LP");
        var item = await db.NewItemAsync(project, "Short claim");

        var first = await db.CallAsync(WorkTools.NextWork, new { project, marker = "brief", ttl_seconds = 1 });
        Key(first.GetProperty("item")).ShouldBe(Key(item));
        (await db.CallAsync(WorkTools.NextWork, new { project, marker = "x" }, user: "bob")).TryGetProperty("item", out _).ShouldBeFalse();

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var second = await db.CallAsync(WorkTools.NextWork, new { project, marker = "x" }, user: "bob");
        Key(second.GetProperty("item")).ShouldBe(Key(item));
        second.GetProperty("item").GetProperty("claim").GetProperty("holder").GetString().ShouldBe("bob");
    }

    [Fact]
    public async Task Dependencies_refuse_cycles_and_stay_within_a_project()
    {
        var project = await db.NewProjectAsync("DP");
        var a = Key(await db.NewItemAsync(project, "A"));
        var b = Key(await db.NewItemAsync(project, "B", blockedBy: [a]));
        var c = Key(await db.NewItemAsync(project, "C", blockedBy: [b]));

        (await Should.ThrowAsync<WorkException>(() => db.UpdateAsync(a, new { add_blocked_by = new[] { c } }))).Code.ShouldBe(WorkErrorCodes.Cycle);
        (await Should.ThrowAsync<WorkException>(() => db.UpdateAsync(a, new { add_blocked_by = new[] { a } }))).Code.ShouldBe(WorkErrorCodes.Cycle);

        var other = await db.NewProjectAsync("OT");
        var foreign = Key(await db.NewItemAsync(other, "Elsewhere"));
        (await Should.ThrowAsync<WorkException>(() => db.UpdateAsync(a, new { add_blocked_by = new[] { foreign } }))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);

        var removed = await db.UpdateAsync(c, new { remove_blocked_by = new[] { b } });
        removed.GetProperty("blocked").GetBoolean().ShouldBeFalse();
        // Adding an edge that exists changes nothing but the version.
        var again = await db.UpdateAsync(b, new { add_blocked_by = new[] { a } });
        again.GetProperty("blocked_by").GetArrayLength().ShouldBe(1);
        (await db.UpdateAsync(a, new { add_blocked_by = new[] { c } })).GetProperty("blocked_by").EnumerateArray().Select(Key).ShouldBe([c]);
    }

    [Fact]
    public async Task Projects_hold_goals_and_owned_or_dependency_repositories_with_one_owner_per_repository()
    {
        var key = "PR" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var created = await db.CallAsync(WorkTools.CreateWork, new
        {
            new_project = new
            {
                key = key.ToLowerInvariant(),
                name = "Projects",
                goals = new[] { "Ship it" },
                repositories = new object[] { new { repository = "codegraph", role = "owned" }, new { repository = "mlx-rs", role = "dependency" } }
            },
            idempotency_key = $"project-{key}"
        });
        var project = created.GetProperty("project");
        project.GetProperty("key").GetString().ShouldBe(key);
        project.GetProperty("goals").EnumerateArray().Single().GetString().ShouldBe("Ship it");

        var repeat = await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key = "ZZZZ", name = "Other" }, idempotency_key = $"project-{key}" });
        repeat.GetProperty("created").GetBoolean().ShouldBeFalse();
        repeat.GetProperty("project").GetProperty("key").GetString().ShouldBe(key);

        // codegraph is owned by this project, so no other project can own it; mlx-rs can be a dependency of many.
        var second = "PS" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        (await db.RefusedAsync(WorkTools.CreateWork, new { new_project = new { key = second, name = "Two", repositories = new[] { new { repository = "codegraph", role = "owned" } } } }))
            .Code.ShouldBe(WorkErrorCodes.Conflict);
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key = second, name = "Two", repositories = new[] { new { repository = "mlx-rs", role = "dependency" } } } });
        (await db.RefusedAsync(WorkTools.CreateWork, new { new_project = new { key = second, name = "Dup" } })).Code.ShouldBe(WorkErrorCodes.Conflict);
        (await db.RefusedAsync(WorkTools.CreateWork, new { new_project = new { key = "PX" + Guid.NewGuid().ToString("N")[..4], name = "Unknown", repositories = new[] { new { repository = "no-such-repo", role = "owned" } } } }))
            .Code.ShouldBe(WorkErrorCodes.InvalidArgument);

        // An item's repository must be an owned one.
        (await db.RefusedAsync(WorkTools.CreateWork, new { project = key, item = new { title = "Dep work", repository = "mlx-rs" } })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        var item = (await db.CallAsync(WorkTools.CreateWork, new { project = key, item = new { title = "Owned work", repository = "codegraph" } })).GetProperty("item");

        // An owned repository that items still name cannot be dropped.
        var dropped = await Should.ThrowAsync<WorkException>(() => db.UpdateAsync(key, new { repositories = new[] { new { repository = "mlx-rs", role = "dependency" } } }));
        dropped.Code.ShouldBe(WorkErrorCodes.Conflict);
        await db.UpdateAsync(Key(item), new { clear = new[] { "repository" } });
        var updated = await db.UpdateAsync(key, new { name = "Renamed", repositories = new[] { new { repository = "mlx-rs", role = "dependency" } } });
        updated.GetProperty("repositories").EnumerateArray().Single().GetProperty("role").GetString().ShouldBe("dependency");

        var feed = await db.AllChangesAsync(key);
        feed.First().GetProperty("kind").GetString().ShouldBe("project_created");
        var projectUpdate = feed.Last();
        projectUpdate.GetProperty("kind").GetString().ShouldBe("project_updated");
        projectUpdate.GetProperty("changes").EnumerateArray().Select(change => change.GetProperty("field").GetString())
            .ShouldBe(["name", "repositories", "version"], ignoreOrder: true);
    }

    [Fact]
    public async Task Features_are_versioned_and_their_document_changes_are_in_the_feed()
    {
        var project = await db.NewProjectAsync("FT");
        var feature = (await db.CallAsync(WorkTools.CreateWork, new { project, feature = new { title = "Feature", documents = new[] { new { title = "Plan", body = "v1" } } } })).GetProperty("feature");
        var documentId = feature.GetProperty("documents")[0].GetProperty("id").GetString();

        var updated = await db.UpdateAsync(Key(feature), new
        {
            title = "Feature, renamed",
            update_documents = new[] { new { id = documentId, body = "v2" } },
            add_documents = new[] { new { title = "Notes", body = "n" } }
        });

        updated.GetProperty("version").GetInt64().ShouldBe(2);
        updated.GetProperty("documents").EnumerateArray().Select(document => document.GetProperty("body").GetString()).ShouldBe(["v2", "n"]);
        var entry = (await db.AllChangesAsync(project)).Last();
        entry.GetProperty("kind").GetString().ShouldBe("feature_updated");
        entry.GetProperty("changes").EnumerateArray().Count(change => change.GetProperty("field").GetString() == "document").ShouldBe(2);
    }

    [Fact]
    public async Task Find_work_lists_small_rows_by_default_full_on_request_and_pages_by_key()
    {
        var project = await db.NewProjectAsync("FW");
        for (var index = 1; index <= 5; index++)
            await db.NewItemAsync(project, $"Item {index}", status: index == 5 ? "closed" : "ready");

        var firstPage = await db.CallAsync(WorkTools.FindWork, new { project, limit = 2 });
        var rows = firstPage.GetProperty("items").EnumerateArray().ToList();
        rows.Select(Key).ShouldBe([$"{project}-1", $"{project}-2"]);
        rows[0].TryGetProperty("comments", out _).ShouldBeFalse();
        rows[0].GetProperty("version").GetInt64().ShouldBe(1);

        var secondPage = await db.CallAsync(WorkTools.FindWork, new { project, limit = 2, cursor = firstPage.GetProperty("next_cursor").GetString() });
        secondPage.GetProperty("items").EnumerateArray().Select(Key).ShouldBe([$"{project}-3", $"{project}-4"]);
        // Closed items only when asked for.
        var thirdPage = await db.CallAsync(WorkTools.FindWork, new { project, limit = 2, cursor = secondPage.GetProperty("next_cursor").GetString() });
        thirdPage.GetProperty("items").GetArrayLength().ShouldBe(0);
        (await db.CallAsync(WorkTools.FindWork, new { project, status = new[] { "closed" } })).GetProperty("items").EnumerateArray().Select(Key).ShouldBe([$"{project}-5"]);

        var full = await db.CallAsync(WorkTools.FindWork, new { project, detail = "full", text = "item 3" });
        full.GetProperty("items").EnumerateArray().Single().GetProperty("comments").GetArrayLength().ShouldBe(0);

        var projects = await db.CallAsync(WorkTools.FindWork, new { kind = "project", limit = 200 });
        projects.GetProperty("items").EnumerateArray().ShouldContain(row => row.GetProperty("key").GetString() == project);
        (await db.RefusedAsync(WorkTools.FindWork, new { kind = "project", status = new[] { "ready" } })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task Get_work_resolves_keys_and_ids_and_answers_not_found()
    {
        var project = await db.NewProjectAsync("GW");
        var item = await db.NewItemAsync(project, "Resolvable");

        (await db.CallAsync(WorkTools.GetWork, new { id = item.GetProperty("id").GetString() })).GetProperty("key").GetString().ShouldBe(Key(item));
        (await db.CallAsync(WorkTools.GetWork, new { id = Key(item).ToLowerInvariant() })).GetProperty("title").GetString().ShouldBe("Resolvable");
        var projectView = await db.CallAsync(WorkTools.GetWork, new { id = project });
        projectView.GetProperty("kind").GetString().ShouldBe("project");
        projectView.GetProperty("item_counts").GetProperty("ready").GetInt32().ShouldBe(1);

        (await db.RefusedAsync(WorkTools.GetWork, new { id = $"{project}-999" })).Code.ShouldBe(WorkErrorCodes.NotFound);
        (await db.RefusedAsync(WorkTools.GetWork, new { id = Guid.NewGuid().ToString() })).Code.ShouldBe(WorkErrorCodes.NotFound);
    }

    [Fact]
    public async Task The_change_feed_cursor_starts_at_the_head_long_polls_and_refuses_a_cursor_ahead_of_it()
    {
        var project = await db.NewProjectAsync("CH");
        var head = await db.CallAsync(WorkTools.Changes, new { project });
        head.GetProperty("items").GetArrayLength().ShouldBe(0);
        var cursor = head.GetProperty("next_cursor").GetString();

        var waiting = db.CallAsync(WorkTools.Changes, new { project, cursor, wait_seconds = 10 });
        await Task.Delay(300);
        waiting.IsCompleted.ShouldBeFalse();
        await db.NewItemAsync(project, "Wakes the reader");
        var woke = await waiting.WaitAsync(TimeSpan.FromSeconds(9));
        woke.GetProperty("items").EnumerateArray().Single().GetProperty("kind").GetString().ShouldBe("item_created");

        var again = await db.CallAsync(WorkTools.Changes, new { project, cursor });
        again.GetProperty("items").GetArrayLength().ShouldBe(1);

        var ahead = Convert.ToBase64String("ch:999999999"u8.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        (await db.RefusedAsync(WorkTools.Changes, new { cursor = ahead })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await db.RefusedAsync(WorkTools.Changes, new { cursor, from_start = true })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        (await db.RefusedAsync(WorkTools.Changes, new { cursor = "not-a-cursor" })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task A_long_text_is_cut_in_the_feed_but_kept_whole_on_the_item()
    {
        var project = await db.NewProjectAsync("TX");
        var description = new string('x', WorkLimits.MaxStoredChangeText + 100);

        var item = (await db.CallAsync(WorkTools.CreateWork, new { project, item = new { title = "Long", description } })).GetProperty("item");

        item.GetProperty("description").GetString()!.Length.ShouldBe(description.Length);
        var change = (await db.AllChangesAsync(project)).Last().GetProperty("changes").EnumerateArray()
            .Single(candidate => candidate.GetProperty("field").GetString() == "description");
        change.GetProperty("new").GetString()!.Length.ShouldBe(WorkLimits.MaxStoredChangeText);
        change.GetProperty("new_truncated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_write_that_cannot_get_the_change_feed_lock_is_refused_busy_and_stores_nothing()
    {
        var project = await db.NewProjectAsync("BZ");
        var before = (await db.AllChangesAsync(project)).Count;
        await using var holder = new MySqlConnection(db.ConnectionString);
        await holder.OpenAsync();
        var lockName = CodeGraph.Data.MariaDb.MySqlWorkStore.ServerLockName(holder.Database, CodeGraph.Data.MariaDb.MySqlWorkStore.ChangeLockName);
        (await holder.ExecuteScalarAsync<long>("SELECT GET_LOCK(@lockName, 1)", new { lockName })).ShouldBe(1);
        try
        {
            var busy = await db.RefusedAsync(WorkTools.CreateWork, new { project, item = new { title = "Waits" } });
            busy.Code.ShouldBe(WorkErrorCodes.Busy);
        }
        finally
        {
            await holder.ExecuteScalarAsync<long?>("SELECT RELEASE_LOCK(@lockName)", new { lockName });
        }

        (await db.CallAsync(WorkTools.FindWork, new { project })).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await db.AllChangesAsync(project)).Count.ShouldBe(before);
        // The number was not used up either.
        Key(await db.NewItemAsync(project, "After")).ShouldBe($"{project}-1");
    }
}
