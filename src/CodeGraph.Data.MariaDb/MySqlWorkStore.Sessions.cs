using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

// Sessions, heartbeats, handoffs, session-bound claims, recovery and the briefing (sc-27866).
//
// A session's claim is stored like any claim, with the session id as marker and claim_session_id
// set, and its claim_expires_at is the session's heartbeat deadline: every heartbeat pushes it
// forward while the claim is still live, so the S1 conditions on claim_expires_at decide liveness
// for session claims too, and a session that goes quiet loses its claims with nothing written. A
// heartbeat never revives a claim that already lapsed: the returning session is told claim_lost.
public sealed partial class MySqlWorkStore
{
    private const string SessionOperation = "session";

    private sealed class SessionRow
    {
        public string Id { get; init; } = "";
        public Guid ProjectId { get; init; }
        public string ProjectKey { get; init; } = "";
        public string Identity { get; init; } = "";
        public string Harness { get; init; } = "";
        public string? Model { get; init; }
        public string? Host { get; init; }
        public string? RepositoryName { get; init; }
        public string? Branch { get; init; }
        public string? Worktree { get; init; }
        public string? Intent { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime LastSeenAt { get; init; }
        public DateTime? EndedAt { get; init; }
    }

    private const string SessionColumns =
        "s.id, s.project_id, p.project_key, s.identity, s.harness, s.model, s.host, s.repository_name, s.branch, s.worktree, s.intent, " +
        "s.started_at, s.last_seen_at, s.ended_at";

    private DateTime StaleBefore(DateTime now) => now.AddSeconds(-idleSeconds);

    private bool IsLive(SessionRow row, DateTime now) => row.EndedAt is null && row.LastSeenAt > StaleBefore(now);

