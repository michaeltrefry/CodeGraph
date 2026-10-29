using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

// GitHub evidence on work items (sc-27870; design §7, decisions D7 and D27).
//
// A webhook event names items by key or alias (or by a PR an item links). Each item is written in
// its own transaction under the item's row, like any other item write: the same version bump and
// the same change-feed entry (attributed to the GitHub login), so the feed shows every move once.
// A delivery is handled once per item: the delivery id is the idempotency key, and evidence rows
// carry a natural key (review id, comment id, PR merge) so a redelivery under another id adds
// nothing either.
//
// Status only ever moves forward: a push to Backlog/Ready makes In Progress, an open PR makes In
// Review, a merge flags merged_awaiting_acceptance and never completes. The one backward move is a
// PR closed without merging, which returns an In Review item to Ready (its attempt ends
// abandoned, carrying the PR description). Evidence tied only to superseded attempts is recorded
// (flagged superseded) and moves nothing.
public sealed partial class MySqlWorkStore
{
    private const int MaxEvidenceTargets = 50;
    private const string GitHubKeyOperation = "github";

    private sealed class AttemptTie
    {
        public Guid Id { get; init; }
        public string Outcome { get; init; } = "";
    }

    private sealed class LinkAttempt
    {
        public Guid? AttemptId { get; init; }
    }

    private sealed class EvidenceRow
    {
        public Guid Id { get; init; }
        public string? Body { get; init; }
        public string? ReviewState { get; init; }
    }

    private sealed class EvidenceViewRow
    {
        public Guid Id { get; init; }
        public string Kind { get; init; } = "";
        public string? Repository { get; init; }
        public int? PrNumber { get; init; }
        public string? Url { get; init; }
        public string AuthorLogin { get; init; } = "";
        public string Source { get; init; } = "";
        public string Priority { get; init; } = "";
        public string? ReviewState { get; init; }
        public string? Location { get; init; }
        public string? Body { get; init; }
        public Guid? AttemptId { get; init; }
        public bool Superseded { get; init; }
        public string? ExternalId { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    public const int EvidenceAttempts = 3;

    public async Task<GitHubEvidenceResult> RecordGitHubEvidenceAsync(GitHubEvidenceSpec spec, CancellationToken ct = default)
    {
        var targets = await RetryTransientAsync(() => FindEvidenceTargetsAsync(spec, ct), EvidenceAttempts, null, ct);
        var results = new List<GitHubEvidenceItemResult>();
        foreach (var itemId in targets)
        {
            var result = await RetryTransientAsync(() => RecordGitHubEvidenceForItemAsync(itemId, spec, ct), EvidenceAttempts, null, ct);
            if (result is not null)
                results.Add(result);
        }

        return new GitHubEvidenceResult(results);
    }

    /// <summary>
    /// Runs <paramref name="body"/> again (up to <paramref name="attempts"/> times, with a short
    /// growing pause) when it failed for a transient reason: the store's busy refusal (deadlocks and
    /// lock waits that outlasted the store's own retries) or a transient MySQL error. GitHub does not
    /// redeliver on its own, so a webhook write gets a few tries before it fails. Every webhook write
    /// is idempotent, so a repeat is safe.
    /// </summary>
    public static async Task<T> RetryTransientAsync<T>(Func<Task<T>> body, int attempts, Func<TimeSpan, Task>? pause, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await body();
            }
            catch (Exception exception) when (attempt < attempts && IsTransient(exception))
            {
                var wait = TimeSpan.FromMilliseconds(200 * attempt);
                await (pause?.Invoke(wait) ?? Task.Delay(wait, ct));
            }
        }
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        WorkException work => work.Code == WorkErrorCodes.Busy,
        MySqlException mysql => mysql.IsTransient || mysql.Number is MySqlDeadlock or MySqlRecordChanged or MySqlLockWaitTimeout,
        _ => false
    };

