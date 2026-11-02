using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

public sealed class MySqlAgentStore(CodeGraphDbContext db) : IAgentStore
{
    private const int DuplicateKey = 1062;

    public async Task<IReadOnlyList<AgentEntity>> ListAgentsAsync(CancellationToken ct = default) =>
        await db.Agents.AsNoTracking().Where(agent => agent.DeletedAt == null).OrderBy(agent => agent.Name).ToListAsync(ct);

    public Task<AgentEntity?> GetAgentAsync(long id, CancellationToken ct = default) =>
        db.Agents.AsNoTracking().FirstOrDefaultAsync(agent => agent.Id == id && agent.DeletedAt == null, ct);

    public async Task<AgentEntity?> CreateAgentAsync(AgentEntity agent, CancellationToken ct = default)
    {
        db.Agents.Add(agent);
        try
        {
            await db.SaveChangesAsync(ct);
            return agent;
        }
        catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: DuplicateKey })
        {
            return null;
        }
        finally
        {
            db.Entry(agent).State = EntityState.Detached;
        }
    }

    public async Task<bool> UpdateAgentAsync(AgentEntity agent, CancellationToken ct = default)
    {
        try
        {
            return await db.Agents
                .Where(row => row.Id == agent.Id && row.DeletedAt == null)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(row => row.Name, agent.Name)
                    .SetProperty(row => row.DisabledAt, agent.DisabledAt)
                    .SetProperty(row => row.UpdatedAt, agent.UpdatedAt), ct) > 0;
        }
        catch (Exception exception) when ((exception as MySqlException ?? exception.InnerException as MySqlException) is { Number: DuplicateKey })
        {
            return false;
        }
    }

    public async Task<bool> DeleteAgentAsync(long id, DateTime deletedAt, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var deleted = await db.Agents
            .Where(agent => agent.Id == id && agent.DeletedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(agent => agent.DeletedAt, deletedAt).SetProperty(agent => agent.UpdatedAt, deletedAt), ct);
        if (deleted == 0)
            return false;
        await db.McpPersonalAccessTokens
            .Where(token => token.AgentId == id && token.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(token => token.RevokedAt, deletedAt), ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> IsHumanIdentityAsync(string name, CancellationToken ct = default) =>
        await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM admin_users WHERE username = {name})
                OR EXISTS (SELECT 1 FROM mcp_personal_access_tokens WHERE agent_id IS NULL AND username = {name})
                OR EXISTS (SELECT 1 FROM work_sessions WHERE identity = {name} AND identity_kind = 'user')
                OR EXISTS (SELECT 1 FROM work_changes WHERE actor_name = {name} AND actor_kind IN ('user', 'session')) AS `Value`
            """).SingleAsync(ct);

    public Task EndSessionsAsync(string name, DateTime endedAt, CancellationToken ct = default) =>
        db.Database.ExecuteSqlAsync($"UPDATE work_sessions SET ended_at = {endedAt} WHERE identity = {name} AND identity_kind = 'agent' AND ended_at IS NULL", ct);

    public async Task<bool> HasLiveSessionAsync(string name, DateTime seenAfter, CancellationToken ct = default) =>
        await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM work_sessions WHERE identity = {name} AND ended_at IS NULL AND last_seen_at > {seenAfter}) AS `Value`
            """).SingleAsync(ct);
}
