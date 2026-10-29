using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Feature planning, re-planning and completion against a real MariaDB (sc-27868): sync_plan
/// previews bound to versions (concurrent confirms, staleness, idempotent confirm), pivots that
/// supersede attempts and notify holders, feature completion by requirement coverage, Complete
/// only by an explicit update with an optional acceptance report, the get_work brief, unmet
/// acceptance in recovery, and plans at the 200-item limit.
/// </summary>
public sealed class WorkPlanningMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static readonly TimeSpan PastIdle = TimeSpan.FromSeconds(WorkDatabase.IdleSeconds + 5);

    private static string Key(JsonElement element) => element.GetProperty("key").GetString()!;

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IEnumerable<string> Strings(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.EnumerateArray().Select(entry => entry.GetString()!) : [];

    /// <summary>A feature (E1, E2) with three items: a (E1), b (E2, blocked by a), c (blocked by b).</summary>
    private async Task<(string Project, string Feature, Dictionary<string, string> Items)> NewPlanAsync(string prefix)
    {
        var project = await db.NewProjectAsync(prefix);
        var plan = await db.CallAsync(WorkTools.CreateWork, new
        {
            project,
            idempotency_key = $"plan-{project}",
            plan = new
            {
                features = new[]
                {
                    new
                    {
                        @ref = "f",
                        title = "Planning",
                        goals = new[] { "Re-plan safely" },
                        requirements = new[] { new { id = "E1", text = "Strict contract" }, new { id = "E2", text = "Change feed" } },
                        acceptance_tests = new[] { "Two agents confirm previews concurrently" }
                    }
                },
                items = new object[]
                {
                    new { @ref = "a", title = "Store", feature = "f", status = "ready", inherits = new[] { "E1" }, acceptance_criteria = new[] { new { text = "Writes return state" } } },
                    new { @ref = "b", title = "Feed", feature = "f", status = "ready", inherits = new[] { "E2" }, blocked_by = new[] { "a" } },
                    new { @ref = "c", title = "Docs", feature = "f", status = "ready", blocked_by = new[] { "b" } }
                }
            }
        });
        var items = plan.GetProperty("items").EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetProperty("key").GetString()!);
        return (project, plan.GetProperty("features").GetProperty("f").GetProperty("key").GetString()!, items);
    }

    private async Task<string> StartAsync(string project, string harness, string user = "michael")
    {
        var started = await db.CallAsync(WorkTools.StartSession, new { project, harness, host = "dev-mac", branch = $"{harness}/work" }, user);
        return started.GetProperty("session").GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> PreviewAsync(string feature, object plan, string user = "alice") =>
        await db.CallAsync(WorkTools.SyncPlan, new { feature, plan }, user);

    private async Task<JsonElement> ConfirmAsync(JsonElement preview, string user = "alice") =>
        await db.CallAsync(WorkTools.SyncPlan, new { confirm = preview.GetProperty("preview_token").GetString() }, user);

    private static object[] Items(Dictionary<string, string> items, params object[] extra) =>
        [new { @ref = "a" }, new { @ref = "b" }, new { @ref = "c" }, .. extra];

    [Fact]
    public async Task Two_agents_confirm_previews_of_one_feature_concurrently_one_applies_and_the_other_is_refused_stale_and_can_regenerate()
    {
        var (project, feature, items) = await NewPlanAsync("SP");
        var aliceRevision = new { items = new object[] { new { @ref = "a", title = "Store (revised by alice)" }, new { @ref = "b" }, new { @ref = "c" } } };
        var bobRevision = new { items = new object[] { new { @ref = "a" }, new { @ref = "b", title = "Feed (revised by bob)" }, new { @ref = "c" } } };
        var alicePreview = await PreviewAsync(feature, aliceRevision, "alice");
        var bobPreview = await PreviewAsync(feature, bobRevision, "bob");
        alicePreview.GetProperty("items_edited").EnumerateArray().Single().GetProperty("key").GetString().ShouldBe(items["a"]);
        var feedBefore = (await db.AllChangesAsync(project)).Count;

        var outcomes = await Task.WhenAll(
            Outcome(() => ConfirmAsync(alicePreview, "alice")),
            Outcome(() => ConfirmAsync(bobPreview, "bob")));

        outcomes.Count(outcome => outcome == "ok").ShouldBe(1);
        outcomes.Count(outcome => outcome == WorkErrorCodes.Conflict).ShouldBe(1);
        var loser = outcomes[0] == "ok" ? ("bob", (object)bobRevision) : ("alice", (object)aliceRevision);

        // The loser regenerates its preview against the state the winner left, and applies it.
        var fresh = await PreviewAsync(feature, loser.Item2, loser.Item1);
        var applied = await ConfirmAsync(fresh, loser.Item1);
        applied.GetProperty("repeated").GetBoolean().ShouldBeFalse();
        (await db.CallAsync(WorkTools.GetWork, new { id = items["a"] })).GetProperty("title").GetString().ShouldBe("Store (revised by alice)");
        (await db.CallAsync(WorkTools.GetWork, new { id = items["b"] })).GetProperty("title").GetString().ShouldBe("Feed (revised by bob)");

        // Confirming again answers the first apply and writes nothing.
        var feedAfter = (await db.AllChangesAsync(project)).Count;
        var repeat = await ConfirmAsync(fresh, loser.Item1);
        repeat.GetProperty("repeated").GetBoolean().ShouldBeTrue();
        (await db.AllChangesAsync(project)).Count.ShouldBe(feedAfter);
        // Two applies, each one feature entry and one item entry, exactly once (E7).
        (feedAfter - feedBefore).ShouldBe(4);

        static async Task<string> Outcome(Func<Task<JsonElement>> call)
        {
            try
            {
                await call();
                return "ok";
            }
            catch (WorkException exception)
            {
                return exception.Code;
            }
        }
    }

    [Fact]
    public async Task A_preview_goes_stale_when_an_item_a_requirement_or_a_dependency_it_covers_changes_and_a_token_is_its_callers_only()
    {
        var (project, feature, items) = await NewPlanAsync("ST");
        var revision = new { items = Items(items, new { @ref = "d", title = "New work" }) };

        var preview = await PreviewAsync(feature, revision);
        await db.UpdateAsync(items["c"], new { title = "Docs, renamed meanwhile" });
        var stale = await db.RefusedAsync(WorkTools.SyncPlan, new { confirm = preview.GetProperty("preview_token").GetString() });
        stale.Code.ShouldBe(WorkErrorCodes.Conflict);
        stale.Message.ShouldContain("stale");

        preview = await PreviewAsync(feature, revision);
        await db.UpdateAsync(feature, new { requirements = new[] { new { id = "E1", text = "Strict contract" }, new { id = "E2", text = "Change feed" }, new { id = "E3", text = "New" } } });
        (await db.RefusedAsync(WorkTools.SyncPlan, new { confirm = preview.GetProperty("preview_token").GetString() })).Code.ShouldBe(WorkErrorCodes.Conflict);

        preview = await PreviewAsync(feature, revision);
        var outsider = await db.NewItemAsync(project, "Outside the feature");
        await db.UpdateAsync(items["c"], new { add_blocked_by = new[] { Key(outsider) } });
        (await db.RefusedAsync(WorkTools.SyncPlan, new { confirm = preview.GetProperty("preview_token").GetString() })).Code.ShouldBe(WorkErrorCodes.Conflict);

        // A new item filed into the feature after the preview makes it stale too.
        preview = await PreviewAsync(feature, revision);
        await db.CallAsync(WorkTools.CreateWork, new { project, item = new { title = "Filed meanwhile", feature } });
        (await db.RefusedAsync(WorkTools.SyncPlan, new { confirm = preview.GetProperty("preview_token").GetString() })).Code.ShouldBe(WorkErrorCodes.Conflict);

        // Nothing of the refused confirmations was written; a fresh preview applies.
        (await db.CallAsync(WorkTools.FindWork, new { project, text = "New work" })).GetProperty("items").GetArrayLength().ShouldBe(0);
        preview = await PreviewAsync(feature, new { items = Items(items, new { @ref = "d", title = "New work" }, new { @ref = Key(outsider) }) });
        (await db.RefusedAsync(WorkTools.SyncPlan, new { confirm = preview.GetProperty("preview_token").GetString() }, "bob")).Code.ShouldBe(WorkErrorCodes.NotFound);
        var applied = await ConfirmAsync(preview);
        applied.GetProperty("added").EnumerateArray().Single().GetString().ShouldNotBeNull();
        applied.GetProperty("closed").EnumerateArray().Single().GetString().ShouldNotBe(Key(outsider));
    }

    [Fact]
    public async Task An_expired_preview_is_refused_conflict_and_changes_nothing()
    {
        var (_, feature, items) = await NewPlanAsync("EX");
        var preview = await PreviewAsync(feature, new { items = Items(items, new { @ref = "late", title = "Too late" }) });
        db.Clock.Advance(TimeSpan.FromSeconds(WorkLimits.PlanPreviewSeconds + 1));
        var refused = await db.RefusedAsync(WorkTools.SyncPlan, new { confirm = preview.GetProperty("preview_token").GetString() });
        refused.Code.ShouldBe(WorkErrorCodes.Conflict);
        refused.Message.ShouldContain("expired");
        (await db.CallAsync(WorkTools.GetWork, new { id = feature })).GetProperty("completion").GetProperty("items").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task A_preview_lists_every_change_and_refuses_cycles_silent_drops_and_completion_without_writing()
    {
        var (project, feature, items) = await NewPlanAsync("PV");
        var feedBefore = (await db.AllChangesAsync(project)).Count;

        var preview = await PreviewAsync(feature, new
        {
            title = "Planning, revised",
            requirements = new[] { new { id = "E1", text = "Strict contract, every write" }, new { id = "E3", text = "Small by default" } },
            drop_requirements = new[] { new { id = "E2", decision = "The feed moved to its own feature." } },
            items = new object[]
            {
                new { @ref = "a", acceptance_criteria = new[] { new { text = "Writes return state" }, new { text = "Unknown args refused" } } },
                new { @ref = "b", blocked_by = Array.Empty<string>(), inherits = new[] { "E3" } },
                new { @ref = "e", title = "Small rows", inherits = new[] { "E3" }, blocked_by = new[] { "a" } }
            }
        });

        preview.GetProperty("unchanged").GetBoolean().ShouldBeFalse();
        Strings(preview, "requirements_added").ShouldBe(["E3"]);
        Strings(preview, "requirements_changed").ShouldBe(["E1"]);
        Strings(preview, "requirements_dropped").ShouldBe(["E2"]);
        preview.GetProperty("items_added").EnumerateArray().Single().GetProperty("ref").GetString().ShouldBe("e");
        preview.GetProperty("items_edited").EnumerateArray().Select(Key).ShouldBe([items["a"], items["b"]], ignoreOrder: true);
        preview.GetProperty("items_closed").EnumerateArray().Single().GetProperty("key").GetString().ShouldBe(items["c"]);
        preview.GetProperty("dependencies_removed").EnumerateArray().Single().GetProperty("item").GetString().ShouldBe(items["b"]);
        preview.GetProperty("dependencies_added").EnumerateArray().Single().GetProperty("blocked_by").GetString().ShouldBe(items["a"]);
        preview.GetProperty("feature_changes").EnumerateArray().Select(change => change.GetProperty("field").GetString()).ShouldContain("title");

        // Refusals: an active requirement left out without a decision, a cycle, a re-plan completing an item.
        (await db.RefusedAsync(WorkTools.SyncPlan, new { feature, plan = new { requirements = new[] { new { id = "E1", text = "Only" } }, items = Items(items) } }))
            .Message.ShouldContain("E2");
        (await db.RefusedAsync(WorkTools.SyncPlan, new { feature, plan = new { items = new object[] { new { @ref = "a", blocked_by = new[] { "c" } }, new { @ref = "b" }, new { @ref = "c" } } } }))
            .Code.ShouldBe(WorkErrorCodes.Cycle);
        (await db.RefusedAsync(WorkTools.SyncPlan, new { feature, plan = new { items = new object[] { new { @ref = "a", status = "complete" }, new { @ref = "b" }, new { @ref = "c" } } } }))
            .Message.ShouldContain("never completes");
        (await db.RefusedAsync(WorkTools.SyncPlan, new { feature, plan = new { items = Items(items), itemz = 1 } })).Message.ShouldContain("items");
        (await db.RefusedAsync(WorkTools.SyncPlan, new { feature, plan = new { items = Items(items) }, confirm = preview.GetProperty("preview_token").GetString() }))
            .Code.ShouldBe(WorkErrorCodes.InvalidArgument);

        // Previews write no work.
        (await db.AllChangesAsync(project)).Count.ShouldBe(feedBefore);

        var applied = await ConfirmAsync(preview);
        var view = applied.GetProperty("feature");
        view.GetProperty("title").GetString().ShouldBe("Planning, revised");
        var requirements = view.GetProperty("requirements").EnumerateArray().ToList();
        requirements.Select(requirement => requirement.GetProperty("id").GetString()).ShouldBe(["E1", "E3", "E2"]);
        requirements[2].GetProperty("state").GetString().ShouldBe("dropped");
        requirements[2].GetProperty("decision").GetString().ShouldBe("The feed moved to its own feature.");
        var a = await db.CallAsync(WorkTools.GetWork, new { id = items["a"] });
        a.GetProperty("acceptance_criteria").EnumerateArray().Select(criterion => criterion.GetProperty("id").GetString()).ShouldBe(["AC1", "AC2"]);
        (await db.CallAsync(WorkTools.GetWork, new { id = items["c"] })).GetProperty("status").GetString().ShouldBe("closed");
        (await db.CallAsync(WorkTools.GetWork, new { id = items["b"] })).TryGetProperty("blocked_by", out var blockers).ShouldBeTrue();
        blockers.GetArrayLength().ShouldBe(0);
        var e = await db.CallAsync(WorkTools.GetWork, new { id = applied.GetProperty("items").GetProperty("e").GetProperty("key").GetString() });
        e.GetProperty("ref").GetString().ShouldBe("e");
        e.GetProperty("feature").GetProperty("key").GetString().ShouldBe(feature);
        e.GetProperty("blocked_by").EnumerateArray().Single().GetProperty("key").GetString().ShouldBe(items["a"]);
    }

    [Fact]
    public async Task A_feature_whose_items_are_all_closed_is_not_complete_and_dependents_of_a_closed_prerequisite_show_cancelled_prerequisite_until_a_replan_replaces_the_edge()
    {
        var (_, feature, items) = await NewPlanAsync("FC");

        await db.UpdateAsync(items["a"], new { status = "closed" });
        var c = await db.CallAsync(WorkTools.GetWork, new { id = items["b"] });
        c.GetProperty("blocked").GetBoolean().ShouldBeTrue();
        c.GetProperty("blocked_reason").GetString().ShouldBe("cancelled_prerequisite");

        await db.UpdateAsync(items["b"], new { status = "closed" });
        await db.UpdateAsync(items["c"], new { status = "closed" });
        var closed = await db.CallAsync(WorkTools.GetWork, new { id = feature });
        closed.GetProperty("status").GetString().ShouldBe("open");
        closed.GetProperty("completion").GetProperty("finished").GetInt32().ShouldBe(3);
        Strings(closed.GetProperty("completion"), "uncovered_requirements").ShouldBe(["E1", "E2"]);

        // A re-plan replaces the cancelled prerequisite: b is reopened, blocked by a new item d.
        var preview = await PreviewAsync(feature, new
        {
            items = new object[]
            {
                new { @ref = "a" },
                new { @ref = "b", status = "ready", blocked_by = new[] { "d" } },
                new { @ref = "c" },
                new { @ref = "d", title = "Store, second approach", status = "ready", inherits = new[] { "E1" } }
            }
        });
        preview.GetProperty("dependencies_removed").EnumerateArray().Single().GetProperty("blocked_by").GetString().ShouldBe(items["a"]);
        var applied = await ConfirmAsync(preview);
        var d = applied.GetProperty("items").GetProperty("d").GetProperty("key").GetString()!;
        var b = await db.CallAsync(WorkTools.GetWork, new { id = items["b"] });
        b.GetProperty("blocked_reason").GetString().ShouldBe("waiting");

        // Only Complete satisfies the dependency, and only covered requirements complete the feature.
        await db.UpdateAsync(d, new { status = "complete" });
        (await db.CallAsync(WorkTools.GetWork, new { id = items["b"] })).GetProperty("blocked").GetBoolean().ShouldBeFalse();
        (await db.CallAsync(WorkTools.GetWork, new { id = feature })).GetProperty("status").GetString().ShouldBe("open");
        await db.UpdateAsync(items["b"], new { status = "complete" });
        var complete = await db.CallAsync(WorkTools.GetWork, new { id = feature });
        complete.GetProperty("status").GetString().ShouldBe("complete");
        complete.GetProperty("requirements").EnumerateArray().First().GetProperty("covered_by").EnumerateArray().Single().GetString().ShouldBe(d);
    }

    [Fact]
    public async Task A_requirement_dropped_with_a_decision_no_longer_has_to_be_covered()
    {
        var (_, feature, items) = await NewPlanAsync("DR");
        await db.UpdateAsync(items["a"], new { status = "complete" });
        await db.UpdateAsync(items["b"], new { status = "closed" });
        await db.UpdateAsync(items["c"], new { status = "closed" });
        (await db.CallAsync(WorkTools.GetWork, new { id = feature })).GetProperty("status").GetString().ShouldBe("open");

        (await db.RefusedAsync(WorkTools.UpdateWork, new
        {
            id = feature,
            expected_version = (await db.CallAsync(WorkTools.GetWork, new { id = feature })).GetProperty("version").GetInt64(),
            requirements = new[] { new { id = "E1", text = "Strict contract" } }
        })).Message.ShouldContain("drop_requirements");
        await db.UpdateAsync(feature, new { drop_requirements = new[] { new { id = "E2", decision = "Not needed: the feed already exists." } } });
        (await db.CallAsync(WorkTools.GetWork, new { id = feature })).GetProperty("status").GetString().ShouldBe("complete");
    }

    [Fact]
    public async Task Complete_is_set_by_an_explicit_update_with_an_optional_acceptance_report_and_merge_evidence_only_flags_the_item()
    {
        var (_, _, items) = await NewPlanAsync("CP");
        var a = items["a"];

        // Merge evidence (S6 sets it through the store) flags the item and moves nothing.
        var item = await db.CallAsync(WorkTools.GetWork, new { id = a });
        var target = (await db.Store.ResolveAsync(a))!;
        var flagged = await db.Store.UpdateItemAsync(target.Id, item.GetProperty("version").GetInt64(), new ItemPatchSpec(MergedAwaitingAcceptance: true), new WorkActor("github"));
        flagged.MergedAwaitingAcceptance.ShouldBe(true);
        flagged.Status.ShouldBe(WorkStatus.Ready);

        (await db.RefusedAsync(WorkTools.UpdateWork, new
        {
            id = a, expected_version = flagged.Version, acceptance_report = new { summary = "Done." }
        })).Message.ShouldContain("status complete");

        var completed = await db.UpdateAsync(a, new
        {
            status = "complete",
            acceptance_report = new
            {
                summary = "Every write answers its state.",
                criteria = new[] { new { id = "AC1", state = "met", evidence = "WorkTrackingMariaDbTests.Every_write_returns_the_resulting_state" } }
            }
        });
        completed.GetProperty("status").GetString().ShouldBe("complete");
        completed.TryGetProperty("merged_awaiting_acceptance", out _).ShouldBeFalse();
        var criterion = completed.GetProperty("acceptance_criteria").EnumerateArray().Single();
        criterion.GetProperty("state").GetString().ShouldBe("met");
        criterion.GetProperty("evidence").GetString()!.ShouldStartWith("WorkTrackingMariaDbTests");
        var report = completed.GetProperty("acceptance_report");
        report.GetProperty("reported_by").GetString().ShouldBe("alice");
        report.GetProperty("summary").GetString().ShouldBe("Every write answers its state.");

        // A report naming a criterion the item does not have is refused.
        (await db.RefusedAsync(WorkTools.UpdateWork, new
        {
            id = items["b"], expected_version = (await db.CallAsync(WorkTools.GetWork, new { id = items["b"] })).GetProperty("version").GetInt64(),
            status = "complete", acceptance_report = new { criteria = new[] { new { id = "AC9", state = "met" } } }
        })).Message.ShouldContain("AC9");
        // More than three criteria are refused.
        (await db.RefusedAsync(WorkTools.CreateWork, new
        {
            project = target.ProjectId.ToString(),
            item = new { title = "Too many", acceptance_criteria = new[] { new { text = "1" }, new { text = "2" }, new { text = "3" }, new { text = "4" } } }
        })).Message.ShouldContain("1 to 3");
    }

    [Fact]
    public async Task Get_work_returns_the_whole_brief_in_one_response_and_a_recovered_item_lists_its_unmet_acceptance()
    {
        var project = await db.NewProjectAsync("BR");
        var feature = (await db.CallAsync(WorkTools.CreateWork, new
        {
            project,
            feature = new { title = "Briefs", requirements = new[] { new { id = "E1", text = "Strict contract" }, new { id = "E4", text = "Small by default" } } }
        })).GetProperty("feature");
        var item = (await db.CallAsync(WorkTools.CreateWork, new
        {
            project,
            item = new
            {
                title = "Assemble the brief",
                status = "ready",
                feature = Key(feature),
                goal = "One response holds everything needed to start.",
                acceptance_criteria = new object[] { new { text = "Goal and AC", state = "met", evidence = "test A" }, new { text = "Unmet AC on recovery" } },
                inherits = new[] { "E1", "E4" },
                touches = new[] { "src/CodeGraph.Services/Work/WorkService.cs", "MySqlWorkStore.ToViewAsync" }
            }
        })).GetProperty("item");
        var key = Key(item);

        var first = await StartAsync(project, "claude-code");
        (await db.CallAsync(WorkTools.NextWork, new { project, session_id = first }, "michael")).GetProperty("item").GetProperty("key").GetString().ShouldBe(key);
        var current = await db.CallAsync(WorkTools.GetWork, new { id = key });
        await db.CallAsync(WorkTools.UpdateWork, new
        {
            id = key, expected_version = current.GetProperty("version").GetInt64(), session_id = first, status = "in_progress",
            add_links = new[] { new { kind = "pr", url = "https://github.com/o/r/pull/11" } },
            handoff = new { done = "Views extended.", next_action = "Wire unmet AC into recovery.", branch = "br-1" }
        }, "michael");

        var brief = await db.CallAsync(WorkTools.GetWork, new { id = key });
        brief.GetProperty("goal").GetString().ShouldBe("One response holds everything needed to start.");
        brief.GetProperty("acceptance_criteria").GetArrayLength().ShouldBe(2);
        brief.GetProperty("inherited_requirements").EnumerateArray().Select(requirement => requirement.GetProperty("text").GetString()).ShouldBe(["Strict contract", "Small by default"]);
        Strings(brief, "touches").ShouldContain("src/CodeGraph.Services/Work/WorkService.cs");
        brief.GetProperty("handoffs").EnumerateArray().Single().GetProperty("next_action").GetString().ShouldBe("Wire unmet AC into recovery.");
        var attempt = brief.GetProperty("attempts").EnumerateArray().Single();
        attempt.GetProperty("outcome").GetString().ShouldBe("active");
        attempt.GetProperty("holder").GetProperty("session_id").GetString().ShouldBe(first);
        attempt.GetProperty("holder").GetProperty("harness").GetString().ShouldBe("claude-code");
        attempt.GetProperty("branch").GetString().ShouldBe("br-1");
        attempt.GetProperty("prs").EnumerateArray().Single().GetString().ShouldBe("https://github.com/o/r/pull/11");
        attempt.GetProperty("handoff_id").GetString().ShouldBe(brief.GetProperty("handoffs")[0].GetProperty("id").GetString());
        // The open_messages slot is filled by S7; empty sections are left out (E4).
        brief.TryGetProperty("open_messages", out _).ShouldBeFalse();

        // The session dies; the next one recovers the item with the criteria not yet met.
        db.Clock.Advance(PastIdle);
        var second = await StartAsync(project, "codex", "bob");
        var briefing = await db.CallAsync(WorkTools.GetBriefing, new { session_id = second }, "bob");
        briefing.GetProperty("suggested_next").GetProperty("unmet_acceptance").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe("AC2");
        var recovered = await db.CallAsync(WorkTools.NextWork, new { project, session_id = second }, "bob");
        var unmet = recovered.GetProperty("recovery").GetProperty("unmet_acceptance").EnumerateArray().Single();
        unmet.GetProperty("id").GetString().ShouldBe("AC2");
        unmet.GetProperty("text").GetString().ShouldBe("Unmet AC on recovery");
        (await db.CallAsync(WorkTools.GetBriefing, new { session_id = second }, "bob")).GetProperty("my_work")[0]
            .GetProperty("unmet_acceptance").GetArrayLength().ShouldBe(1);

        // The first attempt was abandoned when its claim lapsed and passed on; the second is active.
        var attempts = (await db.CallAsync(WorkTools.GetWork, new { id = key })).GetProperty("attempts").EnumerateArray().ToList();
        attempts.Select(entry => entry.GetProperty("outcome").GetString()).ShouldBe(["active", "abandoned"]);
        attempts[0].GetProperty("holder").GetProperty("session_id").GetString().ShouldBe(second);
    }

    [Fact]
    public async Task A_replan_editing_an_in_progress_item_supersedes_its_attempt_keeps_the_claim_under_a_new_attempt_and_notifies_the_holder_once()
    {
        var (project, feature, items) = await NewPlanAsync("PI");
        var holder = await StartAsync(project, "claude-code");
        var claimed = (await db.CallAsync(WorkTools.NextWork, new { project, session_id = holder }, "michael")).GetProperty("item");
        Key(claimed).ShouldBe(items["a"]);
        await db.CallAsync(WorkTools.UpdateWork, new
        {
            id = items["a"], expected_version = claimed.GetProperty("version").GetInt64(), session_id = holder, status = "in_progress",
            add_links = new[] { new { kind = "pr", url = "https://github.com/o/r/pull/21" } }
        }, "michael");

        // Another agent re-plans the item the session holds.
        var preview = await PreviewAsync(feature, new { items = new object[] { new { @ref = "a", title = "Store, narrowed", touches = new[] { "src/Store.cs" } }, new { @ref = "b" }, new { @ref = "c" } } }, "bob");
        var pivot = preview.GetProperty("pivots").EnumerateArray().Single();
        pivot.GetProperty("key").GetString().ShouldBe(items["a"]);
        pivot.GetProperty("action").GetString().ShouldBe("edit");
        pivot.GetProperty("holder").GetProperty("session_id").GetString().ShouldBe(holder);
        await ConfirmAsync(preview, "bob");

        var item = await db.CallAsync(WorkTools.GetWork, new { id = items["a"] });
        item.GetProperty("claim").GetProperty("session_id").GetString().ShouldBe(holder);
        var attempts = item.GetProperty("attempts").EnumerateArray().ToList();
        attempts.Select(attempt => attempt.GetProperty("outcome").GetString()).ShouldBe(["active", "superseded"]);
        // Evidence of the superseded attempt stays with it (S6 ignores it for status).
        attempts[1].GetProperty("prs").EnumerateArray().Single().GetString().ShouldBe("https://github.com/o/r/pull/21");
        attempts[0].TryGetProperty("prs", out _).ShouldBeFalse();
        item.GetProperty("comments").EnumerateArray().Last().GetProperty("body").GetString()!.ShouldContain("superseded");

        // The holder's next work-tool response carries the notice, once.
        var next = await db.CallAsync(WorkTools.GetWork, new { id = items["b"], session_id = holder }, "michael");
        var notice = next.GetProperty("notices").EnumerateArray().Single();
        notice.GetProperty("kind").GetString().ShouldBe("changed_by_replan");
        notice.GetProperty("item").GetString().ShouldBe(items["a"]);
        notice.GetProperty("by").GetString().ShouldBe("bob");
        notice.GetProperty("superseded_attempt").GetString().ShouldBe(attempts[1].GetProperty("id").GetString());
        notice.GetProperty("changes").EnumerateArray().Select(change => change.GetString()).ShouldBe(["title", "touches"], ignoreOrder: true);
        (await db.CallAsync(WorkTools.GetWork, new { id = items["b"], session_id = holder }, "michael")).TryGetProperty("notices", out _).ShouldBeFalse();

        // The holder's write on the stale version is a conflict it merges; then it continues.
        var stale = await db.RefusedAsync(WorkTools.UpdateWork, new { id = items["a"], expected_version = claimed.GetProperty("version").GetInt64() + 1, session_id = holder, comment = "x" }, "michael");
        stale.Code.ShouldBe(WorkErrorCodes.Conflict);
    }

    [Fact]
    public async Task A_replan_closing_a_claimed_item_ends_the_claim_keeps_the_handoff_with_the_superseded_attempt_and_notifies_the_holder()
    {
        var (project, feature, items) = await NewPlanAsync("PC");
        var holder = await StartAsync(project, "codex");
        var claimed = (await db.CallAsync(WorkTools.NextWork, new { project, session_id = holder }, "michael")).GetProperty("item");
        await db.CallAsync(WorkTools.UpdateWork, new
        {
            id = items["a"], expected_version = claimed.GetProperty("version").GetInt64(), session_id = holder, status = "in_progress",
            handoff = new { done = "Half the store.", next_action = "The other half." }
        }, "michael");

        var preview = await PreviewAsync(feature, new { items = new object[] { new { @ref = "b", blocked_by = Array.Empty<string>() }, new { @ref = "c" } } }, "bob");
        preview.GetProperty("pivots").EnumerateArray().Single().GetProperty("action").GetString().ShouldBe("close");
        var applied = await ConfirmAsync(preview, "bob");
        applied.GetProperty("closed").EnumerateArray().Single().GetString().ShouldBe(items["a"]);

        var item = await db.CallAsync(WorkTools.GetWork, new { id = items["a"] });
        item.GetProperty("status").GetString().ShouldBe("closed");
        item.TryGetProperty("claim", out _).ShouldBeFalse();
        var attempt = item.GetProperty("attempts").EnumerateArray().Single();
        attempt.GetProperty("outcome").GetString().ShouldBe("superseded");
        attempt.GetProperty("handoff_id").GetString().ShouldBe(item.GetProperty("handoffs")[0].GetProperty("id").GetString());

        // The session's claim ended with the close: its next write is not claim_lost, and its briefing no longer holds the item.
        var response = await db.CallAsync(WorkTools.GetBriefing, new { session_id = holder }, "michael");
        response.GetProperty("notices").EnumerateArray().Single().GetProperty("closed").GetBoolean().ShouldBeTrue();
        response.TryGetProperty("my_work", out _).ShouldBeFalse();
        await db.CallAsync(WorkTools.UpdateWork, new { id = items["a"], expected_version = item.GetProperty("version").GetInt64(), session_id = holder, comment = "Understood." }, "michael");
    }

    [Fact]
    public async Task Plans_at_the_200_item_limit_import_and_replan_in_one_transaction_with_one_feed_entry_per_change()
    {
        var project = await db.NewProjectAsync("BIG");
        var items = Enumerable.Range(0, 200).Select(index => (object)new
        {
            @ref = $"i{index}",
            title = $"Item {index}",
            feature = "f",
            status = "ready",
            blocked_by = index == 0 ? Array.Empty<string>() : [$"i{index - 1}"],
            acceptance_criteria = new[] { new { text = $"Item {index} works" } }
        }).ToArray();
        var created = await db.CallAsync(WorkTools.CreateWork, new
        {
            project, idempotency_key = "big",
            plan = new { features = new[] { new { @ref = "f", title = "Big", requirements = new[] { new { id = "E1", text = "All" } } } }, items }
        });
        var feature = created.GetProperty("features").GetProperty("f").GetProperty("key").GetString()!;

        (await db.RefusedAsync(WorkTools.SyncPlan, new { feature, plan = new { items = Enumerable.Range(0, 201).Select(index => new { @ref = $"x{index}", title = "x" }).ToArray() } }))
            .Message.ShouldContain("200");

        // Re-plan every item: 199 kept and retitled (the chain re-linked past the dropped one), one closed, one added.
        var revised = Enumerable.Range(1, 199).Select(index => (object)new
        {
            @ref = $"i{index}",
            title = $"Item {index}, revised",
            inherits = new[] { "E1" },
            blocked_by = index == 1 ? Array.Empty<string>() : [$"i{index - 1}"]
        }).Append(new { @ref = "n", title = "New tail", blocked_by = new[] { "i199" } }).ToArray();
        var feedBefore = (await db.AllChangesAsync(project)).Count;
        var preview = await PreviewAsync(feature, new { items = revised });
        preview.GetProperty("items_edited").GetArrayLength().ShouldBe(199);
        preview.GetProperty("items_closed").GetArrayLength().ShouldBe(1);
        var applied = await ConfirmAsync(preview);
        applied.GetProperty("edited").GetArrayLength().ShouldBe(199);

        var entries = (await db.AllChangesAsync(project)).Skip(feedBefore).ToList();
        entries.Count.ShouldBe(1 + 1 + 200);
        entries[0].GetProperty("kind").GetString().ShouldBe("feature_updated");
        entries.Count(entry => entry.GetProperty("kind").GetString() == "item_created").ShouldBe(1);
        entries.Select(entry => entry.GetProperty("entity_id").GetString()).Distinct().Count().ShouldBe(entries.Count);
        var i1 = await db.CallAsync(WorkTools.GetWork, new { id = created.GetProperty("items").GetProperty("i1").GetProperty("key").GetString() });
        i1.GetProperty("blocked").GetBoolean().ShouldBeFalse();
        // The criteria kept their state through the re-plan (left out: unchanged).
        i1.GetProperty("acceptance_criteria").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task An_item_can_be_adopted_by_key_and_inherits_must_name_requirements_of_the_feature()
    {
        var (project, feature, items) = await NewPlanAsync("AD");
        var loose = await db.NewItemAsync(project, "Loose item");

        (await db.RefusedAsync(WorkTools.CreateWork, new { project, item = new { title = "Bad", feature, inherits = new[] { "E9" } } })).Message.ShouldContain("E9");
        (await db.RefusedAsync(WorkTools.CreateWork, new { project, item = new { title = "Bad", inherits = new[] { "E1" } } })).Message.ShouldContain("without a feature");
        (await db.RefusedAsync(WorkTools.SyncPlan, new { feature, plan = new { items = Items(items, new { @ref = "SW-999999", title = "x" }) } })).Code.ShouldBe(WorkErrorCodes.InvalidArgument);

        var preview = await PreviewAsync(feature, new { items = Items(items, new { @ref = Key(loose), inherits = new[] { "E2" } }) });
        var adopted = preview.GetProperty("items_edited").EnumerateArray().Single();
        adopted.GetProperty("key").GetString().ShouldBe(Key(loose));
        await ConfirmAsync(preview);
        var item = await db.CallAsync(WorkTools.GetWork, new { id = Key(loose) });
        item.GetProperty("feature").GetProperty("key").GetString().ShouldBe(feature);
        item.GetProperty("ref").GetString().ShouldBe(Key(loose));
        Strings(item, "inherits").ShouldBe(["E2"]);

        // Moving it out of the feature takes its ref with it and checks its inherits.
        (await db.RefusedAsync(WorkTools.UpdateWork, new { id = Key(loose), expected_version = item.GetProperty("version").GetInt64(), clear = new[] { "feature" } }))
            .Message.ShouldContain("inherits");
        var moved = await db.UpdateAsync(Key(loose), new { clear = new[] { "feature", "inherits" } });
        moved.TryGetProperty("ref", out _).ShouldBeFalse();
    }
}
