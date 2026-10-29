using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The project knowledge store (sc-27869) over a real MariaDB, through the work tools exactly as MCP
/// and REST run them: save points stored with the work write, record / recall, supersede, revise and
/// retract with revisions, project scoping, bounded recall and the native-memory import dry run.
/// </summary>
public sealed class KnowledgeMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private const string WorkServicePath = "src/CodeGraph.Services/Work/WorkService.cs";

    private async Task<string> ProjectAsync(string prefix, string? owned = null, params string[] dependencies)
    {
        var key = (prefix + Guid.NewGuid().ToString("N")[..6]).ToUpperInvariant()[..Math.Min(10, prefix.Length + 6)];
        var repositories = (owned is null ? [] : new[] { new { repository = owned, role = "owned" } })
            .Concat(dependencies.Select(repository => new { repository, role = "dependency" })).ToArray();
        await db.CallAsync(WorkTools.CreateWork, new { new_project = new { key, name = $"Project {key}", repositories } });
        return key;
    }

    private async Task<string> SessionAsync(string project, string user, string harness, string? repository = null, string[]? paths = null) =>
        (await db.CallAsync(WorkTools.StartSession, new { project, harness, repository, paths }, user)).GetProperty("session").GetProperty("id").GetString()!;

    private static List<JsonElement> Items(JsonElement result) => result.GetProperty("items").EnumerateArray().ToList();

    private async Task<long> CountAsync(string sql, object? parameters = null)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        return await connection.ExecuteScalarAsync<long>(sql, parameters);
    }

    [Fact]
    public async Task A_decision_in_update_work_is_recalled_and_in_another_sessions_briefing_for_the_same_paths()
    {
        var key = await ProjectAsync("KD", owned: "codegraph");
        var item = (await db.CallAsync(WorkTools.CreateWork, new { project = key, item = new { title = "Add the knowledge store", status = "ready", repository = "codegraph" } }))
            .GetProperty("item");
        var claude = await SessionAsync(key, "alice", "claude-code", "codegraph");
        var claimed = (await db.CallAsync(WorkTools.NextWork, new { project = key, session_id = claude }, "alice")).GetProperty("item");

        var updated = await db.CallAsync(WorkTools.UpdateWork, new
        {
            id = claimed.GetProperty("key").GetString(),
            expected_version = claimed.GetProperty("version").GetInt64(),
            session_id = claude,
            status = "in_progress",
            decisions = new[]
            {
                new
                {
                    decision = "Save points share the work write's transaction",
                    why = "A decision must never be stored without its item write, or the reverse",
                    alternatives = new[] { "A second call after the write" },
                    made_autonomously = true,
                    paths = new[] { WorkServicePath }
                }
            }
        }, "alice");

        // The write answers what it stored.
        updated.GetProperty("item").GetProperty("status").GetString().ShouldBe("in_progress");
        var recorded = updated.GetProperty("recorded").EnumerateArray().Single();
        recorded.GetProperty("kind").GetString().ShouldBe("decision");
        recorded.GetProperty("made_autonomously").GetBoolean().ShouldBeTrue();
        recorded.GetProperty("item").GetString().ShouldBe(item.GetProperty("key").GetString());
        var id = recorded.GetProperty("id").GetString();

        // recall by path, by a directory above it, and by id in full with its provenance.
        Items(await db.CallAsync(WorkTools.Recall, new { project = key, paths = new[] { WorkServicePath } })).Single().GetProperty("id").GetString().ShouldBe(id);
        Items(await db.CallAsync(WorkTools.Recall, new { project = key, paths = new[] { "src/CodeGraph.Services" } })).Single().GetProperty("id").GetString().ShouldBe(id);
        Items(await db.CallAsync(WorkTools.Recall, new { project = key, paths = new[] { "src/Other.cs" } })).ShouldBeEmpty();
        var full = Items(await db.CallAsync(WorkTools.Recall, new { id, detail = "full" })).Single();
        full.GetProperty("author").GetProperty("session_id").GetString().ShouldBe(claude);
        full.GetProperty("author").GetProperty("harness").GetString().ShouldBe("claude-code");
        full.GetProperty("repositories").EnumerateArray().Single().GetString().ShouldBe("codegraph");
        full.GetProperty("details").GetProperty("why").GetString()!.ShouldContain("never be stored");
        full.GetProperty("revisions").EnumerateArray().Single().GetProperty("action").GetString().ShouldBe("created");

        // Another session (another harness and identity) working on the same path is briefed with it,
        // with no separate save call; one working elsewhere in the repository is not.
        var codex = (await db.CallAsync(WorkTools.StartSession, new { project = key, harness = "codex", repository = "codegraph", paths = new[] { WorkServicePath } }, "bob"));
        var knowledge = codex.GetProperty("briefing").GetProperty("knowledge").EnumerateArray().Single();
        (knowledge.GetProperty("id").GetString(), knowledge.GetProperty("match").GetString()).ShouldBe((id, "path"));
        var elsewhere = await db.CallAsync(WorkTools.StartSession, new { project = key, harness = "codex", repository = "codegraph", paths = new[] { "docs/readme.md" } }, "bob");
        elsewhere.GetProperty("briefing").TryGetProperty("knowledge", out _).ShouldBeFalse();
        var refetched = await db.CallAsync(WorkTools.GetBriefing, new { session_id = codex.GetProperty("session").GetProperty("id").GetString(), paths = new[] { WorkServicePath } }, "bob");
        refetched.GetProperty("knowledge").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(id);

        // The item brief carries it too, matched by the item.
        var brief = await db.CallAsync(WorkTools.GetWork, new { id = item.GetProperty("key").GetString() });
        brief.GetProperty("knowledge").EnumerateArray().Single().GetProperty("match").GetString().ShouldBe("item");

        // The item's change entry names the stored entry.
        var change = (await db.AllChangesAsync(key)).Last();
        change.GetProperty("changes").EnumerateArray().ShouldContain(entry => entry.GetProperty("field").GetString() == "knowledge");
    }

    [Fact]
    public async Task End_session_stores_attempts_corrections_and_findings_with_the_end_linked_to_the_held_item()
    {
        var key = await ProjectAsync("KE", owned: "sceneworks");
        var item = await db.NewItemAsync(key, "Port the kernel");
        var session = await SessionAsync(key, "alice", "claude-code", "sceneworks");
        await db.CallAsync(WorkTools.NextWork, new { project = key, session_id = session }, "alice");

        var ended = await db.CallAsync(WorkTools.EndSession, new
        {
            session_id = session,
            handoff = new { done = "Tried flash-attn", next_action = "Try the reference kernel" },
            attempts = new[] { new { tried = "candle flash-attn on sm_86", why_failed = "segfaults in kernel X" } },
            corrections = new[]
            {
                new
                {
                    what = "Restated epic requirements in a story",
                    correct_behavior = "When slicing a plan, don't restate epic requirements in stories",
                    applies_when = new { activity = new[] { "slicing" } },
                    source = new { kind = "prompt", @ref = "session-1" }
                }
            },
            findings = new[] { new { kind = "gotcha", summary = "Shortcut tools ignore unknown params and still report success" } }
        }, "alice");

        var recorded = ended.GetProperty("recorded").EnumerateArray().ToList();
        recorded.Select(row => row.GetProperty("kind").GetString()).ShouldBe(["attempt", "lesson", "gotcha"]);
        recorded.ShouldAllBe(row => row.GetProperty("item").GetString() == item.GetProperty("key").GetString());

        var lessons = Items(await db.CallAsync(WorkTools.Recall, new { project = key, kind = new[] { "lesson" }, activity = "slicing", source = "prompt" }));
        lessons.Single().GetProperty("summary").GetString()!.ShouldStartWith("When slicing a plan");
        Items(await db.CallAsync(WorkTools.Recall, new { project = key, activity = "merging" })).ShouldBeEmpty();
        var lesson = Items(await db.CallAsync(WorkTools.Recall, new { id = lessons[0].GetProperty("id").GetString() })).Single();
        lesson.GetProperty("details").GetProperty("source").GetProperty("priority").GetString().ShouldBe("highest");

        // Attempts belong to their item: by item, and in its brief; the repository's gotcha too.
        Items(await db.CallAsync(WorkTools.Recall, new { project = key, item = item.GetProperty("key").GetString() })).Count.ShouldBe(3);
        var brief = await db.CallAsync(WorkTools.GetWork, new { id = item.GetProperty("key").GetString() });
        brief.GetProperty("knowledge").GetArrayLength().ShouldBe(3);

        // A retried end (same save points) answers the ended session and what its end stored.
        var retried = await db.CallAsync(WorkTools.EndSession, new
        {
            session_id = session,
            findings = new[] { new { kind = "gotcha", summary = "Shortcut tools ignore unknown params and still report success" } }
        }, "alice");
        retried.GetProperty("session").GetProperty("live").GetBoolean().ShouldBeFalse();
        retried.GetProperty("recorded").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(recorded[2].GetProperty("id").GetString());

        // New save points on an ended session are refused, not dropped.
        var refused = await db.RefusedAsync(WorkTools.EndSession, new { session_id = session, findings = new[] { new { kind = "fact", summary = "Too late" } } }, "alice");
        refused.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        refused.Message.ShouldContain("has ended");
        (await CountAsync("SELECT COUNT(*) FROM knowledge_entries e JOIN work_projects p ON p.id = e.project_id WHERE p.project_key = @key", new { key })).ShouldBe(3);
    }

    [Fact]
    public async Task A_briefing_finds_an_old_path_scoped_entry_behind_many_newer_ones_and_without_a_session_repository()
    {
        var key = await ProjectAsync("KO", dependencies: "inference");
        var old = (await db.CallAsync(WorkTools.Record, new
        {
            project = key,
            entry = new { kind = "constraint", summary = "The scheduler is single-writer", repositories = new[] { "inference" }, paths = new[] { "src/scheduler/mod.rs" } }
        })).GetProperty("entry").GetProperty("id").GetString();

        // 600 newer project-wide and path-scoped entries that do not match.
        await using (var connection = new MySqlConnection(db.ConnectionString))
        {
            var projectId = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM work_projects WHERE project_key = @key", new { key });
            await connection.ExecuteAsync(
                "INSERT INTO knowledge_entries (id, project_id, kind, status, summary, summary_hash, confidence, verified_at, author, created_at, updated_at) " +
                "SELECT UUID(), @projectId, 'decision', 'active', CONCAT('Noise ', seq), SHA2(seq, 256), 'medium', UTC_TIMESTAMP(6), 'alice', " +
                "UTC_TIMESTAMP(6) + INTERVAL seq SECOND, UTC_TIMESTAMP(6) + INTERVAL seq SECOND FROM seq_1_to_600", new { projectId });
            await connection.ExecuteAsync(
                "INSERT INTO knowledge_entry_paths (entry_id, path, prefix) SELECT id, CONCAT('docs/', summary_hash), CONCAT('docs/', summary_hash) " +
                "FROM knowledge_entries WHERE project_id = @projectId AND summary LIKE 'Noise 1%'", new { projectId });
        }

        // A session that names no repository still matches by path.
        var started = await db.CallAsync(WorkTools.StartSession, new { project = key, harness = "codex", paths = new[] { "src/scheduler/mod.rs" } }, "alice");
        var knowledge = started.GetProperty("briefing").GetProperty("knowledge").EnumerateArray().ToList();
        knowledge.Count(row => row.GetProperty("id").GetString() == old).ShouldBe(1);
        knowledge.Single(row => row.GetProperty("id").GetString() == old).GetProperty("match").GetString().ShouldBe("path");

        // The same with the repository named.
        var named = await db.CallAsync(WorkTools.StartSession, new { project = key, harness = "codex", repository = "inference", paths = new[] { "src/scheduler" } }, "alice");
        named.GetProperty("briefing").GetProperty("knowledge").EnumerateArray().ShouldContain(row => row.GetProperty("id").GetString() == old);
    }

    [Fact]
    public async Task A_refused_save_point_leaves_the_item_write_undone_and_a_conflict_stores_no_knowledge()
    {
        var key = await ProjectAsync("KR", owned: "inference");
        var item = await db.NewItemAsync(key, "Original title");
        var itemKey = item.GetProperty("key").GetString()!;
        var version = item.GetProperty("version").GetInt64();

        // A decision scoped to a repository outside the project: refused, and the title change with it.
        var refused = await db.RefusedAsync(WorkTools.UpdateWork, new
        {
            id = itemKey, expected_version = version, title = "Changed",
            decisions = new[] { new { decision = "Use mlx", repositories = new[] { "mlx-rs" } } }
        });
        refused.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        refused.Message.ShouldContain("not a repository of");
        var after = await db.CallAsync(WorkTools.GetWork, new { id = itemKey });
        (after.GetProperty("title").GetString(), after.GetProperty("version").GetInt64()).ShouldBe(("Original title", version));

        // A stale expected_version: the save point is not stored either.
        await db.UpdateAsync(itemKey, new { title = "Moved on" });
        var conflict = await db.RefusedAsync(WorkTools.UpdateWork, new
        {
            id = itemKey, expected_version = version, findings = new[] { new { kind = "fact", summary = "Lost write" } }
        });
        conflict.Code.ShouldBe(WorkErrorCodes.Conflict);
        (await CountAsync("SELECT COUNT(*) FROM knowledge_entries WHERE summary = 'Lost write'")).ShouldBe(0);

        // A misspelt save-point field names the valid ones; a field of another kind is refused.
        var misspelt = await db.RefusedAsync(WorkTools.UpdateWork, new { id = itemKey, expected_version = version + 1, decisions = new[] { new { decison = "x" } } });
        misspelt.Message.ShouldContain("decison");
        misspelt.Message.ShouldContain("made_autonomously");
        var foreign = await db.RefusedAsync(WorkTools.Record, new { project = key, entry = new { kind = "fact", summary = "A fact", why_failed = "no" } });
        foreign.Message.ShouldContain("why_failed");
    }

    [Fact]
    public async Task A_file_indexed_with_backslash_separators_is_verified_against_and_flagged_when_it_changes()
    {
        var key = await ProjectAsync("KW", dependencies: "sceneworks");
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.ExecuteAsync("INSERT INTO file_hashes (project, rel_path, content_hash) VALUES ('sceneworks', 'src\\\\Win\\\\Main.cs', 'h1')");

        var entry = (await db.CallAsync(WorkTools.Record, new
        {
            project = key,
            entry = new { kind = "gotcha", summary = "Main runs on the UI thread", repositories = new[] { "sceneworks" }, paths = new[] { "src/Win/Main.cs" } }
        })).GetProperty("entry");
        entry.GetProperty("verified_files").EnumerateArray().Single().GetProperty("path").GetString().ShouldBe("src\\Win\\Main.cs");
        entry.GetProperty("possibly_stale").GetBoolean().ShouldBeFalse();

        await connection.ExecuteAsync("UPDATE file_hashes SET content_hash = 'h2' WHERE project = 'sceneworks' AND rel_path = 'src\\\\Win\\\\Main.cs'");
        Items(await db.CallAsync(WorkTools.Recall, new { id = entry.GetProperty("id").GetString() })).Single().GetProperty("possibly_stale").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Record_supersede_revise_retract_and_verify_keep_every_revision()
    {
        var key = await ProjectAsync("KL", dependencies: "mlx-rs");
        var first = await db.CallAsync(WorkTools.Record, new
        {
            project = key, idempotency_key = "first",
            entry = new { kind = "constraint", summary = "MLX Metal is single-threaded", repositories = new[] { "mlx-rs" }, confidence = "high" }
        });
        first.GetProperty("created").GetBoolean().ShouldBeTrue();
        var entry = first.GetProperty("entry");
        var id = entry.GetProperty("id").GetString()!;
        (entry.GetProperty("revision").GetInt32(), entry.GetProperty("version").GetInt64(), entry.GetProperty("status").GetString()).ShouldBe((1, 1L, "active"));

        // A repeated key stores nothing and answers the first entry.
        var repeat = await db.CallAsync(WorkTools.Record, new { project = key, idempotency_key = "first", entry = new { kind = "constraint", summary = "Other" } });
        (repeat.GetProperty("created").GetBoolean(), repeat.GetProperty("entry").GetProperty("id").GetString()).ShouldBe((false, id));

        var revised = (await db.CallAsync(WorkTools.Record, new { revise = new { id, expected_version = 1, body = "Never run two MLX jobs on the dev Mac" } })).GetProperty("entry");
        (revised.GetProperty("revision").GetInt32(), revised.GetProperty("version").GetInt64()).ShouldBe((2, 2L));
        revised.GetProperty("summary").GetString().ShouldBe("MLX Metal is single-threaded");
        (await db.RefusedAsync(WorkTools.Record, new { revise = new { id, expected_version = 1, body = "stale" } })).Code.ShouldBe(WorkErrorCodes.Conflict);

        var replacement = await db.CallAsync(WorkTools.Record, new
        {
            project = key,
            entry = new { kind = "constraint", summary = "MLX Metal runs one job at a time per machine", supersedes = new { id, expected_version = 2 } }
        });
        replacement.GetProperty("superseded").GetProperty("status").GetString().ShouldBe("superseded");
        var newId = replacement.GetProperty("entry").GetProperty("id").GetString()!;
        replacement.GetProperty("entry").GetProperty("supersedes").GetString().ShouldBe(id);

        // Active by default; the superseded one on request, linked to its replacement, with its history.
        Items(await db.CallAsync(WorkTools.Recall, new { project = key })).Select(row => row.GetProperty("id").GetString()).ShouldBe([newId]);
        Items(await db.CallAsync(WorkTools.Recall, new { project = key, status = new[] { "superseded" } })).Single().GetProperty("id").GetString().ShouldBe(id);
        var old = Items(await db.CallAsync(WorkTools.Recall, new { id, detail = "full" })).Single();
        old.GetProperty("superseded_by").GetString().ShouldBe(newId);
        old.GetProperty("revisions").EnumerateArray().Select(revision => revision.GetProperty("action").GetString()).ShouldBe(["superseded", "revised", "created"]);
        (await db.RefusedAsync(WorkTools.Record, new { revise = new { id, expected_version = 3, summary = "x" } })).Code.ShouldBe(WorkErrorCodes.Conflict);

        var verified = (await db.CallAsync(WorkTools.Record, new { verify = new { id = newId, expected_version = 1, commit = "abc123" } })).GetProperty("entry");
        verified.GetProperty("verified_commit").GetString().ShouldBe("abc123");
        var retracted = (await db.CallAsync(WorkTools.Record, new { retract = new { id = newId, expected_version = 2, reason = "Fixed in MLX 0.30" } })).GetProperty("entry");
        (retracted.GetProperty("status").GetString(), retracted.GetProperty("status_reason").GetString()).ShouldBe(("retracted", "Fixed in MLX 0.30"));
        Items(await db.CallAsync(WorkTools.Recall, new { project = key })).ShouldBeEmpty();
        (await CountAsync("SELECT COUNT(*) FROM knowledge_revisions WHERE entry_id IN @ids", new { ids = new[] { id, newId } })).ShouldBe(6);
    }

    [Fact]
    public async Task Knowledge_stays_within_its_project()
    {
        var alpha = await ProjectAsync("KA", dependencies: "codegraph");
        var beta = await ProjectAsync("KB", dependencies: "sceneworks");
        var stored = await db.CallAsync(WorkTools.Record, new { project = alpha, entry = new { kind = "fact", summary = "Alpha's fact", paths = new[] { "a.cs" } } });
        var id = stored.GetProperty("entry").GetProperty("id").GetString();

        Items(await db.CallAsync(WorkTools.Recall, new { project = beta })).ShouldBeEmpty();
        Items(await db.CallAsync(WorkTools.Recall, new { project = beta, paths = new[] { "a.cs" } })).ShouldBeEmpty();
        var betaSession = await SessionAsync(beta, "alice", "claude-code", "codegraph", ["a.cs"]);
        (await db.CallAsync(WorkTools.GetBriefing, new { session_id = betaSession, paths = new[] { "a.cs" } }, "alice")).TryGetProperty("knowledge", out _).ShouldBeFalse();

        // A session of beta cannot read or write alpha's knowledge; an entry cannot name beta's repository.
        (await db.RefusedAsync(WorkTools.Recall, new { project = alpha, session_id = betaSession })).Message.ShouldContain("its own project");
        (await db.RefusedAsync(WorkTools.Recall, new { id, session_id = betaSession })).Message.ShouldContain("its own project");
        (await db.RefusedAsync(WorkTools.Record, new { project = alpha, entry = new { kind = "fact", summary = "x", repositories = new[] { "sceneworks" } } }))
            .Message.ShouldContain("not a repository of");
        (await db.RefusedAsync(WorkTools.Recall, new { id, project = beta })).Code.ShouldBe(WorkErrorCodes.NotFound);
    }

    [Fact]
    public async Task Recall_answers_bounded_pages()
    {
        var key = await ProjectAsync("KP");
        for (var index = 0; index < 25; index++)
            await db.CallAsync(WorkTools.Record, new { project = key, entry = new { kind = "fact", summary = $"Fact {index:00}", body = new string('x', 2000) } });

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await db.CallAsync(WorkTools.Recall, new { project = key, limit = 10, cursor });
            var rows = Items(page);
            rows.Count.ShouldBeLessThanOrEqualTo(10);
            // Rows are one line: no body.
            rows.Any(row => row.TryGetProperty("body", out var _)).ShouldBeFalse();
            seen.AddRange(rows.Select(row => row.GetProperty("summary").GetString()!));
            cursor = page.TryGetProperty("next_cursor", out var next) ? next.GetString() : null;
        } while (cursor is not null);

        seen.Count.ShouldBe(25);
        seen.Distinct().Count().ShouldBe(25);
        seen.First().ShouldBe("Fact 24");
        Items(await db.CallAsync(WorkTools.Recall, new { project = key, text = "fact 07" })).Single().GetProperty("summary").GetString().ShouldBe("Fact 07");
        (await db.RefusedAsync(WorkTools.Recall, new { project = key, limit = 51 })).Message.ShouldContain("1 to 50");
        (await db.RefusedAsync(WorkTools.Recall, new { project = key, limit = 11, detail = "full" })).Message.ShouldContain("1 to 10");
        Items(await db.CallAsync(WorkTools.Recall, new { project = key, limit = 3, detail = "full" })).ShouldAllBe(row => row.GetProperty("body").GetString()!.Length == 2000);
    }

    [Fact]
    public async Task The_import_dry_run_stores_nothing_and_confirm_stores_exactly_the_accepted_candidates()
    {
        var key = await ProjectAsync("KI", dependencies: "codegraph");
        await db.CallAsync(WorkTools.Record, new { project = key, entry = new { kind = "gotcha", summary = "Shortcut tools ignore unknown params." } });
        var before = await CountAsync("SELECT COUNT(*) FROM knowledge_entries");
        var candidates = new object[]
        {
            new { entry = new { kind = "fact", summary = "Dev Mac is also a CI runner", repositories = new[] { "codegraph" } }, source_file = "memory/runners.md", verification = "verified" },
            new { entry = new { kind = "gotcha", summary = "shortcut tools ignore  UNKNOWN params" }, source_file = "memory/shortcut.md", verification = "verified" },
            new { entry = new { kind = "fact", summary = "Prefers terse answers" }, source_file = "memory/user.md", verification = "personal" },
            new { entry = new { kind = "fact", summary = "Uses port 4200" }, source_file = "memory/old.md", verification = "stale", verification_note = "now 4300" },
            new { entry = new { kind = "fact", summary = "Lives in another repo", repositories = new[] { "sceneworks" } }, source_file = "memory/x.md", verification = "unverifiable" },
            new { entry = new { kind = "fact", summary = "Dev Mac is also a CI runner." }, source_file = "memory/dup.md", verification = "verified" },
            new { entry = new { kind = "attempt", summary = "No why_failed" }, source_file = "memory/bad.md", verification = "verified" },
            new { entry = new { kind = "lesson", summary = "Read workflow states before moving stories", applies_when = new { activity = new[] { "tracker_updates" } } }, source_file = "memory/sc.md", verification = "unverifiable" }
        };

        var preview = await db.CallAsync(WorkTools.ImportKnowledge, new { project = key, candidates });
        preview.GetProperty("stored").GetBoolean().ShouldBeFalse();
        var outcomes = preview.GetProperty("candidates").EnumerateArray().Select(candidate => candidate.GetProperty("outcome").GetString()).ToList();
        outcomes.ShouldBe(["would_store", "duplicate", "personal", "stale", "out_of_scope", "duplicate", "invalid", "would_store"]);
        preview.GetProperty("candidates")[1].GetProperty("duplicate_of").GetProperty("kind").GetString().ShouldBe("gotcha");
        preview.GetProperty("candidates")[5].GetProperty("duplicate_of_candidate").GetInt32().ShouldBe(0);
        (await CountAsync("SELECT COUNT(*) FROM knowledge_entries")).ShouldBe(before);
        var token = preview.GetProperty("preview_token").GetString();

        // A token of another batch, or a candidate that would not be stored, refuses the confirm; nothing is stored.
        (await db.RefusedAsync(WorkTools.ImportKnowledge, new { project = key, candidates = candidates.Take(7).ToArray(), confirm = true, accept = new[] { 0 }, preview_token = token, idempotency_key = "imp-x" }))
            .Code.ShouldBe(WorkErrorCodes.Conflict);
        (await db.RefusedAsync(WorkTools.ImportKnowledge, new { project = key, candidates, confirm = true, accept = new[] { 0, 2 }, preview_token = token, idempotency_key = "imp-y" }))
            .Message.ShouldContain("candidate 2 is personal");
        (await db.RefusedAsync(WorkTools.ImportKnowledge, new { project = key, candidates, confirm = true, accept = new[] { 0 }, preview_token = token }))
            .Message.ShouldContain("idempotency_key");
        (await CountAsync("SELECT COUNT(*) FROM knowledge_entries")).ShouldBe(before);

        var confirmed = await db.CallAsync(WorkTools.ImportKnowledge, new { project = key, candidates, confirm = true, accept = new[] { 0, 7 }, preview_token = token, idempotency_key = "imp-1" });
        confirmed.GetProperty("stored").GetBoolean().ShouldBeTrue();
        confirmed.GetProperty("candidates").EnumerateArray().Count(candidate => candidate.GetProperty("outcome").GetString() == "stored").ShouldBe(2);
        (await CountAsync("SELECT COUNT(*) FROM knowledge_entries")).ShouldBe(before + 2);
        var imported = Items(await db.CallAsync(WorkTools.Recall, new { project = key, kind = new[] { "fact" }, detail = "full" })).Single();
        imported.GetProperty("source_file").GetString().ShouldBe("memory/runners.md");

        // A retried confirm (same key, say after a lost answer) answers the first result and stores nothing.
        var retried = await db.CallAsync(WorkTools.ImportKnowledge, new { project = key, candidates, confirm = true, accept = new[] { 0, 7 }, preview_token = token, idempotency_key = "imp-1" });
        retried.GetRawText().ShouldBe(confirmed.GetRawText());
        (await CountAsync("SELECT COUNT(*) FROM knowledge_entries")).ShouldBe(before + 2);

        // Confirming the batch again under a new key would store duplicates: refused.
        (await db.RefusedAsync(WorkTools.ImportKnowledge, new { project = key, candidates, confirm = true, accept = new[] { 0 }, preview_token = token, idempotency_key = "imp-2" }))
            .Message.ShouldContain("candidate 0 is duplicate");
    }
}
