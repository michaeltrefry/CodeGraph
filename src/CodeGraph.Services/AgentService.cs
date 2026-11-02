using System.Text.RegularExpressions;
using CodeGraph.Data;
using CodeGraph.Models.Responses;
using CodeGraph.Models.Work;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services;

/// <summary>
/// Agents (CG-F16): named non-human identities the owner manages in Admin, and their tokens. A
/// name is unique among agents, never a reserved or recorded human name, and cannot change (nor
/// the agent be deleted) while a session of the agent is live, since sessions are bound to it by name.
/// </summary>
public sealed partial class AgentService(
    IAgentStore store,
    McpPersonalAccessTokenService tokens,
    IOptions<WorkSessionOptions>? sessionOptions = null,
    TimeProvider? timeProvider = null)
{
    /// <summary>Names CodeGraph itself acts or addresses under.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal) { "anonymous", "dreamer", MessageChannels.HumanRecipient };

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly int idleSeconds = Math.Max(1, sessionOptions?.Value.IdleSeconds ?? new WorkSessionOptions().IdleSeconds);

    public async Task<IReadOnlyList<AgentSummary>> ListAsync(CancellationToken ct = default)
    {
        var agents = await store.ListAgentsAsync(ct);
        var summaries = new List<AgentSummary>(agents.Count);
        foreach (var agent in agents)
            summaries.Add(await SummaryAsync(agent, ct));
        return summaries;
    }

    public async Task<AgentResult<AgentSummary>> CreateAsync(string? name, string createdBy, CancellationToken ct = default)
    {
        var (normalized, error) = await CheckNameAsync(name, ct);
        if (error is not null)
            return error;

        var now = clock.GetUtcNow().UtcDateTime;
        var created = await store.CreateAgentAsync(new AgentEntity { Name = normalized!, CreatedBy = createdBy, CreatedAt = now, UpdatedAt = now }, ct);
        return created is null
            ? AgentResult<AgentSummary>.Fail(AgentErrors.InvalidArgument, $"An agent named '{normalized}' already exists.")
            : AgentResult<AgentSummary>.Ok(await SummaryAsync(created, ct));
    }

    /// <summary>Renames the agent and/or disables or re-enables it; a disabled agent's tokens stop working at once.</summary>
    public async Task<AgentResult<AgentSummary>> UpdateAsync(long id, string? name, bool? disabled, CancellationToken ct = default)
    {
        if (name is null && disabled is null)
            return AgentResult<AgentSummary>.Fail(AgentErrors.InvalidArgument, "Pass name, disabled or both.");
        if (await store.GetAgentAsync(id, ct) is not { } agent)
            return NotFound<AgentSummary>(id);

        var now = clock.GetUtcNow().UtcDateTime;
        if (name is not null && !string.Equals(Normalize(name), agent.Name, StringComparison.Ordinal))
        {
            var (normalized, error) = await CheckNameAsync(name, ct);
            if (error is not null)
                return error;
            if (await HasLiveSessionAsync(agent, now, ct))
                return LiveSession<AgentSummary>(agent, "renamed");
            await store.EndSessionsAsync(agent.Name, now, ct);
            agent.Name = normalized!;
        }

        if (disabled is { } off)
            agent.DisabledAt = off ? agent.DisabledAt ?? now : null;
        agent.UpdatedAt = now;
        return await store.UpdateAgentAsync(agent, ct)
            ? AgentResult<AgentSummary>.Ok(await SummaryAsync(agent, ct))
            : AgentResult<AgentSummary>.Fail(AgentErrors.InvalidArgument, $"An agent named '{agent.Name}' already exists.");
    }

    /// <summary>Deletes the agent and revokes its tokens. Its recorded writes keep its name.</summary>
    public async Task<AgentResult<bool>> DeleteAsync(long id, CancellationToken ct = default)
    {
        if (await store.GetAgentAsync(id, ct) is not { } agent)
            return NotFound<bool>(id);
        var now = clock.GetUtcNow().UtcDateTime;
        if (await HasLiveSessionAsync(agent, now, ct))
            return LiveSession<bool>(agent, "deleted");
        await store.EndSessionsAsync(agent.Name, now, ct);
        return await store.DeleteAgentAsync(id, now, ct) ? AgentResult<bool>.Ok(true) : NotFound<bool>(id);
    }

    public async Task<AgentResult<IReadOnlyList<McpPersonalAccessTokenMetadata>>> ListTokensAsync(long id, CancellationToken ct = default) =>
        await store.GetAgentAsync(id, ct) is { } agent
            ? AgentResult<IReadOnlyList<McpPersonalAccessTokenMetadata>>.Ok(await tokens.ListForAgentAsync(agent, ct))
            : NotFound<IReadOnlyList<McpPersonalAccessTokenMetadata>>(id);

    /// <summary>Issues a token of the agent; the raw value is answered once and never stored.</summary>
    public async Task<AgentResult<McpPersonalAccessTokenCreateResult>> CreateTokenAsync(
        long id, string tokenName, int expiresInDays, IReadOnlyCollection<string>? toolNames, CancellationToken ct = default) =>
        await store.GetAgentAsync(id, ct) is { } agent
            ? AgentResult<McpPersonalAccessTokenCreateResult>.Ok(await tokens.CreateForAgentAsync(agent, tokenName, expiresInDays, toolNames, ct))
            : NotFound<McpPersonalAccessTokenCreateResult>(id);

    public async Task<AgentResult<McpPersonalAccessTokenCreateResult>> UpdateTokenToolsAsync(
        long id, long tokenId, IReadOnlyCollection<string> toolNames, CancellationToken ct = default) =>
        await store.GetAgentAsync(id, ct) is { } agent
            ? AgentResult<McpPersonalAccessTokenCreateResult>.Ok(await tokens.UpdateToolsForAgentAsync(agent, tokenId, toolNames, ct))
            : NotFound<McpPersonalAccessTokenCreateResult>(id);

    public async Task<AgentResult<bool>> RevokeTokenAsync(long id, long tokenId, CancellationToken ct = default) =>
        await store.GetAgentAsync(id, ct) is { } agent
            ? AgentResult<bool>.Ok(await tokens.RevokeForAgentAsync(agent, tokenId, ct))
            : NotFound<bool>(id);

    private async Task<(string? Name, AgentResult<AgentSummary>? Error)> CheckNameAsync(string? name, CancellationToken ct)
    {
        var normalized = Normalize(name);
        if (!NamePattern().IsMatch(normalized))
            return (null, AgentResult<AgentSummary>.Fail(AgentErrors.InvalidArgument,
                "An agent name is 1 to 64 characters: lower-case letters, digits, '.', '_' or '-', starting with a letter or digit."));
        if (ReservedNames.Contains(normalized))
            return (null, AgentResult<AgentSummary>.Fail(AgentErrors.InvalidArgument, $"'{normalized}' is a name CodeGraph reserves for itself."));
        if (await store.IsHumanIdentityAsync(normalized, ct))
            return (null, AgentResult<AgentSummary>.Fail(AgentErrors.InvalidArgument,
                $"'{normalized}' is a person CodeGraph has recorded; an agent cannot share a person's name."));
        return (normalized, null);
    }

    private Task<bool> HasLiveSessionAsync(AgentEntity agent, DateTime now, CancellationToken ct) =>
        store.HasLiveSessionAsync(agent.Name, now.AddSeconds(-idleSeconds), ct);

    private async Task<AgentSummary> SummaryAsync(AgentEntity agent, CancellationToken ct)
    {
        var agentTokens = await tokens.ListForAgentAsync(agent, ct);
        return new AgentSummary(agent.Id, agent.Name, agent.DisabledAt is null ? "active" : "disabled", agent.CreatedBy,
            agent.CreatedAt, agent.UpdatedAt, agent.DisabledAt, agentTokens.Count(token => token.Status == "active"));
    }

    private static string Normalize(string? name) => (name ?? "").Trim().ToLowerInvariant();

    private static AgentResult<T> NotFound<T>(long id) => AgentResult<T>.Fail(AgentErrors.NotFound, $"There is no agent {id}.");

    private static AgentResult<T> LiveSession<T>(AgentEntity agent, string action) =>
        AgentResult<T>.Fail(AgentErrors.Conflict,
            $"Agent '{agent.Name}' has a live session, so it cannot be {action} now: its sessions are bound to its name. Try again once the session ends or goes idle.");

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex NamePattern();
}

public static class AgentErrors
{
    public const string InvalidArgument = "invalid_argument";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
}

public sealed record AgentResult<T>(T? Value, string? ErrorCode, string? ErrorMessage)
{
    public static AgentResult<T> Ok(T value) => new(value, null, null);
    public static AgentResult<T> Fail(string code, string message) => new(default, code, message);
}
