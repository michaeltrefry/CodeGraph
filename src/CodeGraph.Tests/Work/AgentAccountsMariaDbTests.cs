using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Services;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Agent accounts (CG-F16 S1) through the real REST surface, the real token authentication handler
/// and the real admin requirement, with agents and tokens in MariaDB: an admin manages agents and
/// their tokens, an agent token's writes are the agent's (kind agent), a disabled or deleted agent's
/// token stops working on its next call, and names of people and of CodeGraph itself are refused.
/// </summary>
public sealed class AgentAccountsMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>, IAsyncLifetime
{
    private CodeGraphDbContext context = null!;
    private WorkRestHost host = null!;

    public async Task InitializeAsync()
    {
        context = new CodeGraphDbContext(new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(db.ConnectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb)).Options);
        var agentStore = new MySqlAgentStore(context);
        McpPersonalAccessTokenService? tokens = null;
        host = await WorkRestHost.StartAsync(db.Service,
            configureServices: services => services.AddSingleton(provider => new AgentService(agentStore, provider.GetRequiredService<McpPersonalAccessTokenService>(),
                Options.Create(new CodeGraph.Models.Work.WorkSessionOptions { IdleSeconds = WorkDatabase.IdleSeconds }))),
            tokens: hub => tokens = new McpPersonalAccessTokenService(new MySqlMcpPersonalAccessTokenStore(context), hub,
                Options.Create(new CodeGraphStorageOptions { MariaDbEncryptionKey = WorkRestHost.SigningKey }), agentStore));
    }

    public async Task DisposeAsync()
    {
        await host.DisposeAsync();
        await context.DisposeAsync();
    }

    [Fact]
    public async Task An_admin_manages_agents_and_an_agent_tokens_writes_are_the_agents()
    {
        var key = await ProjectAsync();
        var agent = await CreateAgentAsync("Codex-" + key.ToLowerInvariant());
        var name = agent.GetProperty("name").GetString()!;
        name.ShouldBe("codex-" + key.ToLowerInvariant());
        agent.GetProperty("status").GetString().ShouldBe("active");
        var id = agent.GetProperty("id").GetInt64();

        var (created, issued) = await host.SendAsync(HttpMethod.Post, $"/api/admin/agents/{id}/tokens",
            """{"name":"laptop","expiresInDays":30,"toolNames":["record","recall"]}""", person: "admin");
        created.ShouldBe(200);
        var token = issued.GetProperty("rawToken").GetString()!;
        issued.GetProperty("token").GetProperty("entitlementMode").GetString().ShouldBe("selected");

        var (listed, tokens) = await host.SendAsync(HttpMethod.Get, $"/api/admin/agents/{id}/tokens", person: "admin");
        listed.ShouldBe(200);
        tokens.GetArrayLength().ShouldBe(1);

        // A write with the agent's token and no session is the agent's (AC2).
        var (recorded, entry) = await RecordAsync(token, key, "Agents write as themselves");
        recorded.ShouldBe(200, entry.ToString());
        var author = await AuthorOfAsync(entry.GetProperty("entry").GetProperty("id").GetString()!);
        author.ShouldBe((name, "agent"));

        // Disabled: refused on its next call; re-enabled: works again (AC3).
        (await host.SendAsync(HttpMethod.Patch, $"/api/admin/agents/{id}", """{"disabled":true}""", person: "admin")).Status.ShouldBe(200);
        (await RecordAsync(token, key, "Refused while disabled")).Status.ShouldBe(401);
        (await host.SendAsync(HttpMethod.Patch, $"/api/admin/agents/{id}", """{"disabled":false}""", person: "admin")).Status.ShouldBe(200);
        (await RecordAsync(token, key, "Works again")).Status.ShouldBe(200);

        // Renamed: the same token now writes under the new name; history keeps the old one.
        var (renamed, after) = await host.SendAsync(HttpMethod.Patch, $"/api/admin/agents/{id}", $$"""{"name":"{{name}}-2"}""", person: "admin");
        renamed.ShouldBe(200, after.ToString());
        var (_, again) = await RecordAsync(token, key, "Written after the rename");
        (await AuthorOfAsync(again.GetProperty("entry").GetProperty("id").GetString()!)).ShouldBe((name + "-2", "agent"));
        author.ShouldBe((name, "agent"));

        // Deleted: every token revoked; the name may be used again.
        (await host.SendAsync(HttpMethod.Delete, $"/api/admin/agents/{id}", person: "admin")).Status.ShouldBe(204);
        (await RecordAsync(token, key, "Refused once deleted")).Status.ShouldBe(401);
        (await host.SendAsync(HttpMethod.Get, $"/api/admin/agents/{id}/tokens", person: "admin")).Status.ShouldBe(404);
        await CreateAgentAsync(name + "-2");
    }

    [Fact]
    public async Task Only_an_admin_signed_in_to_the_web_manages_agents()
    {
        var key = await ProjectAsync();
        var agent = await CreateAgentAsync("claude-" + key.ToLowerInvariant());
        var id = agent.GetProperty("id").GetInt64();
        var (_, issued) = await host.SendAsync(HttpMethod.Post, $"/api/admin/agents/{id}/tokens", """{"name":"t","expiresInDays":30}""", person: "admin");
        var agentToken = issued.GetProperty("rawToken").GetString()!;
        var personToken = await host.CreateTokenAsync("admin");

        (await host.SendAsync(HttpMethod.Get, "/api/admin/agents", person: "bob")).Status.ShouldBe(403);
        (await host.SendAsync(HttpMethod.Get, "/api/admin/agents", token: agentToken)).Status.ShouldBe(401);
        (await host.SendAsync(HttpMethod.Get, "/api/admin/agents", token: personToken)).Status.ShouldBe(401);
        (await host.SendAsync(HttpMethod.Post, "/api/admin/agents", """{"name":"sneaky"}""", token: agentToken)).Status.ShouldBe(401);
        // An agent's token is not a person's: the user token endpoints refuse it, and the admin's list leaves it out.
        (await host.SendAsync(HttpMethod.Get, "/api/user/mcp-tokens", token: agentToken)).Status.ShouldBe(401);
        (await host.Tokens.ListForUserAsync(agent.GetProperty("name").GetString()!)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Names_of_people_and_of_CodeGraph_itself_are_refused()
    {
        var key = await ProjectAsync();
        // alice wrote work (in the change feed); carol owns a token of her own.
        await host.CreateTokenAsync("carol");

        foreach (var name in new[] { "dreamer", "anonymous", "human", "alice", "carol", "import:shortcut", "", "-lead", new string('a', 65) })
        {
            var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/admin/agents", JsonSerializer.Serialize(new { name }), person: "admin");
            status.ShouldBe(400, $"{name}: {body}");
            body.GetProperty("error").GetString().ShouldBe("invalid_argument");
        }

        var agent = await CreateAgentAsync("dup-" + key.ToLowerInvariant());
        var (duplicate, _) = await host.SendAsync(HttpMethod.Post, "/api/admin/agents", JsonSerializer.Serialize(new { name = agent.GetProperty("name").GetString() }), person: "admin");
        duplicate.ShouldBe(400);
    }

    [Fact]
    public async Task An_agent_with_a_live_session_is_neither_renamed_nor_deleted()
    {
        var key = await ProjectAsync();
        var agent = await CreateAgentAsync("live-" + key.ToLowerInvariant());
        var id = agent.GetProperty("id").GetInt64();
        var (_, issued) = await host.SendAsync(HttpMethod.Post, $"/api/admin/agents/{id}/tokens", """{"name":"t","expiresInDays":30}""", person: "admin");
        var token = issued.GetProperty("rawToken").GetString()!;

        var (started, session) = await host.SendAsync(HttpMethod.Post, "/api/work/sessions", JsonSerializer.Serialize(new { project = key, harness = "codex" }), token: token);
        started.ShouldBe(200, session.ToString());

        var (renamed, refusal) = await host.SendAsync(HttpMethod.Patch, $"/api/admin/agents/{id}", """{"name":"other-name"}""", person: "admin");
        renamed.ShouldBe(409);
        refusal.GetProperty("error").GetString().ShouldBe("conflict");
        (await host.SendAsync(HttpMethod.Delete, $"/api/admin/agents/{id}", person: "admin")).Status.ShouldBe(409);
        // Disabling is allowed: it is how an admin stops an agent at once.
        (await host.SendAsync(HttpMethod.Patch, $"/api/admin/agents/{id}", """{"disabled":true}""", person: "admin")).Status.ShouldBe(200);
    }

    private async Task<JsonElement> CreateAgentAsync(string name)
    {
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/admin/agents", JsonSerializer.Serialize(new { name }), person: "admin");
        status.ShouldBe(200, body.ToString());
        return body;
    }

    private async Task<string> ProjectAsync()
    {
        var key = "AG" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        await db.CallAsync("create_work", new { new_project = new { key, name = "Agents" } });
        return key;
    }

    private Task<(int Status, JsonElement Body)> RecordAsync(string token, string project, string summary) =>
        host.SendAsync(HttpMethod.Post, "/api/work/knowledge",
            JsonSerializer.Serialize(new { project, entry = new { kind = "fact", summary } }), token: token);

    private async Task<(string Author, string Kind)> AuthorOfAsync(string entryId)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        return await connection.QuerySingleAsync<(string, string)>("SELECT author, author_kind FROM knowledge_entries WHERE id = @entryId", new { entryId });
    }
}
