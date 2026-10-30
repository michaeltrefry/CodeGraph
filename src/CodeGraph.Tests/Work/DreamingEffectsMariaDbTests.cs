using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>Actual REST and MariaDB read model for D37, including event deduplication and a merge's immutable baseline.</summary>
public sealed class DreamingEffectsMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private MySqlDreamingEffectsStore Effects => new(Options.Create(new MariaDbStorageOptions { ConnectionString = db.ConnectionString }));

    [Fact]
    public async Task Rest_reports_each_changed_rule_with_recurrence_before_and_after_from_lesson_events()
    {
        var key = await db.NewProjectAsync("DE");
        var project = (await db.Store.ResolveAsync(key))!.Id;
        var rule = await RuleAsync(key, "Read provider configuration");
        var other = await RuleAsync(key, "Preserve exact evidence");
        var candidate = await RuleAsync(key, "Candidate is excluded");
        var run = Guid.NewGuid(); var at = DateTime.UtcNow.AddHours(-1);
        var prior = new[] { Event(rule, "created", at.AddHours(-3)), Event(rule, "served-before", at.AddHours(-2), served: true), Event(rule, "not-served-before", at.AddHours(-1), served: false) };
        // A duplicate correction marked uncounted and an unknown serving state are not served recurrences.
        var current = prior.Concat([Event(rule, "served-before", at.AddMinutes(1), served: true, counted: false), Event(rule, "served-after", at.AddMinutes(2), served: true), Event(rule, "unknown-after", at.AddMinutes(3), served: null)]).ToArray();
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await AddRunAsync(connection, project, run, at);
        await connection.ExecuteAsync("DELETE FROM lesson_events WHERE entry_id IN @ids", new { ids = new[] { rule.ToString(), other.ToString() } });
        foreach (var e in current) await InsertEventAsync(connection, project, e);
        var secondEvents = new[] { Event(other, "created-other", at.AddHours(-2)), Event(other, "served-other-after", at.AddMinutes(3), served: true) };
        foreach (var e in secondEvents) await InsertEventAsync(connection, project, e);
        // Both sides of a merge are included, while the baseline remains in the pre-write journal
        // even after their current event ownership changes.
        await JournalAsync(connection, project, run, at, [Entry(rule, "Read provider configuration", "rule", 1), Entry(other, "Preserve exact evidence", "rule", 1), Entry(candidate, "Candidate is excluded", "candidate", 1)],
            [Entry(rule, "Read provider configuration", "rule", 2), Entry(other, "Preserve exact evidence", "rule", 2), Entry(candidate, "Candidate is excluded", "candidate", 2)], prior.Concat(secondEvents.Take(1)).ToArray());
        // A second action's snapshots include a rule that was read but did not change: exclude it.
        await JournalAsync(connection, project, run, at.AddSeconds(1), [Entry(rule, "Read provider configuration", "rule", 2)], [Entry(rule, "Read provider configuration", "rule", 2)], prior);
        await using var host = await WorkRestHost.StartAsync(db.Service, db.Store, services => services.AddSingleton<IDreamingEffectsStore>(Effects));
        var (status, body) = await host.SendAsync(HttpMethod.Get, $"/api/work/dreaming/{key}/runs/{run}/effects", person: "reviewer");
        status.ShouldBe(200);
        var rules = body.GetProperty("rules").EnumerateArray().ToList(); rules.Count.ShouldBe(2);
        var effect = rules.Single(row => row.GetProperty("entry_id").GetGuid() == rule);
        effect.GetProperty("summary").GetString().ShouldBe("Read provider configuration");
        effect.GetProperty("before").GetProperty("while_served").GetInt32().ShouldBe(1);
        effect.GetProperty("before").GetProperty("while_not_served").GetInt32().ShouldBe(1);
        effect.GetProperty("before").GetProperty("events").GetInt32().ShouldBe(3);
        effect.GetProperty("after").GetProperty("while_served").GetInt32().ShouldBe(1);
        effect.GetProperty("after").GetProperty("while_not_served").GetInt32().ShouldBe(0);
        effect.GetProperty("after").GetProperty("events").GetInt32().ShouldBe(2);
        // Retries/merged duplicates do not inflate recurrence.
        var second = rules.Single(row => row.GetProperty("entry_id").GetGuid() == other);
        second.GetProperty("before").GetProperty("while_served").GetInt32().ShouldBe(0);
        second.GetProperty("after").GetProperty("while_served").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Effects_are_project_scoped_and_require_authentication_and_read_entitlement()
    {
        var key = await db.NewProjectAsync("DF"); var project = (await db.Store.ResolveAsync(key))!.Id;
        var wrongKey = await db.NewProjectAsync("DG"); var run = Guid.NewGuid();
        await using var connection = new MySqlConnection(db.ConnectionString); await connection.OpenAsync();
        await AddRunAsync(connection, project, run, DateTime.UtcNow);
        await using var host = await WorkRestHost.StartAsync(db.Service, db.Store, services => services.AddSingleton<IDreamingEffectsStore>(Effects));
        (await host.SendAsync(HttpMethod.Get, $"/api/work/dreaming/{key}/runs/{run}/effects")).Status.ShouldBe(401);
        (await host.SendAsync(HttpMethod.Get, $"/api/work/dreaming/{wrongKey}/runs/{run}/effects", person: "reviewer")).Status.ShouldBe(404);
        (await host.SendAsync(HttpMethod.Get, $"/api/work/dreaming/UNKNOWN/runs/{run}/effects", person: "reviewer")).Status.ShouldBe(404);
        var token = await host.CreateTokenAsync("writer-only", WorkTools.Record);
        (await host.SendAsync(HttpMethod.Get, $"/api/work/dreaming/{key}/runs/{run}/effects", token: token)).Status.ShouldBe(403);
        var (status, body) = await host.SendAsync(HttpMethod.Get, $"/api/work/dreaming/{key}/runs/{run}/effects", person: "reviewer");
        status.ShouldBe(200); body.GetProperty("rules").GetArrayLength().ShouldBe(0);
    }

    private async Task<Guid> RuleAsync(string key, string summary) => (await db.CallAsync(WorkTools.Record, new { project = key, entry = new { kind = "lesson", summary, source = new { kind = "prompt" }, applies_when = new { activity = new[] { "reviewing" } } } })).GetProperty("entry").GetProperty("id").GetGuid();
    private static Dictionary<string, object?> Entry(Guid id, string summary, string state, long version) => new() { ["id"] = id, ["kind"] = "lesson", ["summary"] = summary, ["rule_state"] = state, ["version"] = version };
    private static Dictionary<string, object?> Event(Guid entry, string hash, DateTime at, bool? served = null, bool counted = true) => new()
    {
        ["entry_id"] = entry, ["correction_id"] = Guid.NewGuid(), ["event_hash"] = hash.PadRight(64, '0'), ["event_key"] = hash,
        ["counted"] = counted, ["rule_at_event"] = true, ["served"] = served, ["created_at"] = at
    };
    private static Task AddRunAsync(MySqlConnection connection, Guid project, Guid run, DateTime at) => connection.ExecuteAsync(
        "INSERT INTO dreaming_runs(id,project_id,status,started_at,ended_at,run_json) VALUES(@run,@project,'completed',@at,@at,'{}')", new { run, project, at });
    private static Task JournalAsync(MySqlConnection connection, Guid project, Guid run, DateTime at, Dictionary<string, object?>[] before, Dictionary<string, object?>[] after, Dictionary<string, object?>[] events) => connection.ExecuteAsync(
        "INSERT INTO dream_write_journal(run_id,project_id,entry_id,action,before_json,after_json,evidence_json,prevalence,created_at) VALUES(@run,@project,@entry,'same_as',@before,@after,'[]','2 sessions',@at)",
        new { run, project, entry = before[0]["id"], before = Snapshot(before, events), after = Snapshot(after, events), at });
    private static string Snapshot(Dictionary<string, object?>[] entries, Dictionary<string, object?>[] events) => JsonSerializer.Serialize(new { EntryIds = entries.Select(entry => entry["id"]), Tables = new Dictionary<string, object> { ["knowledge_entries"] = entries, ["lesson_events"] = events } });
    private static Task InsertEventAsync(MySqlConnection connection, Guid project, Dictionary<string, object?> e) => connection.ExecuteAsync(
        "INSERT INTO lesson_events(correction_id,entry_id,project_id,event_key,event_hash,counted,rule_at_event,served,summary,summary_hash,author,created_at) VALUES(@correction,@entry,@project,@key,@hash,@counted,@rule,@served,'test',@hash,'reviewer',@at)",
        new { correction = e["correction_id"], entry = e["entry_id"], project, key = e["event_key"], hash = e["event_hash"], counted = e["counted"], rule = e["rule_at_event"], served = e["served"], at = e["created_at"] });
}
