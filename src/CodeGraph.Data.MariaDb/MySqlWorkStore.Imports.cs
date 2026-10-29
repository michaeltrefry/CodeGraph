using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using Dapper;
using MySqlConnector;

namespace CodeGraph.Data.MariaDb;

// Imports (sc-27876, design §10): upserts keyed by the source object, in the store's lock order
// (dependency lock, project row, item rows, change-feed lock), each with its change-feed entry
// attributed to the importing actor. The source's own created/updated times and comment authors
// and times are kept; the feed entries carry the time of the import.
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
        public DateTime? SourceUpdatedAt { get; init; }

        public ImportRecord ToRecord() =>
            new(ExternalId, EntityKind, EntityId, ProjectId, ContentHash, ReadList<Guid>(EdgesJson), SourceUpdatedAt is { } at ? Utc(at) : null);
    }

    private const string RecordColumns = "external_id, entity_kind, entity_id, project_id, content_hash, edges_json, source_updated_at";

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
        Guid entityId, Guid projectId, string hash, DateTime? sourceUpdatedAt, DateTime now, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO work_import_records (source, external_kind, external_id, entity_kind, entity_id, project_id, content_hash, source_updated_at, imported_at) " +
            "VALUES (@source, @kind, @externalId, @entityKind, @entityId, @projectId, @hash, @sourceUpdatedAt, @now) " +
            "ON DUPLICATE KEY UPDATE entity_kind = VALUES(entity_kind), entity_id = VALUES(entity_id), project_id = VALUES(project_id), " +
            "content_hash = VALUES(content_hash), source_updated_at = VALUES(source_updated_at), imported_at = VALUES(imported_at)",
            new { source, kind, externalId, entityKind, entityId, projectId, hash, sourceUpdatedAt, now }, transaction, cancellationToken: ct));

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
            var existing = record is null ? null : await ReadFeatureRowAsync(connection, transaction, record.EntityId, ct);
            if (existing is not null && existing.ProjectId != spec.ProjectId)
                throw WorkException.Invalid($"Imported feature {existing.Key} is in another project than the one the import maps it to now. Nothing was changed.");

            var title = Cap(spec.Title, 240);
            var imported = JsonSerializer.Serialize(spec.Imported, WorkJson.Options);
            var documents = spec.Documents.Select(document => new DocumentSpec(Cap(document.Title, 200), document.Body)).ToList();
            if (existing is not null && record!.ContentHash == spec.ContentHash)
            {
                await transaction.CommitAsync(ct);
                return new ImportWrite(existing.Id, existing.Key, existing.ProjectId, ImportOutcome.Unchanged);
            }

            if (existing is null)
            {
                var id = Guid.NewGuid();
                var number = project.NextFeatureNumber;
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO work_features (id, project_id, number, title, description, version, created_by, created_by_kind, created_session_id, created_at, updated_at, imported_json, requirements_unreconciled) " +
                    "VALUES (@id, @projectId, @number, @title, @Description, 1, @actorName, @actorKind, NULL, @createdAt, @updatedAt, @imported, @unreconciled)",
                    new
                    {
                        id, projectId = project.Id, number, title, spec.Description, actorName = actor.Name, actorKind = actor.Kind,
                        createdAt = Stored(spec.CreatedAt), updatedAt = Stored(spec.UpdatedAt), imported, unreconciled = spec.RequirementsUnreconciled
                    }, transaction, cancellationToken: ct));
                await InsertDocumentsAsync(connection, transaction, id, 0, documents, actor, now, ct);
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_projects SET next_feature_number = next_feature_number + 1 WHERE id = @projectId", new { projectId = project.Id }, transaction, cancellationToken: ct));
                await WriteRecordAsync(connection, transaction, spec.Source, "epic", spec.ExternalId, "feature", id, project.Id, spec.ContentHash, Stored(spec.UpdatedAt), now, ct);
                var view = (await LoadFeatureViewAsync(connection, transaction, id, withCounts: false, ct))!;
                var entry = FeatureCreatedEntry(view);
                entry.Changes.Add(Change("imported", null, spec.Imported));
                await CommitAsync(connection, transaction, [entry], actor, now, ct);
                return new ImportWrite(id, view.Key, project.Id, ImportOutcome.Created);
            }

            var before = (await LoadFeatureViewAsync(connection, transaction, existing.Id, withCounts: false, ct))!;
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_features SET title = @title, description = @Description, imported_json = @imported, requirements_unreconciled = @unreconciled, " +
                "version = version + 1, updated_at = @updatedAt WHERE id = @id",
                new { title, spec.Description, imported, unreconciled = spec.RequirementsUnreconciled, updatedAt = Stored(spec.UpdatedAt), id = existing.Id },
                transaction, cancellationToken: ct));

            // Documents match by title: a changed body is updated, a new title added; none is removed.
            var added = new List<DocumentSpec>();
            var changed = new List<(DocumentView Before, DocumentView After)>();
            foreach (var document in documents)
            {
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
            await WriteRecordAsync(connection, transaction, spec.Source, "epic", spec.ExternalId, "feature", existing.Id, project.Id, spec.ContentHash, Stored(spec.UpdatedAt), now, ct);
            var after = (await LoadFeatureViewAsync(connection, transaction, existing.Id, withCounts: false, ct))!;
            var addedViews = after.Documents.Where(document => before.Documents.All(prior => prior.Id != document.Id)).ToList();
            var changes = WithDocuments(Diff(Snapshot(before), Snapshot(after)), addedViews, changed);
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

            var title = Cap(spec.Title, 240);
            var labels = JoinLabels(spec.Labels.Select(label => label.Replace(',', ' ').Trim()).Where(label => label.Length > 0).Distinct().ToList());
            var imported = JsonSerializer.Serialize(spec.Imported, WorkJson.Options);
            var extra = new JsonArray();
            ItemView? before = null;
            Guid id;
            if (existing is null)
            {
                id = Guid.NewGuid();
                var criteria = NormalizeCriteria([], spec.AcceptanceCriteria);
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO work_items (id, project_id, number, feature_id, item_type, title, description, status, priority, sort_order, labels, repository_name, " +
                    "acceptance_json, imported_json, version, created_by, created_by_kind, created_session_id, created_at, updated_at) " +
                    "VALUES (@id, @projectId, @number, @featureId, @type, @title, @Description, @status, @priority, @Order, @labels, @repository, " +
                    "@acceptance, @imported, 1, @actorName, @actorKind, NULL, @createdAt, @updatedAt)",
                    new
                    {
                        id, projectId = project.Id, number = project.NextItemNumber, featureId, type = JsonName(spec.Type), title, spec.Description,
                        status = WorkStatuses.Name(spec.Status), priority = JsonName(spec.Priority), spec.Order, labels, repository,
                        acceptance = WriteList(criteria), imported, actorName = actor.Name, actorKind = actor.Kind,
                        createdAt = Stored(spec.CreatedAt), updatedAt = Stored(spec.UpdatedAt)
                    }, transaction, cancellationToken: ct));
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_projects SET next_item_number = next_item_number + 1 WHERE id = @projectId", new { projectId = project.Id }, transaction, cancellationToken: ct));
            }
            else
            {
                id = existing.Id;
                before = await ToViewAsync(connection, transaction, existing, ct);
                var criteria = NormalizeCriteria(before.AcceptanceCriteria ?? [], spec.AcceptanceCriteria);
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE work_items SET feature_id = @featureId, item_type = @type, title = @title, description = @Description, status = @status, priority = @priority, " +
                    "labels = @labels, repository_name = COALESCE(@repository, repository_name), acceptance_json = @acceptance, imported_json = @imported, " +
                    "version = version + 1, updated_at = @updatedAt WHERE id = @id",
                    new
                    {
                        featureId, type = JsonName(spec.Type), title, spec.Description, status = WorkStatuses.Name(spec.Status), priority = JsonName(spec.Priority),
                        labels, repository, acceptance = WriteList(criteria), imported, updatedAt = Stored(spec.UpdatedAt), id
                    }, transaction, cancellationToken: ct));
            }

            await WriteImportedCommentsAsync(connection, transaction, id, spec.Comments, extra, ct);
            await WriteLinksAsync(connection, transaction, id, spec.Links, [], actor, extra, now, ct);
            await WriteImportedAliasesAsync(connection, transaction, id, spec.Aliases, actor, extra, warnings, now, ct);
            await WriteRecordAsync(connection, transaction, spec.Source, spec.ExternalKind, spec.ExternalId, "item", id, project.Id, spec.ContentHash, Stored(spec.UpdatedAt), now, ct);

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
            }

            foreach (var node in extra)
                change.Changes.Add(node!.DeepClone());
            await CommitAsync(connection, transaction, [change], actor, now, ct);
            return new ImportWrite(id, after.Key, after.ProjectId, before is null ? ImportOutcome.Created : ImportOutcome.Updated, warnings.Count == 0 ? null : warnings);
        }, ct);

    private sealed class ExternalCommentRow
    {
        public Guid Id { get; init; }
        public string ExternalId { get; init; } = "";
        public string Body { get; init; } = "";
    }

    private static async Task<int> WriteImportedCommentsAsync(MySqlConnection connection, MySqlTransaction transaction, Guid itemId, IReadOnlyList<ImportedComment> comments, JsonArray extra, CancellationToken ct)
    {
        if (comments.Count == 0)
            return 0;

        var existing = (await connection.QueryAsync<ExternalCommentRow>(new CommandDefinition(
            "SELECT id, external_id, body FROM work_item_comments WHERE item_id = @itemId AND external_id IS NOT NULL",
            new { itemId }, transaction, cancellationToken: ct))).ToDictionary(row => row.ExternalId, StringComparer.Ordinal);
        var written = 0;
        foreach (var comment in comments.DistinctBy(comment => comment.ExternalId))
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

            var id = Guid.NewGuid();
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO work_item_comments (id, item_id, author, author_kind, author_session_id, body, created_at, external_id) " +
                "VALUES (@id, @itemId, @Author, @AuthorKind, NULL, @Body, @createdAt, @ExternalId)",
                new { id, itemId, comment.Author, comment.AuthorKind, comment.Body, createdAt = Stored(comment.CreatedAt), comment.ExternalId },
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
            await HoldProjectAsync(connection, transaction, record.ProjectId, ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET version = version WHERE id = @itemId", new { itemId }, transaction, cancellationToken: ct));
            var row = await ReadItemRowAsync(connection, transaction, itemId, ct)
                ?? throw WorkException.NotFound($"The item imported from {source} {externalId} no longer exists.");

            var current = (await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT blocker_item_id FROM work_item_dependencies WHERE blocked_item_id = @itemId", new { itemId }, transaction, cancellationToken: ct))).ToHashSet();
            var desired = blockers.Distinct().ToList();
            var inProject = desired.Count == 0
                ? []
                : (await connection.QueryAsync<Guid>(new CommandDefinition(
                    "SELECT id FROM work_items WHERE id IN @desired AND project_id = @projectId", new { desired = desired.ToArray(), projectId = row.ProjectId },
                    transaction, cancellationToken: ct))).ToHashSet();
            var extra = new JsonArray();
            var cycles = new List<Guid>();
            var otherProject = new List<Guid>();
            var kept = new List<Guid>();
            var added = 0;
            foreach (var blocker in desired)
            {
                if (current.Contains(blocker))
                {
                    kept.Add(blocker);
                    continue;
                }

                if (!inProject.Contains(blocker))
                {
                    otherProject.Add(blocker);
                    continue;
                }

                if (blocker == itemId || await IsBlockedByAsync(connection, transaction, blocker, itemId, ct))
                {
                    cycles.Add(blocker);
                    continue;
                }

                await InsertEdgesAsync(connection, transaction, [(itemId, blocker)], now, ct);
                extra.Add(Change("blocked_by", null, await KeyOfAsync(connection, transaction, blocker, ct)));
                kept.Add(blocker);
                added++;
            }

            var removed = 0;
            foreach (var gone in ReadList<Guid>(record.EdgesJson).Where(previous => current.Contains(previous) && !desired.Contains(previous)))
            {
                var key = await KeyOfAsync(connection, transaction, gone, ct);
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM work_item_dependencies WHERE blocked_item_id = @itemId AND blocker_item_id = @gone", new { itemId, gone }, transaction, cancellationToken: ct));
                extra.Add(Change("blocked_by", key, null));
                removed++;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_import_records SET edges_json = @edges WHERE source = @source AND external_kind = @externalKind AND external_id = @externalId",
                new { edges = WriteList(kept), source, externalKind, externalId }, transaction, cancellationToken: ct));
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
            var written = await WriteImportedCommentsAsync(connection, transaction, itemId, comments, extra, ct);
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

    public async Task<bool> HasAttachmentAsync(Guid itemId, string externalId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM work_item_attachments WHERE item_id = @itemId AND external_id = @externalId AND storage_path IS NOT NULL",
            new { itemId, externalId }, cancellationToken: ct)) > 0;
    }

    public Task AddAttachmentAsync(Guid itemId, NewAttachmentSpec attachment, WorkActor actor, CancellationToken ct = default) =>
        RunAsync([], async connection =>
        {
            var now = Now();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE work_items SET version = version WHERE id = @itemId", new { itemId }, transaction, cancellationToken: ct));
            var row = await ReadItemRowAsync(connection, transaction, itemId, ct) ?? throw WorkException.NotFound($"There is no item {itemId}.");
            var prior = (await connection.QueryAsync<(Guid Id, string? StoragePath)>(new CommandDefinition(
                "SELECT id, storage_path FROM work_item_attachments WHERE item_id = @itemId AND external_id = @ExternalId",
                new { itemId, attachment.ExternalId }, transaction, cancellationToken: ct))).ToList();
            // Stored once; a skipped one is recorded once and replaced only when a later run stores it.
            if (prior.Count > 0 && (prior[0].StoragePath is not null || attachment.StoragePath is null))
            {
                await transaction.CommitAsync(ct);
                return 0;
            }

            var id = prior.Count > 0 ? prior[0].Id : Guid.NewGuid();
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
            new { source, json = JsonSerializer.Serialize(stored, WorkJson.Options), approvedBy, approvedAt = approvedBy is null ? (DateTime?)null : now, now },
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

    public async Task HeartbeatImportRunAsync(Guid runId, ImportReport report, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_import_runs SET heartbeat_at = @now, report_json = @report WHERE id = @runId",
            new { now = Now(), report = JsonSerializer.Serialize(report, WorkJson.Options), runId }, cancellationToken: ct));
    }

    public async Task CompleteImportRunAsync(Guid runId, string status, ImportReport report, string? error, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var now = Now();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE work_import_runs SET status = @status, report_json = @report, error = @error, heartbeat_at = @now, completed_at = @now WHERE id = @runId",
            new { status, report = JsonSerializer.Serialize(report, WorkJson.Options), error, now, runId }, cancellationToken: ct));
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

    public async Task<DateTime?> LastCompletedImportStartAsync(string source, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var started = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(started_at) FROM work_import_runs WHERE source = @source AND mode = 'import' AND status = 'completed'",
            new { source }, cancellationToken: ct));
        return started is { } at ? Utc(at) : null;
    }
}
