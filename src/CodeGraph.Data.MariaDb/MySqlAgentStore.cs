using CodeGraph.Data;
using Dapper;
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

    public async Task<AgentWriteOutcome> UpdateAgentAsync(AgentEntity agent, string? previousName, DateTime liveAfter, CancellationToken ct = default) =>
        await LockedAsync(agent.Id, previousName, liveAfter, agent.UpdatedAt, ct, async (connection, transaction) =>
        {
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE agents SET name = @Name, disabled_at = @DisabledAt, updated_at = @UpdatedAt WHERE id = @Id",
                    agent, transaction, cancellationToken: ct));
                return AgentWriteOutcome.Done;
            }
            catch (MySqlException exception) when (exception.Number == DuplicateKey)
            {
                return AgentWriteOutcome.NameTaken;
            }
        });

    public async Task<AgentWriteOutcome> DeleteAgentAsync(long id, DateTime deletedAt, DateTime liveAfter, CancellationToken ct = default) =>
        await LockedAsync(id, null, liveAfter, deletedAt, ct, async (connection, transaction) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE agents SET deleted_at = @deletedAt, updated_at = @deletedAt WHERE id = @id", new { id, deletedAt }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE mcp_personal_access_tokens SET revoked_at = @deletedAt WHERE agent_id = @id AND revoked_at IS NULL", new { id, deletedAt }, transaction, cancellationToken: ct));
            return AgentWriteOutcome.Done;
        }, endSessions: true);

    // Holds the agent's row (FOR UPDATE) for a rename or delete: a session start of the agent takes
    // the same row in share mode, so the two never interleave. Without a name change only the row is held.
    private async Task<AgentWriteOutcome> LockedAsync(long id, string? previousName, DateTime liveAfter, DateTime now, CancellationToken ct,
        Func<MySqlConnection, MySqlTransaction, Task<AgentWriteOutcome>> write, bool endSessions = false)
    {
        var connection = (MySqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var name = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                "SELECT name FROM agents WHERE id = @id AND deleted_at IS NULL FOR UPDATE", new { id }, transaction, cancellationToken: ct));
            if (name is null)
                return AgentWriteOutcome.NotFound;
            var sessionsOf = endSessions ? name : previousName;
            if (sessionsOf is not null)
            {
                if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                        "SELECT EXISTS (SELECT 1 FROM work_sessions WHERE identity = @sessionsOf AND identity_kind = 'agent' AND ended_at IS NULL AND last_seen_at > @liveAfter)",
                        new { sessionsOf, liveAfter }, transaction, cancellationToken: ct)))
                    return AgentWriteOutcome.LiveSession;
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_sessions SET ended_at = @now WHERE identity = @sessionsOf AND identity_kind = 'agent' AND ended_at IS NULL",
                    new { sessionsOf, now }, transaction, cancellationToken: ct));
            }

            var outcome = await write(connection, transaction);
            if (outcome == AgentWriteOutcome.Done)
                await transaction.CommitAsync(ct);
            return outcome;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<bool> IsHumanIdentityAsync(string name, CancellationToken ct = default) =>
        await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM admin_users WHERE username = {name})
                OR EXISTS (SELECT 1 FROM mcp_personal_access_tokens WHERE agent_id IS NULL AND username = {name})
                OR EXISTS (SELECT 1 FROM work_sessions WHERE identity = {name} AND identity_kind = 'user')
                OR EXISTS (SELECT 1 FROM work_changes WHERE actor_name = {name} AND actor_kind IN ('user', 'session')) AS `Value`
            """).SingleAsync(ct);
}