    private static Task<SessionRow?> ReadSessionRowAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid sessionId, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            $"SELECT {SessionColumns} FROM work_sessions s JOIN work_projects p ON p.id = s.project_id WHERE s.id = @id",
            new { id = sessionId.ToString() }, transaction, cancellationToken: ct));

    private SessionView ToView(SessionRow row, DateTime now) =>
        new(row.Id.ToString(), row.ProjectId, row.ProjectKey, row.Identity, row.Harness, row.Model, row.Host, row.RepositoryName, row.Branch,
            row.Worktree, row.Intent, IsLive(row, now), Utc(row.StartedAt), Utc(row.LastSeenAt), row.EndedAt is { } ended ? Utc(ended) : null);

    private static WorkActor ActorOf(SessionRow row) =>
        new(row.Identity, WorkActor.SessionKind, row.Id.ToString(), row.Harness, row.Model, row.Host);

    public Task<WorkCreated<SessionView>> StartSessionAsync(NewSessionSpec spec, WorkActor actor, string? keyHash, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            // Holding the project's row makes a keyed repeat find the first call's session.
            await HoldProjectAsync(connection, transaction, spec.ProjectId, ct);
            if (keyHash is not null && await FindKeyAsync(connection, transaction, spec.ProjectId, SessionOperation, keyHash, ct) is { EntityId: { } existing })
            {
                var earlier = (await ReadSessionRowAsync(connection, transaction, existing, ct))!;
                await transaction.CommitAsync(ct);
                return new WorkCreated<SessionView>(false, ToView(earlier, now));
            }

            // A member repository is stored in the project's spelling; any other name as given.
            var repository = spec.Repository is { } name
                ? await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT repository_name FROM work_project_repositories WHERE project_id = @projectId AND repository_name = @name LIMIT 1",
                    new { projectId = spec.ProjectId, name }, transaction, cancellationToken: ct)) ?? name
                : null;

            var id = Guid.NewGuid();
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO work_sessions (id, project_id, identity, harness, model, host, repository_name, branch, worktree, intent, started_at, last_seen_at) " +
                "VALUES (@id, @projectId, @identity, @harness, @model, @host, @repository, @branch, @worktree, @intent, @now, @now)",
                new
                {
                    id = id.ToString(), projectId = spec.ProjectId, identity = actor.Name, harness = spec.Harness, model = spec.Model, host = spec.Host,
                    repository, branch = spec.Branch, worktree = spec.Worktree, intent = spec.Intent, now
                },
                transaction, cancellationToken: ct));
            if (keyHash is not null)
                await InsertKeyAsync(connection, transaction, spec.ProjectId, SessionOperation, keyHash, id, null, now, ct);

            var row = (await ReadSessionRowAsync(connection, transaction, id, ct))!;
            await transaction.CommitAsync(ct);
            return new WorkCreated<SessionView>(true, ToView(row, now));
        }, ct);

    public Task<WorkActor> TouchSessionAsync(Guid sessionId, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            var row = await RequireOwnSessionAsync(connection, null, sessionId, actor, ct);
            if (row.EndedAt is { } ended)
                throw WorkException.SessionUnusable(WorkErrorCodes.InvalidArgument, $"Session {sessionId} ended at {Utc(ended):O}. Start a new session with start_session.");

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_sessions SET last_seen_at = @now WHERE id = @id", new { id = sessionId.ToString(), now }, cancellationToken: ct));
            // Only claims that are still live are renewed: one that lapsed stays lapsed, and the
            // session's next write on that item is refused claim_lost.
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET claim_expires_at = @deadline " +
                "WHERE claim_session_id = @id AND claim_holder IS NOT NULL AND claim_expires_at > @now",
                new { id = sessionId.ToString(), now, deadline = now.AddSeconds(idleSeconds) }, cancellationToken: ct));
            return ActorOf(row);
        }, ct);

    private static async Task<SessionRow> RequireOwnSessionAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid sessionId, WorkActor actor, CancellationToken ct)
    {
        var row = await ReadSessionRowAsync(connection, transaction, sessionId, ct)
            ?? throw WorkException.SessionUnusable(WorkErrorCodes.NotFound, $"There is no session {sessionId}. Start one with start_session.");
        if (!string.Equals(row.Identity, actor.Name, StringComparison.Ordinal))
            throw WorkException.SessionUnusable(WorkErrorCodes.InvalidArgument, $"Session {sessionId} belongs to another identity; a session is used only by the identity that started it.");
        return row;
    }

    public async Task<SessionView?> GetSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var row = await ReadSessionRowAsync(connection, null, sessionId, ct);
        return row is null ? null : ToView(row, Now());
    }

    public async Task<Guid?> FindOwningProjectAsync(string repository, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var owners = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT project_id FROM work_project_repositories WHERE repository_name = @repository AND role = 'owned'",
            new { repository }, cancellationToken: ct))).ToList();
        return owners.Count == 1 ? owners[0] : null;
    }

    public Task<EndSessionResult> EndSessionAsync(Guid sessionId, Guid? itemId, HandoffSpec? handoff, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_sessions SET last_seen_at = last_seen_at WHERE id = @id", new { id = sessionId.ToString() }, transaction, cancellationToken: ct));
            var session = await RequireOwnSessionAsync(connection, transaction, sessionId, actor, ct);
            if (session.EndedAt is not null)
            {
                // A repeat (say, after a lost answer): the session as it ended, nothing written again.
                await transaction.CommitAsync(ct);
                return new EndSessionResult(ToView(session, now), []);
            }

            var sessionActor = ActorOf(session);
            var held = (await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT id FROM work_items WHERE claim_session_id = @id AND claim_holder IS NOT NULL ORDER BY id",
                new { id = sessionId.ToString() }, transaction, cancellationToken: ct))).ToList();

            Guid? target = null;
            if (handoff is not null)
            {
                if (itemId is { } named)
                {
                    target = named;
                }
                else
                {
                    var open = (await connection.QueryAsync<Guid>(new CommandDefinition(
                        "SELECT item_id FROM work_session_claims WHERE session_id = @id AND released_at IS NULL",
                        new { id = sessionId.ToString() }, transaction, cancellationToken: ct))).Union(held).Distinct().ToList();
                    if (open.Count != 1)
                    {
                        var keys = new List<string>();
                        foreach (var candidate in open)
                            keys.Add(await KeyOfAsync(connection, transaction, candidate, ct));
                        throw WorkException.Invalid(open.Count == 0
                            ? "The session holds no item, so the handoff needs item. Nothing was changed."
                            : $"The session holds {string.Join(", ", keys)}; name the item the handoff is about. Nothing was changed.");
                    }

                    target = open[0];
                }
            }

            var touched = held.Concat(target is { } t ? [t] : []).Distinct().OrderBy(id => id).ToArray();
            if (touched.Length > 0)
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_items SET version = version WHERE id IN @touched", new { touched }, transaction, cancellationToken: ct));

            // A session that lost its claim on the handoff's item to another holder must not write
            // onto it: refused claim_lost naming the holder, nothing written, the session stays open.
            // An item that is merely unclaimed now may still take the handoff.
            if (target is { } handoffItem && await HasOpenSessionClaimAsync(connection, transaction, sessionId.ToString(), handoffItem, ct) &&
                await ReadItemRowAsync(connection, transaction, handoffItem, ct) is { } handoffRow &&
                handoffRow.HasLiveClaim(now) && handoffRow.ClaimSessionId != sessionId.ToString())
                throw await ClaimLostAsync(connection, transaction, handoffRow, now, ct);

            var entries = new List<PendingChange>();
            var released = new List<WorkRow>();
            HandoffView? written = null;
            foreach (var id in touched)
            {
                var before = await ReadItemRowAsync(connection, transaction, id, ct)
                    ?? throw WorkException.NotFound($"There is no item {id}.");
                if (before.ProjectId != session.ProjectId && id == target)
                    throw WorkException.Invalid($"{before.Key} belongs to another project than the session. Nothing was changed.");

                var changes = new JsonArray();
                var releases = held.Contains(id) && before.ClaimSessionId == sessionId.ToString();
                if (releases)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE work_items SET claim_holder = NULL, claim_marker = NULL, claim_session_id = NULL, claimed_at = NULL, claim_expires_at = NULL WHERE id = @id",
                        new { id }, transaction, cancellationToken: ct));
                }

                if (id == target)
                {
                    written = await InsertHandoffAsync(connection, transaction, id, handoff!, sessionActor, now, ct);
                    changes.Add(Change("handoff", (JsonNode?)null, JsonValue.Create(written.Id.ToString())));
                }

                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_items SET version = version + 1, updated_at = @now WHERE id = @id", new { id, now }, transaction, cancellationToken: ct));
                var after = (await ReadItemRowAsync(connection, transaction, id, ct))!;
                foreach (var change in Diff(ClaimSnapshot(before), ClaimSnapshot(after)))
                    changes.Add(change!.DeepClone());
                changes.Add(Change("version", before.Version, after.Version));
                entries.Add(new PendingChange(after.ProjectId, after.ProjectKey, WorkEntityKind.Item, after.Id, after.Key, WorkChangeKinds.ItemUpdated, changes));
                if (releases)
                    released.Add(new WorkRow(after.Id, after.Key, "item", after.Title, WorkStatuses.Parse(after.Status), Version: after.Version));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_session_claims SET released_at = @now WHERE session_id = @id AND released_at IS NULL",
                new { id = sessionId.ToString(), now }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_sessions SET ended_at = @now, last_seen_at = @now WHERE id = @id",
                new { id = sessionId.ToString(), now }, transaction, cancellationToken: ct));
            var ended = (await ReadSessionRowAsync(connection, transaction, sessionId, ct))!;
            await CommitAsync(connection, transaction, entries, sessionActor, now, ct);
            return new EndSessionResult(ToView(ended, now), released, written);
        }, ct);

    // ---- Session claims ------------------------------------------------------------------------

    private static Task OpenSessionClaimAsync(MySqlConnection connection, MySqlTransaction transaction, string sessionId, Guid itemId, DateTime now, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO work_session_claims (session_id, item_id, claimed_at, released_at) VALUES (@sessionId, @itemId, @now, NULL) " +
            "ON DUPLICATE KEY UPDATE claimed_at = @now, released_at = NULL",
            new { sessionId, itemId, now }, transaction, cancellationToken: ct));

    private static Task CloseSessionClaimAsync(MySqlConnection connection, MySqlTransaction transaction, string sessionId, Guid itemId, DateTime now, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_session_claims SET released_at = @now WHERE session_id = @sessionId AND item_id = @itemId AND released_at IS NULL",
            new { sessionId, itemId, now }, transaction, cancellationToken: ct));

    private static async Task<bool> HasOpenSessionClaimAsync(MySqlConnection connection, MySqlTransaction transaction, string sessionId, Guid itemId, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM work_session_claims WHERE session_id = @sessionId AND item_id = @itemId AND released_at IS NULL",
            new { sessionId, itemId }, transaction, cancellationToken: ct)) > 0;

    /// <summary>The holder of a claim as one line: identity, and the session with its harness and model.</summary>
    private static async Task<HolderView> HolderAsync(MySqlConnection connection, MySqlTransaction? transaction, string identity, string? sessionId, CancellationToken ct)
    {
        if (sessionId is null || !Guid.TryParse(sessionId, out var id))
            return new HolderView(identity);
        var session = await ReadSessionRowAsync(connection, transaction, id, ct);
        return session is null
            ? new HolderView(identity, sessionId)
            : new HolderView(identity, sessionId, session.Harness, session.Model, session.Host);
    }

    private static string Describe(HolderView holder) =>
        holder.SessionId is null
            ? holder.Identity
            : $"{holder.Identity} (session {holder.SessionId}{(holder.Harness is null ? string.Empty : $", {holder.Harness}")}" +
              $"{(holder.Model is null ? string.Empty : $" {holder.Model}")}{(holder.Host is null ? string.Empty : $" on {holder.Host}")})";

    /// <summary>
    /// The refusal of a write by a session whose claim on the item lapsed or passed to someone else,
    /// naming the holder now (or saying it is unclaimed), with the item as it is now.
    /// </summary>
    private async Task<WorkException> ClaimLostAsync(MySqlConnection connection, MySqlTransaction? transaction, ItemRow row, DateTime now, CancellationToken ct)
    {
        var view = await ToViewAsync(connection, transaction, row, ct);
        if (row.HasLiveClaim(now))
        {
            var holder = await HolderAsync(connection, transaction, row.ClaimHolder!, row.ClaimSessionId, ct);
            return new WorkException(WorkErrorCodes.ClaimLost,
                $"Your session's claim on {row.Key} lapsed and it is now claimed by {Describe(holder)} since {Utc(row.ClaimedAt!.Value):O}. " +
                "Nothing was changed. Let go of it with release_claim, or coordinate with the holder.", view);
        }

        return new WorkException(WorkErrorCodes.ClaimLost,
            $"Your session's claim on {row.Key} lapsed; it is unclaimed now (status {WorkStatuses.Name(WorkStatuses.Parse(row.Status))}). " +
            "Nothing was changed. Reclaim it with claim while it is unclaimed, or let go of it with release_claim.", view);
    }

    // ---- Handoffs --------------------------------------------------------------------------------

    private sealed class HandoffRow
    {
        public Guid Id { get; init; }
        public string Author { get; init; } = "";
        public string? AuthorSessionId { get; init; }
        public string? Harness { get; init; }
        public string? Done { get; init; }
        public string DecisionsJson { get; init; } = "[]";
        public string OpenIssuesJson { get; init; } = "[]";
        public string CorrectionsJson { get; init; } = "[]";
        public string? NextAction { get; init; }
        public string? Branch { get; init; }
        public string? CommitSha { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private static async Task<HandoffView> InsertHandoffAsync(MySqlConnection connection, MySqlTransaction transaction, Guid itemId, HandoffSpec handoff, WorkActor actor, DateTime now, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO work_handoffs (id, item_id, author, author_kind, author_session_id, done, decisions_json, open_issues_json, corrections_json, next_action, branch, commit_sha, created_at) " +
            "VALUES (@id, @itemId, @author, @kind, @session, @done, @decisions, @openIssues, @corrections, @nextAction, @branch, @commit, @now)",
            new
            {
                id, itemId, author = actor.Name, kind = actor.Kind, session = actor.SessionId, done = handoff.Done,
                decisions = JsonSerializer.Serialize(handoff.Decisions), openIssues = JsonSerializer.Serialize(handoff.OpenIssues),
                corrections = JsonSerializer.Serialize(handoff.Corrections), nextAction = handoff.NextAction, branch = handoff.Branch,
                commit = handoff.Commit, now
            },
            transaction, cancellationToken: ct));
        return (await ReadHandoffsAsync(connection, transaction, itemId, limit: 1, ct))[0];
    }

    /// <summary>The handoffs of an item, newest first.</summary>
    private static async Task<IReadOnlyList<HandoffView>> ReadHandoffsAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid itemId, int? limit, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<HandoffRow>(new CommandDefinition(
            "SELECT h.id, h.author, h.author_session_id, s.harness, h.done, h.decisions_json, h.open_issues_json, h.corrections_json, h.next_action, " +
            "h.branch, h.commit_sha, h.created_at FROM work_handoffs h LEFT JOIN work_sessions s ON s.id = h.author_session_id " +
            "WHERE h.item_id = @itemId ORDER BY h.created_at DESC, h.id" + (limit is { } count ? $" LIMIT {count}" : string.Empty),
            new { itemId }, transaction, cancellationToken: ct));
        return rows.Select(row => new HandoffView(
            row.Id, row.Author, Utc(row.CreatedAt), row.AuthorSessionId, row.Harness, row.Done,
            NonEmptyList(row.DecisionsJson), NonEmptyList(row.OpenIssuesJson), NonEmptyList(row.CorrectionsJson),
            row.NextAction, row.Branch, row.CommitSha)).ToList();

        static IReadOnlyList<string>? NonEmptyList(string json) =>
            JsonSerializer.Deserialize<List<string>>(json) is { Count: > 0 } list ? list : null;
    }

    // ---- Recovery --------------------------------------------------------------------------------

    /// <summary>
    /// What the next holder of an in-progress item needs (§5.9), read before its claim is replaced:
    /// the previous holder, branch and PR links, the latest handoff, and when there is none the last
    /// change the previous holder made to the item.
    /// </summary>
    private async Task<RecoveryView> LoadRecoveryAsync(MySqlConnection connection, MySqlTransaction? transaction, ItemRow before, CancellationToken ct)
    {
        HolderView? previous = null;
        if (before.ClaimHolder is { } holder)
        {
            previous = await HolderAsync(connection, transaction, holder, before.ClaimSessionId, ct);
        }
        else
        {
            var lastSession = await connection.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(
                "SELECT c.session_id FROM work_session_claims c WHERE c.item_id = @id ORDER BY c.claimed_at DESC LIMIT 1",
                new { id = before.Id }, transaction, cancellationToken: ct));
            if (lastSession is not null && Guid.TryParse(lastSession, out var sessionId) &&
                await ReadSessionRowAsync(connection, transaction, sessionId, ct) is { } session)
                previous = new HolderView(session.Identity, lastSession, session.Harness, session.Model, session.Host);
        }

        var (branches, prs) = await ReadWorkLinksAsync(connection, transaction, before.Id, ct);
        var handoff = (await ReadHandoffsAsync(connection, transaction, before.Id, limit: 1, ct)).FirstOrDefault();
        ChangeView? last = null;
        if (handoff is null)
        {
            var (where, parameters) = previous?.SessionId is { } previousSession
                ? ("c.actor_session_id = @who", new { id = before.Id, who = previousSession })
                : previous is not null
                    ? ("c.actor_name = @who", new { id = before.Id, who = previous.Identity })
                    : ("1 = 1", new { id = before.Id, who = "" });
            var row = await connection.QueryFirstOrDefaultAsync<ChangeRow>(new CommandDefinition(
                "SELECT c.sequence, c.project_id, c.project_key, c.entity_kind, c.entity_id, c.entity_key, c.kind, c.actor_name, c.actor_kind, c.actor_session_id, " +
                "s.harness, s.model, s.host, c.changes, c.created_at FROM work_changes c LEFT JOIN work_sessions s ON s.id = c.actor_session_id " +
                $"WHERE c.entity_id = @id AND {where} ORDER BY c.sequence DESC LIMIT 1",
                parameters, transaction, cancellationToken: ct));
            last = row is null ? null : ToView(row);
        }

        return new RecoveryView(previous, branches, prs, handoff, last);
    }

    private static async Task<(IReadOnlyList<string>? Branches, IReadOnlyList<string>? Prs)> ReadWorkLinksAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid itemId, CancellationToken ct)
    {
        var links = (await connection.QueryAsync<(string Kind, string Url)>(new CommandDefinition(
            "SELECT kind, url FROM work_item_links WHERE item_id = @itemId AND kind IN ('branch', 'pr') ORDER BY created_at, id",
            new { itemId }, transaction, cancellationToken: ct))).ToList();
        var branches = links.Where(link => link.Kind == "branch").Select(link => link.Url).ToList();
        var prs = links.Where(link => link.Kind == "pr").Select(link => link.Url).ToList();
        return (branches.Count == 0 ? null : branches, prs.Count == 0 ? null : prs);
    }

    // ---- Briefing --------------------------------------------------------------------------------

    private const int MaxBriefingItems = 20;
    private const int MaxBriefingSessions = 50;

    public async Task<BriefingView> ReadBriefingAsync(Guid sessionId, Guid? focusItemId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var now = Now();
        var session = await ReadSessionRowAsync(connection, null, sessionId, ct)
            ?? throw WorkException.SessionUnusable(WorkErrorCodes.NotFound, $"There is no session {sessionId}.");
        var project = (await ReadProjectRowAsync(connection, null, session.ProjectId, ct))!;
        var goals = JsonSerializer.Deserialize<List<string>>(project.GoalsJson) ?? [];

        // My work: the live claims of this session in its project.
        var mine = (await connection.QueryAsync<ItemRow>(new CommandDefinition(
            $"SELECT {ItemColumns} FROM work_items i JOIN work_projects p ON p.id = i.project_id " +
            "WHERE i.project_id = @projectId AND i.claim_session_id = @sessionId AND i.claim_holder IS NOT NULL AND i.claim_expires_at > @now " +
            $"AND i.status NOT IN ('complete', 'closed') ORDER BY i.claimed_at, i.number LIMIT {MaxBriefingItems}",
            new { projectId = session.ProjectId, sessionId = sessionId.ToString(), now }, cancellationToken: ct))).ToList();
        var myWork = new List<BriefingItem>();
        foreach (var row in mine)
            myWork.Add(await BriefingItemAsync(connection, row, recoverable: false, previous: null, ct));

        BriefingItem? focus = null;
        if (focusItemId is { } focusId && mine.All(row => row.Id != focusId) && await ReadItemRowAsync(connection, null, focusId, ct) is { } focusRow)
        {
            if (focusRow.ProjectId != session.ProjectId)
                throw WorkException.Invalid($"{focusRow.Key} is not an item of {project.ProjectKey}, the session's project.");
            focus = await BriefingItemAsync(connection, focusRow, recoverable: await IsRecoverableAsync(connection, focusRow, now, ct), previous: null, ct);
        }

        // Other live sessions of the project and the items they hold; the session's repository and
        // branch first.
        var others = (await connection.QueryAsync<SessionRow>(new CommandDefinition(
            $"SELECT {SessionColumns} FROM work_sessions s JOIN work_projects p ON p.id = s.project_id " +
            "WHERE s.project_id = @projectId AND s.ended_at IS NULL AND s.last_seen_at > @staleBefore AND s.id <> @sessionId " +
            "ORDER BY (s.repository_name <=> @repository AND s.branch <=> @branch) DESC, (s.repository_name <=> @repository) DESC, s.last_seen_at DESC " +
            $"LIMIT {MaxBriefingSessions}",
            new { projectId = session.ProjectId, staleBefore = StaleBefore(now), sessionId = sessionId.ToString(), repository = session.RepositoryName, branch = session.Branch },
            cancellationToken: ct))).ToList();
        var holds = others.Count == 0
            ? []
            : (await connection.QueryAsync<(string SessionId, string ProjectKey, int Number, string Title, string Status)>(new CommandDefinition(
                "SELECT i.claim_session_id, p.project_key, i.number, i.title, i.status FROM work_items i JOIN work_projects p ON p.id = i.project_id " +
                "WHERE i.project_id = @projectId AND i.claim_session_id IN @ids AND i.claim_holder IS NOT NULL AND i.claim_expires_at > @now " +
                "AND i.status NOT IN ('complete', 'closed') ORDER BY i.number",
                new { projectId = session.ProjectId, ids = others.Select(other => other.Id.ToString()).ToArray(), now }, cancellationToken: ct))).ToList();
        var otherSessions = others.Select(other =>
        {
            var held = holds.Where(hold => hold.SessionId == other.Id.ToString())
                .Select(hold => new BriefingHold(WorkKeys.Item(hold.ProjectKey, hold.Number), hold.Title, WorkStatuses.Parse(hold.Status))).ToList();
            return new BriefingSession(other.Id.ToString(), other.Identity, other.Harness, Utc(other.LastSeenAt), other.Model, other.Host,
                other.RepositoryName, other.Branch, other.Intent, held.Count == 0 ? null : held);
        }).ToList();

        var suggested = await SuggestAsync(connection, session, now, ct);

        return new BriefingView(
            sessionId.ToString(), 0, 0,
            new BriefingProject(project.ProjectKey, project.Name, goals.Count == 0 ? null : goals),
            myWork.Count == 0 ? null : myWork,
            focus,
            otherSessions.Count == 0 ? null : otherSessions,
            suggested);
    }

    // In progress, no live claim, and a claim in its history (§5.9: "whose claim lapsed"): an item a
    // human moved to in progress, never claimed, is not recoverable.
    private static async Task<bool> IsRecoverableAsync(MySqlConnection connection, ItemRow row, DateTime now, CancellationToken ct) =>
        WorkStatuses.Parse(row.Status) == WorkStatus.InProgress && !row.HasLiveClaim(now) &&
        (row.ClaimHolder is not null || await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM work_session_claims WHERE item_id = @id", new { id = row.Id }, cancellationToken: ct)) > 0);

    private async Task<BriefingItem> BriefingItemAsync(MySqlConnection connection, ItemRow row, bool recoverable, HolderView? previous, CancellationToken ct)
    {
        var (branches, prs) = await ReadWorkLinksAsync(connection, null, row.Id, ct);
        var handoff = (await ReadHandoffsAsync(connection, null, row.Id, limit: 1, ct)).FirstOrDefault();
        return new BriefingItem(
            row.Key, row.Title, WorkStatuses.Parse(row.Status), ParseName<WorkPriority>(row.Priority), row.RepositoryName,
            recoverable ? true : null, branches, prs, handoff, previous);
    }

    /// <summary>
    /// One item to suggest, not claimed: a recoverable item before a ready one (as next_work offers
    /// them), the session's repository first, then the item whose title shares most words with the
    /// session's intent, then the queue order.
    /// </summary>
    private async Task<BriefingItem?> SuggestAsync(MySqlConnection connection, SessionRow session, DateTime now, CancellationToken ct)
    {
        foreach (var recoverable in new[] { true, false })
        {
            var rows = (await connection.QueryAsync<ItemRow>(new CommandDefinition(
                $"SELECT {ItemColumns} FROM work_items i JOIN work_projects p ON p.id = i.project_id " +
                $"WHERE i.project_id = @projectId AND {(recoverable ? RecoverableCondition : AvailableCondition)} " +
                "ORDER BY (i.repository_name <=> @repository) DESC, i.priority_rank, i.sort_order, i.created_at, i.number LIMIT 25",
                new { projectId = session.ProjectId, repository = session.RepositoryName, now }, cancellationToken: ct))).ToList();
            if (rows.Count == 0)
                continue;

            var words = IntentWords(session.Intent);
            var best = rows
                .Select((row, index) => (row, index, sameRepository: row.RepositoryName is not null && row.RepositoryName == session.RepositoryName,
                    score: words.Count(word => row.Title.Contains(word, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(entry => entry.sameRepository)
                .ThenByDescending(entry => entry.score)
                .ThenBy(entry => entry.index)
                .First().row;
            var previous = recoverable ? (await LoadRecoveryAsync(connection, null, best, ct)).PreviousHolder : null;
            return await BriefingItemAsync(connection, best, recoverable, previous, ct);
        }

        return null;
    }

    private static IReadOnlyList<string> IntentWords(string? intent) =>
        string.IsNullOrWhiteSpace(intent)
            ? []
            : intent.Split([' ', ',', '.', ';', ':', '-', '/', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
}
