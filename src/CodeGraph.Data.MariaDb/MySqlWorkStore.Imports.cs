using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

// Imports (sc-27876, design §10): upserts keyed by the source object, in the store's lock order
// (dependency lock, project row, feature row, item rows in id order, change-feed lock), each with its
// change-feed entry attributed to the importing actor. The source's own created/updated times and
// comment authors and times are kept; the feed entries carry the time of the import.
//
// Until cutover a re-run is a three-way merge: the record keeps the mapped fields as the last run
// wrote them (snapshot_json); a field is written only when the source changed it since, so what
// CodeGraph changed in the other fields (a forward status, agent AC and evidence, removed edges)
// survives. When both changed a field, the source wins: it is the source of truth until cutover.
public sealed partial class MySqlWorkStore : IWorkImportStore
{
    private sealed class ImportRecordRow
    {
        public string ExternalId { get; init; } = "";
        public string EntityKind { get; init; } = "";
        public Guid EntityId { get; init; }
        public Guid ProjectId { get; init; }
        public string ContentHash { get; init; } = "";
        public string? EdgesJson { get; init; }
        public string? SnapshotJson { get; init; }
        public string? RelationsJson { get; init; }
        public DateTime? SourceUpdatedAt { get; init; }

        public ImportRecord ToRecord() =>
            new(ExternalId, EntityKind, EntityId, ProjectId, ContentHash, ReadList<Guid>(EdgesJson), SourceUpdatedAt is { } at ? Utc(at) : null,
                RelationsJson is { Length: > 2 });
    }

    /// <summary>The mapped fields of an item as an import last wrote them.</summary>
    private sealed record ItemSnapshot(
        string Title, string Type, string Status, string Priority, string? Description, string? Labels, string? Repository, Guid? FeatureId,
        IReadOnlyList<CriterionSpec> AcceptanceCriteria);

    /// <summary>The mapped fields of a feature as an import last wrote them; documents by title.</summary>
    private sealed record FeatureSnapshot(string Title, string? Description, bool RequirementsUnreconciled, IReadOnlyDictionary<string, string> Documents);

    private const string RecordColumns = "external_id, entity_kind, entity_id, project_id, content_hash, edges_json, snapshot_json, relations_json, source_updated_at";

    public async Task<IReadOnlyDictionary<string, ImportRecord>> ListImportRecordsAsync(string source, string externalKind, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<ImportRecordRow>(new CommandDefinition(
            $"SELECT {RecordColumns} FROM work_import_records WHERE source = @source AND external_kind = @externalKind",
            new { source, externalKind }, cancellationToken: ct));
        return rows.ToDictionary(row => row.ExternalId, row => row.ToRecord(), StringComparer.Ordinal);
    }

    private static Task<ImportRecordRow?> ReadRecordAsync(MySqlConnection connection, MySqlTransaction transaction, string source, string kind, string externalId, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<ImportRecordRow>(new CommandDefinition(
            $"SELECT {RecordColumns} FROM work_import_records WHERE source = @source AND external_kind = @kind AND external_id = @externalId",
            new { source, kind, externalId }, transaction, cancellationToken: ct));

    private static Task WriteRecordAsync(MySqlConnection connection, MySqlTransaction transaction, string source, string kind, string externalId, string entityKind,
        Guid entityId, Guid projectId, string hash, string snapshot, DateTime? sourceUpdatedAt, DateTime now, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO work_import_records (source, external_kind, external_id, entity_kind, entity_id, project_id, content_hash, snapshot_json, source_updated_at, imported_at) " +
            "VALUES (@source, @kind, @externalId, @entityKind, @entityId, @projectId, @hash, @snapshot, @sourceUpdatedAt, @now) " +
            "ON DUPLICATE KEY UPDATE entity_kind = VALUES(entity_kind), entity_id = VALUES(entity_id), project_id = VALUES(project_id), " +
            "content_hash = VALUES(content_hash), snapshot_json = VALUES(snapshot_json), source_updated_at = VALUES(source_updated_at), imported_at = VALUES(imported_at)",
            new { source, kind, externalId, entityKind, entityId, projectId, hash, snapshot, sourceUpdatedAt, now }, transaction, cancellationToken: ct));

    // Snapshots are the store's own: plain System.Text.Json, every field written (also null).
    private static readonly JsonSerializerOptions SnapshotOptions = new(JsonSerializerDefaults.General);

