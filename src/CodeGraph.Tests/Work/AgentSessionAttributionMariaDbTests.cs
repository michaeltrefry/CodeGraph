using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Agent identity survives sessions (CG-F16 S3): an agent's session adds harness, model and host to
/// the agent instead of turning it into kind session, on every write path; sessions are not shared
/// between an agent and a person; a person's writes are recorded as before.
/// </summary>
public sealed class AgentSessionAttributionMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static readonly WorkActor Codex = new("codex-s3", WorkActor.AgentKind);

    [Fact]
    public async Task An_agents_session_writes_are_the_agents_on_every_path()
    {
        var project = await db.NewProjectAsync("AS");
        var item = await db.NewItemAsync(project, "Agent work");
        var key = item.GetProperty("key").GetString()!;
        var itemId = item.GetProperty("id").GetString()!;
        var session = await StartAsync(project, Codex);

        var claimed = await UpdateAsync(key, Codex, new { session_id = session, claim = new { }, status = "in_progress", comment = "Taking it." });
        claimed.GetProperty("claim").GetProperty("holder").GetString().ShouldBe(Codex.Name);
        await UpdateAsync(key, Codex, new { session_id = session, handoff = new { done = "Half.", next_action = "Finish." } });
        var recorded = await db.CallAsAsync(WorkTools.Record, new { session_id = session, entry = new { kind = "fact", summary = "Agents keep their kind" } }, Codex);
        await db.CallAsAsync(WorkTools.Post, new { session_id = session, item = key, kind = "info", body = "Progress." }, Codex);
        var hold = await db.CallAsAsync(WorkTools.Acquire, new { session_id = session, resource = "gpu:s3", purpose = "train" }, Codex);

        await using var connection = new MySqlConnection(db.ConnectionString);
        (await connection.QuerySingleAsync<string>("SELECT identity_kind FROM work_sessions WHERE id = @session", new { session })).ShouldBe("agent");
        var entry = await connection.QuerySingleAsync<(string, string, string, string, string)>(
            "SELECT author, author_kind, author_session_id, author_harness, author_model FROM knowledge_entries WHERE id = @id",
            new { id = recorded.GetProperty("entry").GetProperty("id").GetString() });
        entry.ShouldBe((Codex.Name, "agent", session, "codex", "gpt-5.6"));
        var comments = (await connection.QueryAsync<(string, string, string)>(
            "SELECT c.author, c.author_kind, c.author_session_id FROM work_item_comments c WHERE c.item_id = @itemId", new { itemId })).ToList();
        comments.ShouldNotBeEmpty();
        comments.ShouldAllBe(row => row.Item1 == Codex.Name && row.Item2 == "agent" && row.Item3 == session);
        (await connection.QuerySingleAsync<(string, string)>(
            "SELECT author, author_kind FROM work_handoffs WHERE item_id = @itemId", new { itemId }))
            .ShouldBe((Codex.Name, "agent"));
        (await connection.QuerySingleAsync<(string, string)>(
            "SELECT sender_name, sender_kind FROM work_messages WHERE item_id = @itemId", new { itemId }))
            .ShouldBe((Codex.Name, "agent"));
        var holder = hold.GetProperty("hold").GetProperty("holder");
        holder.GetProperty("identity").GetString().ShouldBe(Codex.Name);
        holder.GetProperty("kind").GetString().ShouldBe("agent");
        holder.GetProperty("harness").GetString().ShouldBe("codex");

        // The change feed carries the same actor.
        var changes = (await db.AllChangesAsync(project)).Where(change => change.GetProperty("entity_key").GetString() == key).ToList();
        var byAgent = changes.Where(change => change.GetProperty("actor").GetProperty("name").GetString() == Codex.Name).ToList();
        byAgent.ShouldNotBeEmpty();
        byAgent.ShouldAllBe(change => change.GetProperty("actor").GetProperty("kind").GetString() == "agent" &&
            change.GetProperty("actor").GetProperty("session_id").GetString() == session);
    }

    [Fact]
    public async Task An_agents_claim_and_hold_without_a_session_are_the_agents()
    {
        var project = await db.NewProjectAsync("AV");
        var key = (await db.NewItemAsync(project, "Sessionless")).GetProperty("key").GetString()!;
        var claimed = await UpdateAsync(key, Codex, new { claim = new { marker = "run-1" } });
        claimed.GetProperty("claim").GetProperty("holder_kind").GetString().ShouldBe("agent");
        // The attempt keeps the kind without a session to read it from.
        (await db.CallAsync(WorkTools.GetWork, new { id = key })).GetProperty("attempts")[0].GetProperty("holder").GetProperty("kind").GetString().ShouldBe("agent");
        var hold = await db.CallAsAsync(WorkTools.Acquire, new { project, resource = "gpu:sl", purpose = "train" }, Codex);
        hold.GetProperty("hold").GetProperty("holder").GetProperty("kind").GetString().ShouldBe("agent");

        // A person's claim carries no kind, as before.
        var other = (await db.NewItemAsync(project, "Owner")).GetProperty("key").GetString()!;
        (await UpdateAsync(other, new WorkActor("michael"), new { claim = new { marker = "m" } })).GetProperty("claim").TryGetProperty("holder_kind", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task An_agent_and_a_person_never_share_a_session()
    {
        var project = await db.NewProjectAsync("AT");
        var person = await StartAsync(project, new WorkActor("michael"));
        var agent = await StartAsync(project, new WorkActor("michael", WorkActor.AgentKind));

        var refused = await Should.ThrowAsync<WorkException>(() =>
            db.CallAsAsync(WorkTools.FindWork, new { session_id = person, project }, new WorkActor("michael", WorkActor.AgentKind)));
        refused.Message.ShouldContain("belongs to another identity");
        (await Should.ThrowAsync<WorkException>(() => db.CallAsAsync(WorkTools.FindWork, new { session_id = agent, project }, new WorkActor("michael"))))
            .Message.ShouldContain("belongs to another identity");
        (await Should.ThrowAsync<WorkException>(() => db.CallAsAsync(WorkTools.FindWork, new { session_id = agent, project }, Codex)))
            .Message.ShouldContain("belongs to another identity");
    }

    [Fact]
    public async Task Only_an_enabled_agent_of_that_name_starts_a_session()
    {
        var project = await db.NewProjectAsync("AW");
        var ghost = await Should.ThrowAsync<WorkException>(() =>
            db.CallAsAsync(WorkTools.StartSession, new { project, harness = "codex" }, new WorkActor("ghost-s3", WorkActor.AgentKind)));
        ghost.Code.ShouldBe(WorkErrorCodes.Forbidden);

        var off = await db.AgentAsync("off-s3");
        await using var connection = new MySqlConnection(db.ConnectionString);
        await connection.ExecuteAsync("UPDATE agents SET disabled_at = UTC_TIMESTAMP(3) WHERE name = 'off-s3'");
        (await Should.ThrowAsync<WorkException>(() => db.CallAsAsync(WorkTools.StartSession, new { project, harness = "codex" }, off))).Code.ShouldBe(WorkErrorCodes.Forbidden);
    }

    [Fact]
    public async Task A_persons_writes_are_recorded_as_before()
    {
        var project = await db.NewProjectAsync("AU");
        var key = (await db.NewItemAsync(project, "Owner work")).GetProperty("key").GetString()!;
        var session = await StartAsync(project, new WorkActor("michael"));
        await UpdateAsync(key, new WorkActor("michael"), new { session_id = session, comment = "With a session." });
        await UpdateAsync(key, new WorkActor("michael"), new { comment = "Without one." });

        var actors = (await db.AllChangesAsync(project))
            .Where(change => change.GetProperty("entity_key").GetString() == key && change.GetProperty("actor").GetProperty("name").GetString() == "michael")
            .Select(change => change.GetProperty("actor").GetProperty("kind").GetString()).ToList();
        actors.ShouldBe(["session", "user"]);
        await using var connection = new MySqlConnection(db.ConnectionString);
        (await connection.QuerySingleAsync<string>("SELECT identity_kind FROM work_sessions WHERE id = @session", new { session })).ShouldBe("user");
    }

    private async Task<string> StartAsync(string project, WorkActor actor)
    {
        if (actor.IsAgent)
            await db.AgentAsync(actor.Name);
        return (await db.CallAsAsync(WorkTools.StartSession, new { project, harness = "codex", model = "gpt-5.6", host = "mac" }, actor))
            .GetProperty("session").GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> UpdateAsync(string id, WorkActor actor, object fields)
    {
        var current = await db.CallAsync(WorkTools.GetWork, new { id });
        var arguments = JsonSerializer.SerializeToNode(fields, WorkJson.Options)!.AsObject();
        arguments["id"] = id;
        arguments["expected_version"] = current.GetProperty("version").GetInt64();
        return (await db.CallAsAsync(WorkTools.UpdateWork, arguments, actor)).GetProperty("item");
    }
}