    /// <summary>
    /// The items an event names: a key or alias in its texts of an item whose project holds the
    /// repository (a repository outside the project never moves its items), plus every item that
    /// already links the event's PR. In id order, so concurrent events take item rows alike.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> FindEvidenceTargetsAsync(GitHubEvidenceSpec spec, CancellationToken ct)
    {
        var references = spec.References.SelectMany(WorkReferences.Find).Distinct().Take(MaxEvidenceTargets).ToList();
        var hasPr = !string.IsNullOrEmpty(spec.PullRequestUrl);
        if (references.Count == 0 && !hasPr)
            return [];

        var parameters = new DynamicParameters();
        var clauses = new List<string>();
        if (references.Count > 0)
        {
            var keys = new List<string>();
            for (var index = 0; index < references.Count; index++)
            {
                keys.Add($"(p.project_key = @k{index} AND i.number = @n{index})");
                parameters.Add($"k{index}", references[index].Prefix.ToUpperInvariant());
                parameters.Add($"n{index}", references[index].Number);
            }

            parameters.Add("aliases", references.Select(reference => $"{reference.Prefix}-{reference.Number}").ToArray());
            parameters.Add("repos", spec.Repositories.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray());
            clauses.Add(
                $"(({string.Join(" OR ", keys)} OR EXISTS (SELECT 1 FROM work_item_aliases a WHERE a.item_id = i.id AND a.alias IN @aliases)) " +
                "AND EXISTS (SELECT 1 FROM work_project_repositories r WHERE r.project_id = i.project_id AND r.repository_name IN @repos))");
        }

        if (hasPr)
        {
            parameters.Add("prHash", HashUrl(spec.PullRequestUrl!));
            // An imported item (sc-27876) carries the PR links of its source tracker. A PR that was
            // already merged or closed there (a historical link) must not move it later; a PR that was
            // in flight at import time links like any other. A historical link stops being one when
            // GitHub evidence or an agent adds it again.
            clauses.Add("EXISTS (SELECT 1 FROM work_item_links l WHERE l.item_id = i.id AND l.kind = 'pr' AND l.url_hash = @prHash AND l.historical = 0)");
        }

        await using var connection = await OpenAsync(ct);
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            $"SELECT i.id FROM work_items i JOIN work_projects p ON p.id = i.project_id WHERE {string.Join(" OR ", clauses)} ORDER BY i.id",
            parameters, cancellationToken: ct))).ToList();
    }

    private Task<GitHubEvidenceItemResult?> RecordGitHubEvidenceForItemAsync(Guid itemId, GitHubEvidenceSpec spec, CancellationToken ct) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET version = version WHERE id = @itemId", new { itemId }, transaction, cancellationToken: ct));
            var beforeRow = await ReadItemRowAsync(connection, transaction, itemId, ct);
            if (beforeRow is null)
            {
                await transaction.CommitAsync(ct);
                return null;
            }

            var before = await ToViewAsync(connection, transaction, beforeRow, ct);
            var keyHash = WorkRules.HashIdempotencyKey(GitHubKeyOperation, $"{spec.DeliveryId}|{spec.Kind}|{itemId}");
            if (await FindKeyAsync(connection, transaction, itemId, GitHubKeyOperation, keyHash, ct) is not null)
            {
                await transaction.CommitAsync(ct);
                return new GitHubEvidenceItemResult(itemId, before.Key, "duplicate", before.Status, before.Status, false);
            }

            await InsertKeyAsync(connection, transaction, itemId, GitHubKeyOperation, keyHash, itemId, null, now, ct);

            var actor = GitHubActors.For(spec.ActorLogin);
            var extra = new JsonArray();

            // The attempts this event belongs to: those that made the PR or branch link, or whose
            // session branch it is. All of them superseded means the event moves nothing.
            var hashes = new List<string>();
            if (!string.IsNullOrEmpty(spec.PullRequestUrl))
                hashes.Add(HashUrl(spec.PullRequestUrl));
            if (!string.IsNullOrEmpty(spec.BranchUrl))
                hashes.Add(HashUrl(spec.BranchUrl));
            var tied = (await connection.QueryAsync<AttemptTie>(new CommandDefinition(
                "SELECT DISTINCT a.id, a.outcome FROM work_attempts a WHERE a.item_id = @itemId AND " +
                "(a.id IN (SELECT l.attempt_id FROM work_item_links l WHERE l.item_id = @itemId AND l.attempt_id IS NOT NULL AND l.url_hash IN @hashes) " +
                "OR (@branch IS NOT NULL AND a.branch = @branch))",
                new { itemId, hashes = hashes.ToArray(), branch = spec.Branch }, transaction, cancellationToken: ct))).ToList();
            var active = await ActiveAttemptAsync(connection, transaction, itemId, ct);
            var superseded = tied.Count > 0 && tied.All(tie => tie.Outcome == "superseded");
            var attemptId = superseded
                ? tied[0].Id
                : tied.FirstOrDefault(tie => tie.Outcome == "active")?.Id ?? tied.FirstOrDefault()?.Id ?? active?.Id;

            // A PR closed unmerged may return the item only when this PR was linked to it already
            // and belongs to its current attempt: a stale PR of an earlier attempt, or a foreign PR
            // that merely mentions the key, is recorded as evidence and moves nothing.
            var linkedPr = string.IsNullOrEmpty(spec.PullRequestUrl) ? null : await connection.QuerySingleOrDefaultAsync<LinkAttempt>(new CommandDefinition(
                "SELECT attempt_id FROM work_item_links WHERE item_id = @itemId AND kind = 'pr' AND url_hash = @hash",
                new { itemId, hash = HashUrl(spec.PullRequestUrl) }, transaction, cancellationToken: ct));
            var ownsCurrentAttempt = linkedPr is not null && (active is null || linkedPr.AttemptId == active.Id);

            // Links: a push links its branch; every PR event links the PR.
            var links = new List<LinkSpec>();
            if (spec.Kind == GitHubEvidenceKind.Push && !string.IsNullOrEmpty(spec.BranchUrl))
                links.Add(new LinkSpec(WorkLinkKind.Branch, spec.BranchUrl));
            if (spec.Kind != GitHubEvidenceKind.Push && !string.IsNullOrEmpty(spec.PullRequestUrl))
                links.Add(new LinkSpec(WorkLinkKind.Pr, spec.PullRequestUrl));
            await WriteLinksAsync(connection, transaction, itemId, links, [], actor, extra, now, ct, attemptId);

            // Forward only; finished and superseded items move nothing. The one way back is a PR
            // closed without merging, from In Review.
            var status = before.Status;
            var merged = beforeRow.MergedAwaitingAcceptance;
            var returned = false;
            if (!superseded && !WorkStatuses.IsFinished(before.Status))
            {
                switch (spec.Kind)
                {
                    case GitHubEvidenceKind.Push when status is WorkStatus.Backlog or WorkStatus.Ready:
                        status = WorkStatus.InProgress;
                        break;
                    case GitHubEvidenceKind.PullRequestOpen when status is WorkStatus.Backlog or WorkStatus.Ready or WorkStatus.InProgress:
                        status = WorkStatus.InReview;
                        break;
                    case GitHubEvidenceKind.PullRequestMerged:
                        if (status is WorkStatus.Backlog or WorkStatus.Ready or WorkStatus.InProgress)
                            status = WorkStatus.InReview;
                        merged = true;
                        break;
                    case GitHubEvidenceKind.PullRequestClosed when status == WorkStatus.InReview && ownsCurrentAttempt:
                        status = WorkStatus.Ready;
                        returned = true;
                        break;
                }
            }

            // Evidence rows: pushes (head SHA and commits), merge, close, reviews and comments.
            var evidenceKind = spec.Kind switch
            {
                GitHubEvidenceKind.Push => "push",
                GitHubEvidenceKind.PullRequestMerged => "merge",
                GitHubEvidenceKind.PullRequestClosed => "pr_closed",
                GitHubEvidenceKind.Review => "review",
                GitHubEvidenceKind.ReviewComment => "review_comment",
                GitHubEvidenceKind.IssueComment => "issue_comment",
                _ => null
            };

            if (returned)
            {
                var reason = ClosedUnmergedReason(spec);
                if (active is not null)
                {
                    await EndAttemptAsync(connection, transaction, active, AttemptOutcome.Abandoned, reason, now, extra, ct);
                    attemptId = active.Id;
                }
                else
                {
                    attemptId = await DraftAbandonedAttemptAsync(connection, transaction, itemId, actor, spec, reason, now, extra, ct);
                }

                // The item goes back to the queue: its claim is cleared below, as a lapse does. The
                // session's own claim rows stay open, so its next write is refused claim_lost.
            }

            if (evidenceKind is not null)
                await UpsertEvidenceAsync(connection, transaction, itemId, evidenceKind, spec, attemptId, superseded, now, extra, ct);

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET status = @status, merged_awaiting_acceptance = @merged, " +
                "claim_holder = IF(@returned, NULL, claim_holder), claim_marker = IF(@returned, NULL, claim_marker), claim_session_id = IF(@returned, NULL, claim_session_id), " +
                "claimed_at = IF(@returned, NULL, claimed_at), claim_expires_at = IF(@returned, NULL, claim_expires_at) WHERE id = @itemId",
                new { status = WorkStatuses.Name(status), merged, returned, itemId }, transaction, cancellationToken: ct));

            var movedRow = (await ReadItemRowAsync(connection, transaction, itemId, ct))!;
            var moved = await ToViewAsync(connection, transaction, movedRow, ct);
            var changes = Diff(Snapshot(before), Snapshot(moved));
            foreach (var change in Diff(ClaimSnapshot(beforeRow), ClaimSnapshot(movedRow)))
                changes.Add(change!.DeepClone());
            foreach (var change in extra)
                changes.Add(change!.DeepClone());
            if (changes.Count == 0)
            {
                await transaction.CommitAsync(ct);
                return new GitHubEvidenceItemResult(itemId, before.Key, "unchanged", before.Status, before.Status, superseded);
            }

            // Evidence that changes no link, status, merge flag, claim or attempt (a review, a comment,
            // a merge record on an item that has it all already) is still fed, attributed and
            // exactly once, but does not bump the item's version: a bot's comment must not make the
            // agent working on the item fail its next expected_version. Such an entry carries no
            // version change; the version it describes is the item's current, unbumped one.
            var bumps = changes.Any(change => change!["field"]!.GetValue<string>() is not "evidence");
            if (bumps)
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_items SET version = version + 1, updated_at = @now WHERE id = @itemId", new { itemId, now }, transaction, cancellationToken: ct));
            var after = (await LoadItemViewAsync(connection, transaction, itemId, ct))!;
            if (bumps)
                changes.Add(Change("version", before.Version, after.Version));
            await CommitAsync(connection, transaction,
                [new PendingChange(after.ProjectId, after.ProjectKey, WorkEntityKind.Item, after.Id, after.Key, WorkChangeKinds.ItemUpdated, changes)],
                actor, now, ct);
            return new GitHubEvidenceItemResult(itemId, after.Key, "applied", before.Status, after.Status, superseded,
                FlaggedMerged: merged && !beforeRow.MergedAwaitingAcceptance);
        }, ct);

    private static string ClosedUnmergedReason(GitHubEvidenceSpec spec)
    {
        var head = $"Pull request #{spec.PullRequestNumber} was closed without merging" +
                   (string.IsNullOrWhiteSpace(spec.Title) ? "." : $": {spec.Title.Trim()}");
        if (string.IsNullOrWhiteSpace(spec.Description))
            return head;

        var description = Regex.Replace(spec.Description.Trim(), @"\s+", " ");
        return $"{head} Description: {description}";
    }

    private static async Task<Guid> DraftAbandonedAttemptAsync(MySqlConnection connection, MySqlTransaction transaction, Guid itemId, WorkActor actor,
        GitHubEvidenceSpec spec, string reason, DateTime now, JsonArray extra, CancellationToken ct)
    {
        reason = reason.Length <= 1000 ? reason : reason[..999] + "…";
        var number = (await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(number) FROM work_attempts WHERE item_id = @itemId", new { itemId }, transaction, cancellationToken: ct)) ?? 0) + 1;
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO work_attempts (id, item_id, number, holder, marker, session_id, branch, outcome, reason, started_at, ended_at) " +
            "VALUES (@id, @itemId, @number, @holder, @marker, NULL, @branch, 'abandoned', @reason, @now, @now)",
            new { id, itemId, number, holder = actor.Name, marker = $"github:pr:{spec.PullRequestNumber}", branch = spec.Branch, reason, now },
            transaction, cancellationToken: ct));
        extra.Add(Change("attempt", null, new JsonObject
        {
            ["id"] = id.ToString(), ["number"] = number, ["outcome"] = WorkJson.Name(AttemptOutcome.Abandoned), ["reason"] = reason
        }));
        return id;
    }

    private async Task UpsertEvidenceAsync(MySqlConnection connection, MySqlTransaction transaction, Guid itemId, string kind, GitHubEvidenceSpec spec,
        Guid? attemptId, bool superseded, DateTime now, JsonArray extra, CancellationToken ct)
    {
        var evidenceKey = spec.Kind switch
        {
            GitHubEvidenceKind.PullRequestMerged => $"pr:{spec.PullRequestNumber}:merged",
            GitHubEvidenceKind.PullRequestClosed => $"pr:{spec.PullRequestNumber}:closed",
            _ => $"{kind}:{spec.ExternalId}"
        };
        var body = spec.Kind is GitHubEvidenceKind.PullRequestMerged or GitHubEvidenceKind.PullRequestClosed
            ? spec.Description
            : spec.Body;
        var existing = await connection.QuerySingleOrDefaultAsync<EvidenceRow>(new CommandDefinition(
            "SELECT id, body, review_state FROM work_github_evidence WHERE item_id = @itemId AND evidence_key = @evidenceKey",
            new { itemId, evidenceKey }, transaction, cancellationToken: ct));
        if (existing is not null)
        {
            if (existing.Body == body && existing.ReviewState == spec.ReviewState)
                return;

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_github_evidence SET body = @body, review_state = @reviewState, updated_at = @now WHERE id = @id",
                new { body, reviewState = spec.ReviewState, now, id = existing.Id }, transaction, cancellationToken: ct));
            extra.Add(Change("evidence", new { id = existing.Id, kind, state = existing.ReviewState }, new { id = existing.Id, kind, state = spec.ReviewState, edited = true }));
            return;
        }

        var id = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO work_github_evidence (id, item_id, kind, evidence_key, repository, pr_number, url, external_id, author_login, source, priority, " +
            "review_state, location, body, attempt_id, superseded, delivery_id, created_at, updated_at) " +
            "VALUES (@id, @itemId, @kind, @evidenceKey, @repository, @pr, @url, @externalId, @author, @source, @priority, " +
            "@reviewState, @location, @body, @attemptId, @superseded, @delivery, @now, @now)",
            new
            {
                id, itemId, kind, evidenceKey,
                repository = spec.Repositories.FirstOrDefault(),
                pr = spec.PullRequestNumber, url = spec.Url ?? spec.PullRequestUrl, externalId = spec.ExternalId,
                author = spec.ActorLogin, source = spec.Source, priority = spec.Priority,
                reviewState = spec.ReviewState, location = spec.Location, body, attemptId, superseded,
                delivery = spec.DeliveryId, now
            },
            transaction, cancellationToken: ct));
        extra.Add(Change("evidence", null, new
        {
            id, kind, pr = spec.PullRequestNumber, url = spec.Url ?? spec.PullRequestUrl, author = spec.ActorLogin,
            source = spec.Source, priority = spec.Priority, state = spec.ReviewState, superseded = superseded ? true : (bool?)null
        }));
    }

    public async Task<IReadOnlyList<GitHubEvidenceView>> ListGitHubEvidenceAsync(Guid itemId, int limit, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<EvidenceViewRow>(new CommandDefinition(
            "SELECT id, kind, repository, pr_number, url, author_login, source, priority, review_state, location, body, attempt_id, superseded, external_id, created_at " +
            "FROM work_github_evidence WHERE item_id = @itemId ORDER BY created_at DESC, id LIMIT @limit",
            new { itemId, limit = Math.Clamp(limit, 1, 500) }, cancellationToken: ct));
        return rows.Select(row => new GitHubEvidenceView(
            row.Id, row.Kind, row.Repository, row.PrNumber, row.Url, row.AuthorLogin, row.Source, row.Priority, row.ReviewState,
            row.Location, row.Body, row.AttemptId, row.Superseded, Utc(row.CreatedAt), row.ExternalId)).ToList();
    }


    public async Task<ItemView> AddItemAliasesAsync(Guid itemId, IReadOnlyList<string> aliases, WorkActor actor, CancellationToken ct = default)
    {
        // Leading zeros in the number do not make another alias: sc-0012 is sc-12.
        var normalized = aliases.Select(alias => WorkReferences.NormalizeAlias(alias) ?? alias.Trim()).Distinct().ToList();
        var invalid = normalized.Where(alias => WorkReferences.NormalizeAlias(alias) != alias).ToList();
        if (invalid.Count > 0)
            throw WorkException.Invalid($"Aliases look like 'sc-27866' (letters and digits, a hyphen, a number above zero); not: {string.Join(", ", invalid)}. Nothing was changed.");

        return await RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET version = version WHERE id = @itemId", new { itemId }, transaction, cancellationToken: ct));
            var beforeRow = await ReadItemRowAsync(connection, transaction, itemId, ct)
                ?? throw WorkException.NotFound($"There is no item {itemId}.");
            var extra = new JsonArray();
            foreach (var alias in normalized)
            {
                // An alias that reads as the key of an item would be matched as that item.
                var separator = alias.IndexOf('-');
                var keyed = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT COUNT(*) FROM work_items i JOIN work_projects p ON p.id = i.project_id WHERE p.project_key = @prefix AND i.number = @number",
                    new { prefix = alias[..separator].ToUpperInvariant(), number = int.Parse(alias[(separator + 1)..]) }, transaction, cancellationToken: ct));
                if (keyed > 0)
                    throw WorkException.Invalid($"'{alias}' is the key of an item: an alias cannot be one. Nothing was changed.");

                var owner = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                    "SELECT item_id FROM work_item_aliases WHERE alias = @alias", new { alias }, transaction, cancellationToken: ct));
                if (owner == itemId)
                    continue;
                if (owner is not null)
                    throw new WorkException(WorkErrorCodes.Conflict, $"The alias '{alias}' names another item already. Nothing was changed.");

                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO work_item_aliases (item_id, alias, created_by, created_at) VALUES (@itemId, @alias, @by, @now)",
                    new { itemId, alias, by = actor.Name, now }, transaction, cancellationToken: ct));
                extra.Add(Change("alias", null, alias));
            }

            if (extra.Count == 0)
            {
                var unchanged = await ToViewAsync(connection, transaction, beforeRow, ct);
                await transaction.CommitAsync(ct);
                return unchanged;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET version = version + 1, updated_at = @now WHERE id = @itemId", new { itemId, now }, transaction, cancellationToken: ct));
            var after = (await LoadItemViewAsync(connection, transaction, itemId, ct))!;
            extra.Add(Change("version", beforeRow.Version, after.Version));
            await CommitAsync(connection, transaction,
                [new PendingChange(after.ProjectId, after.ProjectKey, WorkEntityKind.Item, after.Id, after.Key, WorkChangeKinds.ItemUpdated, extra)],
                actor, now, ct);
            return after;
        }, ct);
    }
}