    private static T? ReadSnapshot<T>(string? json) where T : class =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, SnapshotOptions);

    private static string SnapshotJson(object? value) => JsonSerializer.Serialize(value, SnapshotOptions);

    private static string Json(object? value) => JsonSerializer.Serialize(value, WorkJson.Options);

    private static DateTime Stored(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }

    private static string Cap(string value, int length) => value.Length <= length ? value : value[..(char.IsHighSurrogate(value[length - 1]) ? length - 1 : length)];

    // ---- Features ----------------------------------------------------------------------------

    public Task<ImportWrite> UpsertImportedFeatureAsync(ImportedFeatureSpec spec, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var project = await HoldProjectAsync(connection, transaction, spec.ProjectId, ct);
            var record = await ReadRecordAsync(connection, transaction, spec.Source, "epic", spec.ExternalId, ct);
            FeatureRow? existing = null;
            if (record is not null)
            {
                // The feature's row is held before anything of it is read.
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_features SET version = version WHERE id = @id", new { id = record.EntityId }, transaction, cancellationToken: ct));
                existing = await ReadFeatureRowAsync(connection, transaction, record.EntityId, ct);
            }

            if (existing is not null && existing.ProjectId != spec.ProjectId)
                throw WorkException.Invalid($"Imported feature {existing.Key} is in another project than the one the import maps it to now. Nothing was changed.");

            var title = Cap(spec.Title, 240);
            var imported = Json(spec.Imported);
            var documents = spec.Documents.Select(document => new DocumentSpec(Cap(document.Title, 200), document.Body)).DistinctBy(document => document.Title).ToList();
            var snapshot = new FeatureSnapshot(title, spec.Description, spec.RequirementsUnreconciled, documents.ToDictionary(document => document.Title, document => document.Body));
            if (existing is not null && record!.ContentHash == spec.ContentHash)
            {
                await transaction.CommitAsync(ct);
                return new ImportWrite(existing.Id, existing.Key, existing.ProjectId, ImportOutcome.Unchanged);
            }

            if (existing is null)
            {
                var id = Guid.NewGuid();
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO work_features (id, project_id, number, title, description, version, created_by, created_by_kind, created_session_id, created_at, updated_at, imported_json, requirements_unreconciled) " +
                    "VALUES (@id, @projectId, @number, @title, @Description, 1, @actorName, @actorKind, NULL, @createdAt, @updatedAt, @imported, @unreconciled)",
                    new
                    {
                        id, projectId = project.Id, number = project.NextFeatureNumber, title, spec.Description, actorName = actor.Name, actorKind = actor.Kind,
                        createdAt = Stored(spec.CreatedAt), updatedAt = Stored(spec.UpdatedAt), imported, unreconciled = spec.RequirementsUnreconciled
                    }, transaction, cancellationToken: ct));
                await InsertDocumentsAsync(connection, transaction, id, 0, documents, actor, now, ct);
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_projects SET next_feature_number = next_feature_number + 1 WHERE id = @projectId", new { projectId = project.Id }, transaction, cancellationToken: ct));
                await WriteRecordAsync(connection, transaction, spec.Source, "epic", spec.ExternalId, "feature", id, project.Id, spec.ContentHash, SnapshotJson(snapshot), Stored(spec.UpdatedAt), now, ct);
                var view = (await LoadFeatureViewAsync(connection, transaction, id, withCounts: false, ct))!;
                var entry = FeatureCreatedEntry(view);
                entry.Changes.Add(Change("imported", null, spec.Imported));
                await CommitAsync(connection, transaction, [entry], actor, now, ct);
                return new ImportWrite(id, view.Key, project.Id, ImportOutcome.Created);
            }

            var base_ = ReadSnapshot<FeatureSnapshot>(record!.SnapshotJson);
            var before = (await LoadFeatureViewAsync(connection, transaction, existing.Id, withCounts: false, ct))!;
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_features SET title = @title, description = @description, imported_json = @imported, requirements_unreconciled = @unreconciled, " +
                "version = version + 1, updated_at = @updatedAt WHERE id = @id",
                new
                {
                    title = base_ is null || base_.Title != title ? title : existing.Title,
                    description = base_ is null || base_.Description != spec.Description ? spec.Description : existing.Description,
                    imported,
                    unreconciled = base_ is null || base_.RequirementsUnreconciled != spec.RequirementsUnreconciled ? spec.RequirementsUnreconciled : existing.RequirementsUnreconciled,
                    updatedAt = Stored(spec.UpdatedAt), id = existing.Id
                },
                transaction, cancellationToken: ct));

            // Documents match by title. A document whose source body changed since the last run is
            // written; one the source left alone keeps what CodeGraph made of it; none is removed.
            var added = new List<DocumentSpec>();
            var changed = new List<(DocumentView Before, DocumentView After)>();
            foreach (var document in documents)
            {
                if (base_ is not null && base_.Documents.TryGetValue(document.Title, out var previous) && previous == document.Body)
                    continue;
                var current = before.Documents.FirstOrDefault(candidate => candidate.Title == document.Title);
                if (current is null)
                {
                    added.Add(document);
                    continue;
                }

                if (current.Body == document.Body)
                    continue;
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_feature_documents SET body = @Body, updated_at = @now WHERE id = @Id", new { document.Body, now, current.Id }, transaction, cancellationToken: ct));
                changed.Add((current, current with { Body = document.Body, UpdatedAt = now }));
            }

            var firstPosition = before.Documents.Count == 0 ? 0 : before.Documents.Max(document => document.Position) + 1;
            await InsertDocumentsAsync(connection, transaction, existing.Id, firstPosition, added, actor, now, ct);
            await WriteRecordAsync(connection, transaction, spec.Source, "epic", spec.ExternalId, "feature", existing.Id, project.Id, spec.ContentHash, SnapshotJson(snapshot), Stored(spec.UpdatedAt), now, ct);
            var after = (await LoadFeatureViewAsync(connection, transaction, existing.Id, withCounts: false, ct))!;
            var addedViews = after.Documents.Where(document => before.Documents.All(prior => prior.Id != document.Id)).ToList();
            var changes = WithDocuments(Diff(Snapshot(before), Snapshot(after)), addedViews, changed);
            // What the feature came from changed (the source archived it, sc-27929): in the feed like any field.
            if (existing.ImportedJson != imported)
                changes.Add(Change("imported", existing.ImportedJson is null ? null : JsonNode.Parse(existing.ImportedJson), spec.Imported));
            await CommitAsync(connection, transaction,
                [new PendingChange(after.ProjectId, after.ProjectKey, WorkEntityKind.Feature, after.Id, after.Key, WorkChangeKinds.FeatureUpdated, changes)],
                actor, now, ct);
            return new ImportWrite(after.Id, after.Key, after.ProjectId, ImportOutcome.Updated);
        }, ct);

    // ---- Items -------------------------------------------------------------------------------

    public Task<ImportWrite> UpsertImportedItemAsync(ImportedItemSpec spec, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            var warnings = new List<string>();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var project = await HoldProjectAsync(connection, transaction, spec.ProjectId, ct);
            var record = await ReadRecordAsync(connection, transaction, spec.Source, spec.ExternalKind, spec.ExternalId, ct);
            ItemRow? existing = null;
            if (record is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_items SET version = version WHERE id = @id", new { id = record.EntityId }, transaction, cancellationToken: ct));
                existing = await ReadItemRowAsync(connection, transaction, record.EntityId, ct);
            }

            if (existing is not null && existing.ProjectId != spec.ProjectId)
                throw WorkException.Invalid($"Imported item {existing.Key} is in another project than the one the import maps it to now. Nothing was changed.");
            if (existing is not null && record!.ContentHash == spec.ContentHash)
            {
                await transaction.CommitAsync(ct);
                return new ImportWrite(existing.Id, existing.Key, existing.ProjectId, ImportOutcome.Unchanged);
            }

            string? repository = null;
            if (spec.Repository is { } wanted)
            {
                repository = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT repository_name FROM work_project_repositories WHERE project_id = @projectId AND repository_name = @wanted AND role = 'owned' LIMIT 1",
                    new { projectId = project.Id, wanted }, transaction, cancellationToken: ct));
                if (repository is null)
                    warnings.Add($"'{wanted}' is not an owned repository of {project.ProjectKey}; the repository was left empty.");
            }

            Guid? featureId = spec.FeatureId;
            if (featureId is { } feature && await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT COUNT(*) FROM work_features WHERE id = @feature AND project_id = @projectId", new { feature, projectId = project.Id }, transaction, cancellationToken: ct)) == 0)
            {
                warnings.Add($"Feature {feature} is not in {project.ProjectKey}; the item was imported without its feature.");
                featureId = null;
            }

            var snapshot = new ItemSnapshot(
                Cap(spec.Title, 240), JsonName(spec.Type), WorkStatuses.Name(spec.Status), JsonName(spec.Priority), spec.Description,
                JoinLabels(spec.Labels.Select(label => label.Replace(',', ' ').Trim()).Where(label => label.Length > 0).Distinct().ToList()),
                repository, featureId, spec.AcceptanceCriteria);
            var imported = Json(spec.Imported);
            var historical = (spec.HistoricalLinks ?? []).ToHashSet(StringComparer.Ordinal);
            var extra = new JsonArray();
            ItemView? before = null;
            Guid id;
            if (existing is null)
            {
                id = Guid.NewGuid();
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO work_items (id, project_id, number, feature_id, item_type, title, description, status, priority, sort_order, labels, repository_name, " +
                    "acceptance_json, imported_json, version, created_by, created_by_kind, created_session_id, created_at, updated_at) " +
                    "VALUES (@id, @projectId, @number, @FeatureId, @Type, @Title, @Description, @Status, @Priority, @order, @Labels, @Repository, " +
                    "@acceptance, @imported, 1, @actorName, @actorKind, NULL, @createdAt, @updatedAt)",
                    new
                    {
                        id, projectId = project.Id, number = project.NextItemNumber, snapshot.FeatureId, snapshot.Type, snapshot.Title, snapshot.Description,
                        snapshot.Status, snapshot.Priority, order = spec.Order, snapshot.Labels, snapshot.Repository,
                        acceptance = WriteList(NormalizeCriteria([], spec.AcceptanceCriteria)), imported, actorName = actor.Name, actorKind = actor.Kind,
                        createdAt = Stored(spec.CreatedAt), updatedAt = Stored(spec.UpdatedAt)
                    }, transaction, cancellationToken: ct));
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_projects SET next_item_number = next_item_number + 1 WHERE id = @projectId", new { projectId = project.Id }, transaction, cancellationToken: ct));
            }
            else
            {
                id = existing.Id;
                before = await ToViewAsync(connection, transaction, existing, ct);
                var base_ = ReadSnapshot<ItemSnapshot>(record!.SnapshotJson);
                bool Changed(Func<ItemSnapshot, object?> field) => base_ is null || SnapshotJson(field(base_)) != SnapshotJson(field(snapshot));
                var status = Changed(s => s.Status) ? snapshot.Status : existing.Status;
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_items SET feature_id = @featureId, item_type = @type, title = @title, description = @description, status = @status, priority = @priority, " +
                    "labels = @labels, repository_name = @repository, acceptance_json = @acceptance, imported_json = @imported, " +
                    "version = version + 1, updated_at = @updatedAt WHERE id = @id",
                    new
                    {
                        featureId = Changed(s => s.FeatureId) ? snapshot.FeatureId : existing.FeatureId,
                        type = Changed(s => s.Type) ? snapshot.Type : existing.ItemType,
                        title = Changed(s => s.Title) ? snapshot.Title : existing.Title,
                        description = Changed(s => s.Description) ? snapshot.Description : existing.Description,
                        status,
                        priority = Changed(s => s.Priority) ? snapshot.Priority : existing.Priority,
                        labels = Changed(s => s.Labels) ? snapshot.Labels : existing.Labels,
                        // A repository the source no longer names is not cleared.
                        repository = Changed(s => s.Repository) && snapshot.Repository is not null ? snapshot.Repository : existing.RepositoryName,
                        acceptance = Changed(s => s.AcceptanceCriteria)
                            ? WriteList(MergeCriteria(before.AcceptanceCriteria ?? [], base_?.AcceptanceCriteria, spec.AcceptanceCriteria))
                            : existing.AcceptanceJson,
                        imported, updatedAt = Stored(spec.UpdatedAt), id
                    }, transaction, cancellationToken: ct));

                // A finished item (the source finished or archived it) is claimed by no one, like any
                // finished item (UpdateItemAsync's rule, E1): its claim is cleared, every session's
                // claim on it released and its active attempt ended, all in this write's feed entry.
                var finished = WorkStatuses.Parse(status);
                if (WorkStatuses.IsFinished(finished))
                {
                    if (existing.ClaimHolder is not null)
                        await connection.ExecuteAsync(new CommandDefinition(
                            "UPDATE work_items SET claim_holder = NULL, claim_marker = NULL, claim_session_id = NULL, claimed_at = NULL, claim_expires_at = NULL WHERE id = @id",
                            new { id }, transaction, cancellationToken: ct));
                    await ReleaseSessionClaimsAsync(connection, transaction, id, now, ct);
                    if (await ActiveAttemptAsync(connection, transaction, id, ct) is { } attempt)
                        await FinishAttemptAsync(connection, transaction, attempt, existing.Key, finished, actor, now, extra, ct);
                }

                // What the item came from changed (the source archived it, sc-27929): in the feed like any field (E7).
                if (existing.ImportedJson != imported)
                    extra.Add(Change("imported", existing.ImportedJson is null ? null : JsonNode.Parse(existing.ImportedJson), spec.Imported));
            }

            await WriteImportedCommentsAsync(connection, transaction, id, spec.Comments, extra, now, ct);
            await WriteLinksAsync(connection, transaction, id, spec.Links, [], actor, extra, now, ct, historicalUrls: historical);
            await WriteImportedAliasesAsync(connection, transaction, id, spec.Aliases, actor, extra, warnings, now, ct);
            await WriteRecordAsync(connection, transaction, spec.Source, spec.ExternalKind, spec.ExternalId, "item", id, project.Id, spec.ContentHash, SnapshotJson(snapshot), Stored(spec.UpdatedAt), now, ct);

            var after = (await LoadItemViewAsync(connection, transaction, id, ct))!;
            PendingChange change;
            if (before is null)
            {
                change = ItemCreatedEntry(after);
                change.Changes.Add(Change("imported", null, spec.Imported));
            }
            else
            {
                change = new PendingChange(after.ProjectId, after.ProjectKey, WorkEntityKind.Item, after.Id, after.Key, WorkChangeKinds.ItemUpdated, Diff(Snapshot(before), Snapshot(after)));
                foreach (var claim in Diff(ClaimSnapshot(existing!), ClaimSnapshot((await ReadItemRowAsync(connection, transaction, id, ct))!)))
                    change.Changes.Add(claim!.DeepClone());
            }

            foreach (var node in extra)
                change.Changes.Add(node!.DeepClone());
            await CommitAsync(connection, transaction, [change], actor, now, ct);
            return new ImportWrite(id, after.Key, after.ProjectId, before is null ? ImportOutcome.Created : ImportOutcome.Updated, warnings.Count == 0 ? null : warnings);
        }, ct);

    /// <summary>
    /// The acceptance criteria after a delta, criterion by criterion (by id) against the last imported
    /// list: a criterion the source added or changed takes the source's text and state (keeping its
    /// evidence while the text is the same); one the source dropped goes when CodeGraph left it as
    /// imported; criteria only CodeGraph has, and the evidence of criteria the source did not change,
    /// stay. Without a last list (an older record) the source's list is taken as a whole.
    /// </summary>
    private static List<AcceptanceCriterionView> MergeCriteria(IReadOnlyList<AcceptanceCriterionView> current, IReadOnlyList<CriterionSpec>? baseline, IReadOnlyList<CriterionSpec> source)
    {
        if (baseline is null || source.Any(criterion => criterion.Id is null))
            return NormalizeCriteria(current, source);

        var result = current.ToList();
        var last = baseline.Where(criterion => criterion.Id is not null).ToDictionary(criterion => criterion.Id!, StringComparer.Ordinal);
        foreach (var given in source)
        {
            var state = given.State ?? AcceptanceState.Unmet;
            last.TryGetValue(given.Id!, out var was);
            var changed = was is null || was.Text != given.Text || (was.State ?? AcceptanceState.Unmet) != state;
            if (!changed)
                continue;
            var index = result.FindIndex(criterion => criterion.Id == given.Id);
            if (index < 0)
                result.Add(new AcceptanceCriterionView(given.Id!, given.Text, state));
            else
                result[index] = result[index] with { Text = given.Text, State = state, Evidence = result[index].Text == given.Text ? result[index].Evidence : null };
        }

        foreach (var dropped in baseline.Where(was => was.Id is not null && source.All(given => given.Id != was.Id)))
        {
            var index = result.FindIndex(criterion => criterion.Id == dropped.Id);
            if (index >= 0 && result[index].Text == dropped.Text && result[index].State == (dropped.State ?? AcceptanceState.Unmet) && result[index].Evidence is null)
                result.RemoveAt(index);
        }

        return result;
    }

    private sealed class ExternalCommentRow
    {
        public Guid Id { get; init; }
        public string ExternalId { get; init; } = "";
        public string Body { get; init; } = "";
    }

    /// <summary>
    /// Adds the comments that are new (by external id) and updates the text of edited ones. Like every
    /// comment write, a new comment is later than the ones committed before it (AddCommentAsync's rule,
    /// so a reader paging by time never has one land behind it): created_at is the source time when
    /// that is later, else just after the latest; the source time is kept in source_created_at.
    /// </summary>
    private static async Task<int> WriteImportedCommentsAsync(MySqlConnection connection, MySqlTransaction transaction, Guid itemId, IReadOnlyList<ImportedComment> comments, JsonArray extra,
        DateTime now, CancellationToken ct)
    {
        if (comments.Count == 0)
            return 0;

        var existing = (await connection.QueryAsync<ExternalCommentRow>(new CommandDefinition(
            "SELECT id, external_id, body FROM work_item_comments WHERE item_id = @itemId AND external_id IS NOT NULL",
            new { itemId }, transaction, cancellationToken: ct))).ToDictionary(row => row.ExternalId, StringComparer.Ordinal);
        var latest = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(created_at) FROM work_item_comments WHERE item_id = @itemId", new { itemId }, transaction, cancellationToken: ct));
        var written = 0;
        foreach (var comment in comments.DistinctBy(comment => comment.ExternalId).OrderBy(comment => comment.CreatedAt))
        {
            if (existing.TryGetValue(comment.ExternalId, out var stored))
            {
                if (stored.Body == comment.Body)
                    continue;
                // An edited source comment: the text follows the source, the author and time stay.
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_item_comments SET body = @Body WHERE id = @Id", new { comment.Body, stored.Id }, transaction, cancellationToken: ct));
                extra.Add(Change("comment", new { id = stored.Id, body = stored.Body }, new { id = stored.Id, author = comment.Author, body = comment.Body }));
                written++;
                continue;
            }

            var source = Stored(comment.CreatedAt);
            var createdAt = latest is { } last && last >= source ? last.AddTicks(10) : source;
            latest = createdAt;
            var id = Guid.NewGuid();
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO work_item_comments (id, item_id, author, author_kind, author_session_id, body, created_at, source_created_at, external_id) " +
                "VALUES (@id, @itemId, @Author, @AuthorKind, NULL, @Body, @createdAt, @source, @ExternalId)",
                new { id, itemId, comment.Author, comment.AuthorKind, comment.Body, createdAt, source, comment.ExternalId },
                transaction, cancellationToken: ct));
            extra.Add(Change("comment", null, new { id, author = comment.Author, body = comment.Body }));
            written++;
        }

        return written;
    }

    private static async Task WriteImportedAliasesAsync(MySqlConnection connection, MySqlTransaction transaction, Guid itemId, IReadOnlyList<string> aliases, WorkActor actor,
        JsonArray extra, List<string> warnings, DateTime now, CancellationToken ct)
    {
        foreach (var given in aliases)
        {
            if (WorkReferences.NormalizeAlias(given) is not { } alias)
            {
                warnings.Add($"'{given}' is not a valid alias.");
                continue;
            }

            var separator = alias.IndexOf('-');
            var keyed = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM work_items i JOIN work_projects p ON p.id = i.project_id WHERE p.project_key = @prefix AND i.number = @number",
                new { prefix = alias[..separator].ToUpperInvariant(), number = int.Parse(alias[(separator + 1)..]) }, transaction, cancellationToken: ct));
            if (keyed > 0)
            {
                warnings.Add($"The alias '{alias}' is the key of an item, so it was not added.");
                continue;
            }

            var owner = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT item_id FROM work_item_aliases WHERE alias = @alias", new { alias }, transaction, cancellationToken: ct));
            if (owner == itemId)
                continue;
            if (owner is not null)
            {
                warnings.Add($"The alias '{alias}' names another item already, so it was not added.");
                continue;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO work_item_aliases (item_id, alias, created_by, created_at) VALUES (@itemId, @alias, @by, @now)",
                new { itemId, alias, by = actor.Name, now }, transaction, cancellationToken: ct));
            extra.Add(Change("alias", null, alias));
        }
    }

    // ---- Dependencies, comments, attachments -------------------------------------------------

    public Task<DependencySync> SyncImportedDependenciesAsync(string source, string externalKind, string externalId, IReadOnlyList<Guid> blockers, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([DependencyLockName], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var record = await ReadRecordAsync(connection, transaction, source, externalKind, externalId, ct)
                ?? throw WorkException.NotFound($"{source} {externalKind} {externalId} has not been imported.");
            var itemId = record.EntityId;
            var desired = blockers.Distinct().Where(blocker => blocker != itemId).OrderBy(blocker => blocker).ToList();
            var previousRelations = record.RelationsJson is null ? null : ReadList<Guid>(record.RelationsJson);
            // Gated on the source: when the blockers it names are the ones of the last sync, nothing is
            // done, so an edge an agent removed (or added) since stays as it is.
            if (previousRelations is not null && previousRelations.OrderBy(blocker => blocker).SequenceEqual(desired))
            {
                await transaction.CommitAsync(ct);
                return new DependencySync(0, 0, [], []);
            }

            await HoldProjectAsync(connection, transaction, record.ProjectId, ct);
            // The item and every blocker it may gain or lose, held in id order (the store's lock order).
            var owned = ReadList<Guid>(record.EdgesJson);
            var held = desired.Concat(owned).Append(itemId).Distinct().OrderBy(id => id).ToArray();
            await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT id FROM work_items WHERE id IN @held ORDER BY id FOR UPDATE", new { held }, transaction, cancellationToken: ct));
            var row = await ReadItemRowAsync(connection, transaction, itemId, ct)
                ?? throw WorkException.NotFound($"The item imported from {source} {externalId} no longer exists.");

            var current = (await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT blocker_item_id FROM work_item_dependencies WHERE blocked_item_id = @itemId", new { itemId }, transaction, cancellationToken: ct))).ToHashSet();
            var inProject = desired.Count == 0
                ? []
                : (await connection.QueryAsync<Guid>(new CommandDefinition(
                    "SELECT id FROM work_items WHERE id IN @desired AND project_id = @projectId", new { desired = desired.ToArray(), projectId = row.ProjectId },
                    transaction, cancellationToken: ct))).ToHashSet();
            var previous = previousRelations?.ToHashSet() ?? [];
            var extra = new JsonArray();
            var cycles = new List<Guid>();
            var otherProject = new List<Guid>();
            // The import owns only the edges it inserted; an edge that existed already (an agent's)
            // stays the agent's and is never removed by the import.
            var kept = owned.Where(current.Contains).ToHashSet();
            var added = 0;
            foreach (var blocker in desired.Where(blocker => !previous.Contains(blocker)))
            {
                if (current.Contains(blocker))
                    continue;
                if (!inProject.Contains(blocker))
                {
                    otherProject.Add(blocker);
                    continue;
                }

                if (await IsBlockedByAsync(connection, transaction, blocker, itemId, ct))
                {
                    cycles.Add(blocker);
                    continue;
                }

                await InsertEdgesAsync(connection, transaction, [(itemId, blocker)], now, ct);
                extra.Add(Change("blocked_by", null, await KeyOfAsync(connection, transaction, blocker, ct)));
                kept.Add(blocker);
                added++;
            }

            // A blocker the source dropped since the last sync goes, when the import owns its edge.
            var removed = 0;
            foreach (var gone in previous.Where(blocker => !desired.Contains(blocker) && kept.Contains(blocker)).ToList())
            {
                var key = await KeyOfAsync(connection, transaction, gone, ct);
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM work_item_dependencies WHERE blocked_item_id = @itemId AND blocker_item_id = @gone", new { itemId, gone }, transaction, cancellationToken: ct));
                extra.Add(Change("blocked_by", key, null));
                kept.Remove(gone);
                removed++;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_import_records SET edges_json = @edges, relations_json = @relations WHERE source = @source AND external_kind = @externalKind AND external_id = @externalId",
                // A blocker skipped as a cycle is not recorded as synced: it stays pending, so the next
                // sync tries it again (and reports it again while the cycle lasts).
                new { edges = WriteList(kept.OrderBy(blocker => blocker).ToList()), relations = Json(desired.Where(blocker => !cycles.Contains(blocker)).ToList()), source, externalKind, externalId },
                transaction, cancellationToken: ct));
            if (extra.Count == 0)
            {
                await transaction.CommitAsync(ct);
                return new DependencySync(0, 0, cycles, otherProject);
            }

            // A versioned, fed write; updated_at keeps the source's time.
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET version = version + 1 WHERE id = @itemId", new { itemId }, transaction, cancellationToken: ct));
            extra.Add(Change("version", row.Version, row.Version + 1));
            await CommitAsync(connection, transaction,
                [new PendingChange(row.ProjectId, row.ProjectKey, WorkEntityKind.Item, row.Id, row.Key, WorkChangeKinds.ItemUpdated, extra)], actor, now, ct);
            return new DependencySync(added, removed, cycles, otherProject);
        }, ct);

    public Task<int> AddImportedCommentsAsync(Guid itemId, IReadOnlyList<ImportedComment> comments, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET version = version WHERE id = @itemId", new { itemId }, transaction, cancellationToken: ct));
            var row = await ReadItemRowAsync(connection, transaction, itemId, ct) ?? throw WorkException.NotFound($"There is no item {itemId}.");
            var extra = new JsonArray();
            var written = await WriteImportedCommentsAsync(connection, transaction, itemId, comments, extra, now, ct);
            if (written == 0)
            {
                await transaction.CommitAsync(ct);
                return 0;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET version = version + 1 WHERE id = @itemId", new { itemId }, transaction, cancellationToken: ct));
            extra.Add(Change("version", row.Version, row.Version + 1));
            await CommitAsync(connection, transaction,
                [new PendingChange(row.ProjectId, row.ProjectKey, WorkEntityKind.Item, row.Id, row.Key, WorkChangeKinds.ItemUpdated, extra)], actor, now, ct);
            return written;
        }, ct);

    public async Task<AttachmentState?> GetAttachmentStateAsync(Guid itemId, string externalId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return (await connection.QueryAsync<AttachmentState>(new CommandDefinition(
            "SELECT storage_path AS StoragePath, skipped_reason AS SkippedReason FROM work_item_attachments WHERE item_id = @itemId AND external_id = @externalId",
            new { itemId, externalId }, cancellationToken: ct))).FirstOrDefault();
    }

    public Task AddAttachmentAsync(Guid itemId, NewAttachmentSpec attachment, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET version = version WHERE id = @itemId", new { itemId }, transaction, cancellationToken: ct));
            var row = await ReadItemRowAsync(connection, transaction, itemId, ct) ?? throw WorkException.NotFound($"There is no item {itemId}.");
            var prior = (await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT id FROM work_item_attachments WHERE item_id = @itemId AND external_id = @ExternalId",
                new { itemId, attachment.ExternalId }, transaction, cancellationToken: ct))).ToList();
            // A file not stored is recorded once; a stored file (also one stored again because its
            // earlier copy went missing) replaces what the record says.
            if (prior.Count > 0 && attachment.StoragePath is null)
            {
                await transaction.CommitAsync(ct);
                return 0;
            }

            var id = prior.Count > 0 ? prior[0] : Guid.NewGuid();
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO work_item_attachments (id, item_id, external_id, filename, content_type, size_bytes, sha256, storage_path, source_url, skipped_reason, created_by, created_at) " +
                "VALUES (@id, @itemId, @ExternalId, @filename, @contentType, @SizeBytes, @Sha256, @StoragePath, @SourceUrl, @skipped, @by, @createdAt) " +
                "ON DUPLICATE KEY UPDATE sha256 = VALUES(sha256), storage_path = VALUES(storage_path), size_bytes = VALUES(size_bytes), skipped_reason = VALUES(skipped_reason)",
                new
                {
                    id, itemId, attachment.ExternalId, filename = Cap(attachment.Filename, 255), contentType = Cap(attachment.ContentType, 255), attachment.SizeBytes,
                    attachment.Sha256, attachment.StoragePath, attachment.SourceUrl, skipped = attachment.SkippedReason is null ? null : Cap(attachment.SkippedReason, 500),
                    by = actor.Name, createdAt = Stored(attachment.CreatedAt)
                }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET version = version + 1 WHERE id = @itemId", new { itemId }, transaction, cancellationToken: ct));
            var extra = new JsonArray
            {
                Change("attachment", null, new { id, filename = attachment.Filename, size_bytes = attachment.SizeBytes, stored = attachment.StoragePath is not null }),
                Change("version", row.Version, row.Version + 1)
            };
            await CommitAsync(connection, transaction,
                [new PendingChange(row.ProjectId, row.ProjectKey, WorkEntityKind.Item, row.Id, row.Key, WorkChangeKinds.ItemUpdated, extra)], actor, now, ct);
            return 1;
        }, ct);

    public async Task<AttachmentRecord?> GetAttachmentAsync(Guid attachmentId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<AttachmentRecord>(new CommandDefinition(
            "SELECT id AS Id, item_id AS ItemId, filename AS Filename, content_type AS ContentType, size_bytes AS SizeBytes, storage_path AS StoragePath " +
            "FROM work_item_attachments WHERE id = @attachmentId",
            new { attachmentId }, cancellationToken: ct));
    }

    // ---- Settings and runs -------------------------------------------------------------------

    public async Task<ImportSettings?> GetImportSettingsAsync(string source, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var rows = (await connection.QueryAsync<(string SettingsJson, string? ApprovedBy, DateTime? ApprovedAt)>(new CommandDefinition(
            "SELECT settings_json, approved_by, approved_at FROM work_import_settings WHERE source = @source", new { source }, cancellationToken: ct))).ToList();
        if (rows.Count == 0)
            return null;
        var found = rows[0];
        var settings = JsonSerializer.Deserialize<ImportSettings>(found.SettingsJson, WorkJson.Options)!;
        return settings with { ApprovedBy = found.ApprovedBy, ApprovedAt = found.ApprovedAt is { } at ? Utc(at) : null };
    }

    public async Task<ImportSettings> SaveImportSettingsAsync(string source, ImportSettings settings, string? approvedBy, CancellationToken ct = default)
    {
        var now = Now();
        var stored = settings with { ApprovedBy = null, ApprovedAt = null };
        await using var connection = await OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO work_import_settings (source, settings_json, approved_by, approved_at, updated_at) VALUES (@source, @json, @approvedBy, @approvedAt, @now) " +
            "ON DUPLICATE KEY UPDATE settings_json = VALUES(settings_json), approved_by = VALUES(approved_by), approved_at = VALUES(approved_at), updated_at = VALUES(updated_at)",
            new { source, json = Json(stored), approvedBy, approvedAt = approvedBy is null ? (DateTime?)null : now, now },
            cancellationToken: ct));
        return stored with { ApprovedBy = approvedBy, ApprovedAt = approvedBy is null ? null : now };
    }

    private sealed class RunRow
    {
        public Guid Id { get; init; }
        public string Source { get; init; } = "";
        public string Mode { get; init; } = "";
        public string Status { get; init; } = "";
        public string StartedBy { get; init; } = "";
        public DateTime? UpdatedSince { get; init; }
        public string? ReportJson { get; init; }
        public string? Error { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }

        public ImportRunView ToView() => new(
            Id, Source, ParseName<ImportMode>(Mode), Status, StartedBy, Utc(StartedAt), CompletedAt is { } done ? Utc(done) : null,
            UpdatedSince is { } since ? Utc(since) : null,
            ReportJson is null ? null : JsonSerializer.Deserialize<ImportReport>(ReportJson, WorkJson.Options), Error);
    }

    private const string RunColumns = "id, source, mode, status, started_by, updated_since, report_json, error, started_at, completed_at";

    public Task<Guid> StartImportRunAsync(string source, ImportMode mode, DateTime? updatedSince, string startedBy, TimeSpan staleAfter, CancellationToken ct = default) =>
        RunAsync([$"codegraph_work_import_{source}"], async connection =>
        {
            var now = Now();
            var alive = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM work_import_runs WHERE source = @source AND status = 'running' AND heartbeat_at > @cutoff LIMIT 1",
                new { source, cutoff = now - staleAfter }, cancellationToken: ct));
            if (alive is { } running)
                throw new WorkException(WorkErrorCodes.Conflict, $"Import run {running} of {source} is still running. Wait for it to finish.");

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_import_runs SET status = 'failed', error = 'The run stopped sending heartbeats.', completed_at = @now WHERE source = @source AND status = 'running'",
                new { source, now }, cancellationToken: ct));
            var id = Guid.NewGuid();
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO work_import_runs (id, source, mode, status, started_by, updated_since, started_at, heartbeat_at) VALUES (@id, @source, @mode, 'running', @startedBy, @updatedSince, @now, @now)",
                new { id, source, mode = JsonName(mode), startedBy, updatedSince = updatedSince is { } since ? Stored(since) : (DateTime?)null, now }, cancellationToken: ct));
            return id;
        }, ct);

    public async Task HeartbeatImportRunAsync(Guid runId, ImportReport? report, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        // Without a report only the heartbeat moves: the saved progress is never replaced by less.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_import_runs SET heartbeat_at = @now, report_json = COALESCE(@report, report_json) WHERE id = @runId AND status = 'running'",
            new { now = Now(), report = report is null ? null : Json(report), runId }, cancellationToken: ct));
    }

    public async Task<bool> CompleteImportRunAsync(Guid runId, string status, ImportReport? report, string? error, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var now = Now();
        // Only the run itself, while it is running: a run declared dead by a later start is not revived.
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_import_runs SET status = @status, report_json = COALESCE(@report, report_json), error = @error, heartbeat_at = @now, completed_at = @now " +
            "WHERE id = @runId AND status = 'running'",
            new { status, report = report is null ? null : Json(report), error, now, runId }, cancellationToken: ct)) == 1;
    }

    public async Task<ImportRunView?> GetImportRunAsync(Guid runId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            $"SELECT {RunColumns} FROM work_import_runs WHERE id = @runId", new { runId }, cancellationToken: ct));
        return row?.ToView();
    }

    public async Task<IReadOnlyList<ImportRunView>> ListImportRunsAsync(string source, int limit, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<RunRow>(new CommandDefinition(
            // The list leaves the reports out; read a run for its report.
            "SELECT id, source, mode, status, started_by, updated_since, NULL AS report_json, error, started_at, completed_at FROM work_import_runs " +
            "WHERE source = @source ORDER BY started_at DESC LIMIT @limit",
            new { source, limit = Math.Clamp(limit, 1, 100) }, cancellationToken: ct));
        return rows.Select(row => row.ToView()).ToList();
    }

    public async Task<IReadOnlyList<IndexedRepository>> ListIndexedRepositoriesAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<IndexedRepository>(new CommandDefinition(
            "SELECT r.name AS Name, r.repo_url AS RepoUrl, " +
            "(SELECT p.project_key FROM work_project_repositories w JOIN work_projects p ON p.id = w.project_id WHERE w.repository_name = r.name AND w.role = 'owned' LIMIT 1) AS OwnedBy " +
            "FROM repositories r ORDER BY r.name",
            cancellationToken: ct));
        return rows.ToList();
    }

    // Applied repositories are import records of kind 'repository' keyed by a hash of (project, repository): the
    // name can be longer than an external id; the record's snapshot holds it.
    private static string AppliedRepositoryId(Guid projectId, string repository) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{projectId}\n{repository.ToLowerInvariant()}")));

    public async Task<IReadOnlySet<string>> ListAppliedRepositoriesAsync(string source, Guid projectId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT snapshot_json FROM work_import_records WHERE source = @source AND external_kind = 'repository' AND project_id = @projectId",
            new { source, projectId }, cancellationToken: ct));
        return rows.Select(json => JsonSerializer.Deserialize<string>(json, SnapshotOptions)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task RecordAppliedRepositoriesAsync(string source, Guid projectId, IReadOnlyCollection<string> repositories, CancellationToken ct = default)
    {
        if (repositories.Count == 0)
            return;
        await using var connection = await OpenAsync(ct);
        var now = Now();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO work_import_records (source, external_kind, external_id, entity_kind, entity_id, project_id, content_hash, snapshot_json, imported_at) " +
            "VALUES (@source, 'repository', @externalId, 'project', @projectId, @projectId, @externalId, @snapshot, @now)",
            repositories.Distinct(StringComparer.OrdinalIgnoreCase).Select(repository => new
            {
                source, externalId = AppliedRepositoryId(projectId, repository), projectId, snapshot = SnapshotJson(repository), now
            }), cancellationToken: ct));
    }

    public async Task<ImportRunView?> LastCompletedImportAsync(string source, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<RunRow>(new CommandDefinition(
            $"SELECT {RunColumns} FROM work_import_runs WHERE source = @source AND mode = 'import' AND status = 'completed' ORDER BY started_at DESC LIMIT 1",
            new { source }, cancellationToken: ct));
        return row?.ToView();
    }
}
