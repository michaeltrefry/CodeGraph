using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

// Resources and holds (sc-27872; design §5.6, §5.9).
//
// Occupancy, not session chatter: a hold lives for its own ttl and its own renewals. Nothing here
// reads a session's heartbeat to decide whether a hold is still held. The heartbeat is read only
// to LABEL a live hold whose session went silent (owner unreachable) and to drop WAITERS of a dead
// or silent session, who would otherwise take a slot nobody is there to use.
//
// Every change to the holds of one resource takes the resource's row first, so capacity accounting,
// expiry, promotion of waiters and grants are one writer at a time per resource. A change begins
// with a sweep under that lock: lapsed holds are expired, dead waiters cancelled and free slots go
// to waiting entries in order (priority, then first come first served).
public sealed partial class MySqlWorkStore
{
    private sealed class ResourceRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public string ProjectKey { get; init; } = "";
        public string Name { get; init; } = "";
        public int Capacity { get; init; }
        public string LabelsJson { get; init; } = "[]";
    }

    private sealed class HoldRow
    {
        public Guid Id { get; init; }
        public long Seq { get; init; }
        public Guid ResourceId { get; init; }
        public string ResourceName { get; init; } = "";
        public string ProjectKey { get; init; } = "";
        public string Identity { get; init; } = "";
        public string? SessionId { get; init; }
        public int? ItemNumber { get; init; }
        public string Purpose { get; init; } = "";
        public string Priority { get; init; } = "medium";
        public string State { get; init; } = "";
        public int TtlSeconds { get; init; }
        public DateTime RequestedAt { get; init; }
        public DateTime? WaitExpiresAt { get; init; }
        public DateTime? GrantedAt { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public DateTime? RenewedAt { get; init; }
        public DateTime? EndedAt { get; init; }
        public string? SessionHarness { get; init; }
        public string? SessionModel { get; init; }
        public string? SessionHost { get; init; }
        public DateTime? SessionLastSeenAt { get; init; }
        public DateTime? SessionEndedAt { get; init; }
    }

    private const string HoldSelect =
        "SELECT h.id, h.seq, h.resource_id, r.name AS resource_name, p.project_key, h.identity, h.session_id, i.number AS item_number, " +
        "h.purpose, h.priority, h.state, h.ttl_seconds, h.requested_at, h.wait_expires_at, h.granted_at, h.expires_at, h.renewed_at, h.ended_at, " +
        "s.harness AS session_harness, s.model AS session_model, s.host AS session_host, s.last_seen_at AS session_last_seen_at, s.ended_at AS session_ended_at " +
        "FROM work_holds h JOIN work_resources r ON r.id = h.resource_id JOIN work_projects p ON p.id = h.project_id " +
        "LEFT JOIN work_items i ON i.id = h.item_id LEFT JOIN work_sessions s ON s.id = h.session_id ";

    // A hold that occupies a slot now: its own expiry decides, whatever its session does.
    private const string LiveHeld = "h.state = 'held' AND h.expires_at > @now";

    // A waiter still in the queue: inside its wait window, and its session (if any) alive.
    private const string LiveWaiting =
        "h.state = 'waiting' AND h.wait_expires_at > @now AND (h.session_id IS NULL OR (s.ended_at IS NULL AND s.last_seen_at > @staleBefore))";

    private static int Rank(string priority) => priority switch { "critical" => 0, "high" => 1, "medium" => 2, _ => 3 };

    // ---- Views -------------------------------------------------------------------------------------

    private HoldView ToHoldView(HoldRow row, DateTime now, int? queuePosition = null)
    {
        // A lapsed hold reads expired whether or not a sweep has written that yet; a waiter past its
        // window or whose session is gone reads cancelled.
        var state = row.State;
        var ended = row.EndedAt;
        if (state == WorkHoldStates.Held && row.ExpiresAt is { } expires && expires <= now)
        {
            state = WorkHoldStates.Expired;
            ended ??= expires;
        }
        else if (state == WorkHoldStates.Waiting && ((row.WaitExpiresAt is { } window && window <= now) || SessionGone(row, now)))
        {
            state = WorkHoldStates.Cancelled;
        }

        // The occupancy read of §5.9: still held, its ttl live, but its session silent past the idle
        // threshold. A label on the hold, never a reason to free it.
        var unreachable = state == WorkHoldStates.Held && row.SessionId is not null && SessionGone(row, now);
        DateTime? lastSeen = row.SessionLastSeenAt is { } seen ? Utc(seen) : null;
        var holder = row.SessionId is null
            ? new HolderView(row.Identity)
            : new HolderView(row.Identity, row.SessionId, row.SessionHarness, row.SessionModel, row.SessionHost);
        return new HoldView(
            row.Id, row.ResourceName, row.ProjectKey, holder, row.Purpose, state, ParseName<WorkPriority>(row.Priority), row.TtlSeconds, Utc(row.RequestedAt),
            row.ItemNumber is { } number ? WorkKeys.Item(row.ProjectKey, number) : null,
            row.GrantedAt is { } granted ? Utc(granted) : null,
            row.ExpiresAt is { } expiry ? Utc(expiry) : null,
            row.RenewedAt is { } renewed ? Utc(renewed) : null,
            ended is { } end ? Utc(end) : null,
            state == WorkHoldStates.Waiting ? queuePosition : null,
            unreachable ? true : null,
            unreachable && lastSeen is { } since ? since.AddSeconds(idleSeconds) : null,
            unreachable ? lastSeen : null);
    }

    // The session of the row is ended or silent past the idle threshold (or gone).
    private bool SessionGone(HoldRow row, DateTime now) =>
        row.SessionId is not null && (row.SessionLastSeenAt is null || row.SessionEndedAt is not null || row.SessionLastSeenAt <= StaleBefore(now));

    private static async Task<ResourceRow?> ReadResourceRowAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid resourceId, CancellationToken ct) =>
        await connection.QuerySingleOrDefaultAsync<ResourceRow>(new CommandDefinition(
            "SELECT r.id, r.project_id, p.project_key, r.name, r.capacity, r.labels_json FROM work_resources r JOIN work_projects p ON p.id = r.project_id WHERE r.id = @resourceId",
            new { resourceId }, transaction, cancellationToken: ct));

    private static async Task<HoldRow?> ReadHoldRowAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid holdId, CancellationToken ct) =>
        await connection.QuerySingleOrDefaultAsync<HoldRow>(new CommandDefinition(
            HoldSelect + "WHERE h.id = @holdId", new { holdId }, transaction, cancellationToken: ct));

    /// <summary>Resources with their live holders and queued waiters, as of <paramref name="now"/> (nothing is written).</summary>
    private async Task<List<ResourceView>> BuildResourceViewsAsync(MySqlConnection connection, MySqlTransaction? transaction, IReadOnlyList<ResourceRow> resources, DateTime now, CancellationToken ct)
    {
        if (resources.Count == 0)
            return [];
        var ids = resources.Select(resource => resource.Id).ToArray();
        var rows = (await connection.QueryAsync<HoldRow>(new CommandDefinition(
            HoldSelect + $"WHERE h.resource_id IN @ids AND (({LiveHeld}) OR ({LiveWaiting})) ORDER BY h.seq",
            new { ids, now, staleBefore = StaleBefore(now) }, transaction, cancellationToken: ct))).ToList();

        var views = new List<ResourceView>();
        foreach (var resource in resources)
        {
            var ofResource = rows.Where(row => row.ResourceId == resource.Id).ToList();
            var holders = ofResource.Where(row => row.State == WorkHoldStates.Held)
                .OrderBy(row => row.GrantedAt).ThenBy(row => row.Seq).Select(row => ToHoldView(row, now)).ToList();
            var waiters = ofResource.Where(row => row.State == WorkHoldStates.Waiting)
                .OrderBy(row => Rank(row.Priority)).ThenBy(row => row.Seq)
                .Select((row, index) => ToHoldView(row, now, index + 1)).ToList();
            var labels = JsonSerializer.Deserialize<List<string>>(resource.LabelsJson) ?? [];
            views.Add(new ResourceView(
                resource.Id, resource.ProjectKey, resource.Name, resource.Capacity, labels, holders.Count, holders.Count < resource.Capacity,
                holders.Take(WorkHoldLimits.MaxListed).ToList(),
                waiters.Count == 0 ? null : waiters.Take(WorkHoldLimits.MaxListed).ToList()));
        }

        return views;
    }

    // ---- Sweep -------------------------------------------------------------------------------------

    /// <summary>
    /// Brings the holds of a resource up to <paramref name="now"/>; the caller holds the resource's row.
    /// Lapsed holds become expired; waiters past their window or of an ended or silent session are
    /// cancelled; free slots go to the waiting entries in order. A hold is never touched because its
    /// session is quiet.
    /// </summary>
    private async Task SweepResourceAsync(MySqlConnection connection, MySqlTransaction transaction, ResourceRow resource, DateTime now, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_holds SET state = 'expired', ended_at = expires_at WHERE resource_id = @resourceId AND state = 'held' AND expires_at <= @now",
            new { resourceId = resource.Id, now }, transaction, cancellationToken: ct));
        // The sessions of waiters that are gone are found with a plain read, so the UPDATE below
        // touches work_holds only and never locks a session row (end_session holds a session row
        // before it wants a resource row).
        var goneSessions = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT h.session_id FROM work_holds h LEFT JOIN work_sessions s ON s.id = h.session_id " +
            "WHERE h.resource_id = @resourceId AND h.state = 'waiting' AND h.session_id IS NOT NULL AND " +
            "(s.id IS NULL OR s.ended_at IS NOT NULL OR s.last_seen_at <= @staleBefore)",
            new { resourceId = resource.Id, staleBefore = StaleBefore(now) }, transaction, cancellationToken: ct))).ToArray();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_holds SET state = 'cancelled', ended_at = @now " +
            "WHERE resource_id = @resourceId AND state = 'waiting' AND (wait_expires_at <= @now OR session_id IN @goneSessions)",
            new { resourceId = resource.Id, now, goneSessions = goneSessions.Length == 0 ? new[] { "" } : goneSessions }, transaction, cancellationToken: ct));
        await PromoteWaitersAsync(connection, transaction, resource, now, ct);
    }

    // A waiter promoted here while it is not polling holds the slot from the promotion until its
    // ttl runs out or it releases (documented in the acquire tool description): its entry was
    // live and its session alive, and promotion cannot know whether it still wants the slot.
    private static async Task PromoteWaitersAsync(MySqlConnection connection, MySqlTransaction transaction, ResourceRow resource, DateTime now, CancellationToken ct)
    {
        var used = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM work_holds WHERE resource_id = @resourceId AND state = 'held' FOR UPDATE", new { resourceId = resource.Id }, transaction, cancellationToken: ct));
        var free = resource.Capacity - used;
        if (free <= 0)
            return;

        var next = (await connection.QueryAsync<(Guid Id, int TtlSeconds)>(new CommandDefinition(
            $"SELECT id, ttl_seconds FROM work_holds WHERE resource_id = @resourceId AND state = 'waiting' ORDER BY priority_rank, seq LIMIT {free} FOR UPDATE",
            new { resourceId = resource.Id }, transaction, cancellationToken: ct))).ToList();
        foreach (var waiter in next)
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_holds SET state = 'held', granted_at = @now, expires_at = @expires, wait_expires_at = NULL WHERE id = @id",
                new { id = waiter.Id, now, expires = now.AddSeconds(waiter.TtlSeconds) }, transaction, cancellationToken: ct));
    }

    // Under the resource's row every read must see what the previous holder of the row committed, so
    // these transactions read committed: a repeatable-read snapshot taken before the row was granted
    // would count holds as they were before, and capacity would be exceeded.
    private const System.Data.IsolationLevel HoldIsolation = System.Data.IsolationLevel.ReadCommitted;

    // Takes the resource's row; every change of its holds runs under it.
    private static async Task LockResourceAsync(MySqlConnection connection, MySqlTransaction transaction, Guid resourceId, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM work_resources WHERE id = @resourceId FOR UPDATE", new { resourceId }, transaction, cancellationToken: ct));

    // ---- acquire -----------------------------------------------------------------------------------

    public Task<AcquireResult> AcquireAsync(AcquireSpec spec, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(HoldIsolation, ct);

            // Lock order (see the class comment): the item row, shared, before any resource row.
            if (spec.ItemId is { } itemId &&
                await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                    "SELECT id FROM work_items WHERE id = @itemId LOCK IN SHARE MODE", new { itemId }, transaction, cancellationToken: ct)) is null)
                throw WorkException.NotFound($"There is no item {itemId}.");

            // The resource is created by its first acquire. A concurrent first acquire waits on the
            // unique key and then finds the row; either way the row is then locked.
            var newId = Guid.NewGuid();
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO work_resources (id, project_id, name, capacity, labels_json, created_by, created_at) " +
                "VALUES (@id, @projectId, @name, @capacity, @labels, @createdBy, @now) ON DUPLICATE KEY UPDATE id = id",
                new
                {
                    id = newId, projectId = spec.ProjectId, name = spec.Resource, capacity = spec.Capacity ?? 1,
                    labels = JsonSerializer.Serialize(spec.Labels ?? []), createdBy = actor.Name, now
                }, transaction, cancellationToken: ct));
            var resourceId = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
                "SELECT id FROM work_resources WHERE project_id = @projectId AND name = @name", new { projectId = spec.ProjectId, name = spec.Resource },
                transaction, cancellationToken: ct));
            var created = resourceId == newId;
            await LockResourceAsync(connection, transaction, resourceId, ct);
            var resource = (await ReadResourceRowAsync(connection, transaction, resourceId, ct))!;

            if (!created)
                RequireSameDeclaration(resource, spec);

            await SweepResourceAsync(connection, transaction, resource, now, ct);

            // A repeat of a keyed call answers the hold it made, whatever became of it since.
            HoldRow? existing = null;
            if (spec.KeyHash is not null)
            {
                existing = await connection.QueryFirstOrDefaultAsync<HoldRow>(new CommandDefinition(
                    HoldSelect + "WHERE h.project_id = @projectId AND h.key_hash = @keyHash", new { projectId = spec.ProjectId, keyHash = spec.KeyHash },
                    transaction, cancellationToken: ct));
                if (existing is not null && existing.ResourceId != resourceId)
                    throw WorkException.Invalid($"idempotency_key was used for another resource ({existing.ResourceName}). Nothing was changed.");
            }

            // A holder has one live hold or place in the queue per resource: asking again answers it.
            existing ??= await connection.QueryFirstOrDefaultAsync<HoldRow>(new CommandDefinition(
                HoldSelect + "WHERE h.resource_id = @resourceId AND h.state IN ('held', 'waiting') AND " +
                (actor.SessionId is null ? "h.session_id IS NULL AND h.identity = @identity" : "h.session_id = @sessionId") + " ORDER BY h.seq LIMIT 1",
                new { resourceId, identity = actor.Name, sessionId = actor.SessionId }, transaction, cancellationToken: ct));
            var reused = existing is not null;

            Guid? holdId = existing?.Id;
            if (existing is null)
            {
                var used = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM work_holds WHERE resource_id = @resourceId AND state = 'held'", new { resourceId }, transaction, cancellationToken: ct));
                // A free slot after the sweep means nobody queued is left waiting for it.
                var grant = used < resource.Capacity;
                if (grant || spec.Wait)
                {
                    holdId = Guid.NewGuid();
                    try
                    {
                    await connection.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO work_holds (id, resource_id, project_id, identity, session_id, item_id, purpose, priority, state, ttl_seconds, requested_at, " +
                        "wait_expires_at, granted_at, expires_at, key_hash) VALUES (@id, @resourceId, @projectId, @identity, @sessionId, @itemId, @purpose, @priority, " +
                        "@state, @ttl, @now, @waitExpires, @grantedAt, @expires, @keyHash)",
                        new
                        {
                            id = holdId, resourceId, projectId = spec.ProjectId, identity = actor.Name, sessionId = actor.SessionId, itemId = spec.ItemId,
                            purpose = spec.Purpose, priority = JsonName(spec.Priority), state = grant ? WorkHoldStates.Held : WorkHoldStates.Waiting,
                            ttl = spec.TtlSeconds, now, waitExpires = grant ? (DateTime?)null : now.AddSeconds(WorkHoldLimits.WaiterLingerSeconds),
                            grantedAt = grant ? now : (DateTime?)null, expires = grant ? now.AddSeconds(spec.TtlSeconds) : (DateTime?)null, keyHash = spec.KeyHash
                        }, transaction, cancellationToken: ct));
                    }
                    catch (MySqlException exception) when (exception.Number == MySqlDuplicateEntry && exception.Message.Contains("ux_work_holds_key", StringComparison.Ordinal))
                    {
                        // The same key raced in on another resource (on this one the resource row
                        // serialises the calls and the key was found above): same refusal as the
                        // sequential case. The whole call rolls back.
                        throw WorkException.Invalid("idempotency_key was used for another resource. Nothing was changed.");
                    }
                }
            }
            else if (existing.State == WorkHoldStates.Waiting)
            {
                // Polling keeps the place in the queue.
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_holds SET wait_expires_at = @waitExpires WHERE id = @id AND state = 'waiting'",
                    new { id = existing.Id, waitExpires = now.AddSeconds(WorkHoldLimits.WaiterLingerSeconds) }, transaction, cancellationToken: ct));
            }

            var views = await BuildResourceViewsAsync(connection, transaction, [resource], now, ct);
            HoldView? hold = null;
            if (holdId is { } id)
            {
                var row = (await ReadHoldRowAsync(connection, transaction, id, ct))!;
                var position = views[0].Waiters?.FirstOrDefault(waiter => waiter.Id == id)?.QueuePosition;
                hold = ToHoldView(row, now, position);
            }

            await transaction.CommitAsync(ct);
            var outcome = hold?.State == WorkHoldStates.Held ? WorkHoldOutcomes.Granted : WorkHoldOutcomes.Held;
            return new AcquireResult(outcome, views[0], hold, reused ? true : null, created ? true : null);
        }, ct);

    private static void RequireSameDeclaration(ResourceRow resource, AcquireSpec spec)
    {
        if (spec.Capacity is { } capacity && capacity != resource.Capacity)
            throw new WorkException(WorkErrorCodes.Conflict,
                $"Resource {resource.Name} exists with capacity {resource.Capacity}, not {capacity}. Leave capacity out to use the resource as it is. Nothing was changed.");
        if (spec.Labels is { } labels)
        {
            var stored = JsonSerializer.Deserialize<List<string>>(resource.LabelsJson) ?? [];
            if (!stored.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(labels))
                throw new WorkException(WorkErrorCodes.Conflict,
                    $"Resource {resource.Name} exists with labels [{string.Join(", ", stored)}]. Leave labels out to use the resource as it is. Nothing was changed.");
        }
    }

    // ---- renew_hold and release --------------------------------------------------------------------

    public Task<HoldView> RenewHoldAsync(Guid holdId, int? ttlSeconds, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(HoldIsolation, ct);
            var resource = await LockHoldAsync(connection, transaction, holdId, actor, ct);
            await SweepResourceAsync(connection, transaction, resource, now, ct);
            var row = (await ReadHoldRowAsync(connection, transaction, holdId, ct))!;

            // A hold that is no longer held is never granted again by a renewal: the caller learns it
            // lost the slot and acquires again (and queues if someone else has it by now).
            if (row.State != WorkHoldStates.Held)
            {
                var view = ToHoldView(row, now);
                var reason = row.State switch
                {
                    WorkHoldStates.Waiting => $"Hold {holdId} on {resource.Name} is still waiting for a slot; acquire again to keep your place, there is nothing to renew.",
                    WorkHoldStates.Expired => $"Hold {holdId} on {resource.Name} lapsed at {Utc(row.ExpiresAt!.Value):O}. It was not renewed; acquire again.",
                    WorkHoldStates.Released => $"Hold {holdId} on {resource.Name} was released at {Utc(row.EndedAt!.Value):O}; acquire again.",
                    _ => $"Hold {holdId} on {resource.Name} ended at {Utc(row.EndedAt!.Value):O} ({row.State}); acquire again."
                };
                throw new WorkException(WorkErrorCodes.Conflict, reason + " Nothing was changed.", view);
            }

            var ttl = ttlSeconds ?? row.TtlSeconds;
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_holds SET expires_at = @expires, renewed_at = @now, ttl_seconds = @ttl WHERE id = @holdId",
                new { holdId, expires = now.AddSeconds(ttl), now, ttl }, transaction, cancellationToken: ct));
            var renewed = (await ReadHoldRowAsync(connection, transaction, holdId, ct))!;
            await transaction.CommitAsync(ct);
            return ToHoldView(renewed, now);
        }, ct);

    public Task<ReleaseResult> ReleaseHoldAsync(Guid holdId, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(HoldIsolation, ct);
            var resource = await LockHoldAsync(connection, transaction, holdId, actor, ct);
            await SweepResourceAsync(connection, transaction, resource, now, ct);
            var row = (await ReadHoldRowAsync(connection, transaction, holdId, ct))!;

            if (row.State is WorkHoldStates.Held or WorkHoldStates.Waiting)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_holds SET state = @state, ended_at = @now WHERE id = @holdId",
                    new { holdId, now, state = row.State == WorkHoldStates.Held ? WorkHoldStates.Released : WorkHoldStates.Cancelled }, transaction, cancellationToken: ct));
                await PromoteWaitersAsync(connection, transaction, resource, now, ct);
                row = (await ReadHoldRowAsync(connection, transaction, holdId, ct))!;
            }

            var resources = await BuildResourceViewsAsync(connection, transaction, [resource], now, ct);
            await transaction.CommitAsync(ct);
            return new ReleaseResult(ToHoldView(row, now), resources[0]);
        }, ct);

    // The hold's resource locked; only the identity that made the hold may change it.
    private static async Task<ResourceRow> LockHoldAsync(MySqlConnection connection, MySqlTransaction transaction, Guid holdId, WorkActor actor, CancellationToken ct)
    {
        var found = await ReadHoldRowAsync(connection, transaction, holdId, ct)
            ?? throw WorkException.NotFound($"There is no hold {holdId}.");
        if (!string.Equals(found.Identity, actor.Name, StringComparison.Ordinal))
            throw WorkException.NotFound($"There is no hold {holdId} of yours.");
        await LockResourceAsync(connection, transaction, found.ResourceId, ct);
        return (await ReadResourceRowAsync(connection, transaction, found.ResourceId, ct))!;
    }

    // ---- end_session -------------------------------------------------------------------------------

    /// <summary>
    /// Releases every hold of the session and cancels its waiting entries, then lets the next waiters
    /// in. Runs inside end_session's transaction, taking each affected resource's row in id order.
    /// </summary>
    private async Task<IReadOnlyList<HoldView>> ReleaseSessionHoldsAsync(MySqlConnection connection, MySqlTransaction transaction, Guid sessionId, DateTime now, CancellationToken ct)
    {
        var resourceIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT DISTINCT resource_id FROM work_holds WHERE session_id = @sessionId AND state IN ('held', 'waiting') ORDER BY resource_id FOR UPDATE",
            new { sessionId = sessionId.ToString() }, transaction, cancellationToken: ct))).ToList();
        var released = new List<HoldView>();
        foreach (var resourceId in resourceIds)
        {
            await LockResourceAsync(connection, transaction, resourceId, ct);
            var resource = (await ReadResourceRowAsync(connection, transaction, resourceId, ct))!;
            // The session's own entries end first, so a waiter of this session is reported cancelled
            // rather than swept as a dead waiter; a hold that already lapsed is expired, not released.
            // Locking reads: a repeatable-read snapshot could miss a hold or waiter created meanwhile.
            var rows = (await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT id FROM work_holds WHERE resource_id = @resourceId AND session_id = @sessionId AND state IN ('held', 'waiting') ORDER BY seq FOR UPDATE",
                new { resourceId, sessionId = sessionId.ToString() }, transaction, cancellationToken: ct))).ToList();
            await connection.ExecuteAsync(new CommandDefinition(
                // ended_at first: assignments are evaluated in order and the later ones see the new state.
                "UPDATE work_holds SET ended_at = IF(state = 'held' AND expires_at <= @now, expires_at, @now), " +
                "state = IF(state = 'held', IF(expires_at <= @now, 'expired', 'released'), 'cancelled') " +
                "WHERE resource_id = @resourceId AND session_id = @sessionId AND state IN ('held', 'waiting')",
                new { resourceId, sessionId = sessionId.ToString(), now }, transaction, cancellationToken: ct));
            await SweepResourceAsync(connection, transaction, resource, now, ct);
            foreach (var row in rows)
            {
                var view = ToHoldView((await ReadHoldRowAsync(connection, transaction, row, ct))!, now);
                if (view.State is WorkHoldStates.Released or WorkHoldStates.Cancelled)
                    released.Add(view);
            }
        }

        return released;
    }

    // ---- Briefing ----------------------------------------------------------------------------------

    private const int MaxBriefingResources = 20;

    /// <summary>The holds and waits of the session, and the busy resources of its project (holders and waiters, owner unreachable marked).</summary>
    private async Task<(IReadOnlyList<HoldView>? Mine, IReadOnlyList<ResourceView>? Busy)> ReadBriefingHoldsAsync(
        MySqlConnection connection, SessionRow session, DateTime now, CancellationToken ct)
    {
        var parameters = new { projectId = session.ProjectId, sessionId = session.Id, now, staleBefore = StaleBefore(now) };
        var mineRows = (await connection.QueryAsync<HoldRow>(new CommandDefinition(
            HoldSelect + $"WHERE h.project_id = @projectId AND h.session_id = @sessionId AND (({LiveHeld}) OR ({LiveWaiting})) ORDER BY h.seq",
            parameters, cancellationToken: ct))).ToList();

        var busyIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT r.id FROM work_resources r WHERE r.project_id = @projectId AND EXISTS (" +
            "SELECT 1 FROM work_holds h LEFT JOIN work_sessions s ON s.id = h.session_id WHERE h.resource_id = r.id AND ((" + LiveHeld + ") OR (" + LiveWaiting + "))) " +
            $"ORDER BY r.name LIMIT {MaxBriefingResources}", parameters, cancellationToken: ct))).ToList();
        var resources = new List<ResourceRow>();
        foreach (var id in busyIds)
            resources.Add((await ReadResourceRowAsync(connection, null, id, ct))!);
        var busy = await BuildResourceViewsAsync(connection, null, resources, now, ct);

        // Queue positions of my waiting entries come from the resource they wait on.
        var mine = mineRows.Select(row => ToHoldView(row, now,
            busy.FirstOrDefault(resource => resource.Id == row.ResourceId)?.Waiters?.FirstOrDefault(waiter => waiter.Id == row.Id)?.QueuePosition)).ToList();
        return (mine.Count == 0 ? null : mine, busy.Count == 0 ? null : busy);
    }
}
