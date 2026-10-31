using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

public sealed partial class DreamingRunAcceptanceMariaDbTests
{
    // The native test server defaults to general_ci. Force the historical/new-table mismatch
    // explicitly; otherwise a clean schema silently misses the production failure.
    [Theory]
    [InlineData("repository_name", "repository", false)]
    [InlineData("path", "rel_path", false)]
    [InlineData("content_hash", "content_hash", false)]
    [InlineData("path", "rel_path", true)]
    [InlineData("content_hash", "content_hash", true)]
    public async Task Mixed_file_collations_allow_scheduled_digest_full_reads_and_source_verification(string baselineColumn, string indexColumn, bool reverse)
    {
        var (project, entry) = await StaleFactAsync();
        var emptyProject = await ProjectAsync();
        var model = new ScriptedModel(Response(Call("full", "dream_read", new { id = entry.Id, detail = "full" })),
            Response(Call("source", "dream_read_source", new { repository = "codegraph", path = "Source.cs" })),
            Response(Call("verify", "dream_verify", Verify(entry))), Response());
        var service = await ServiceAsync(model);
        await using var metadata = new MySqlConnection(db.ConnectionString);
        var nativeRepositoryCollation = await metadata.QuerySingleAsync<string>("SELECT collation_name FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='file_hashes' AND column_name='repository'");
        var indexCollation = indexColumn == "repository" ? nativeRepositoryCollation : reverse ? "utf8mb4_uca1400_ai_ci" : "utf8mb4_unicode_ci";
        var baselineCollation = indexCollation == "utf8mb4_uca1400_ai_ci" ? "utf8mb4_unicode_ci" : "utf8mb4_uca1400_ai_ci";
        await using var baseline = await ColumnCollationAsync("knowledge_entry_files", baselineColumn, baselineCollation);
        await using var index = await ColumnCollationAsync("file_hashes", indexColumn, indexCollation);

        var empty = await service.RunAsync(emptyProject, "schedule", Human);
        empty.Status.ShouldBe("skipped", empty.Reason);
        empty.Reason.ShouldBe("no_signal");
        model.Requests.ShouldBeEmpty();

        var run = await service.RunAsync(project, "schedule", Human);

        run.Status.ShouldBe("completed", run.Reason);
        SuccessfulWrites(run, 3);
        model.Requests.Count.ShouldBe(4);
        (await db.Store.GetKnowledgeAsync(entry.Id, false))!.PossiblyStale.ShouldBeFalse();
        var persisted = await runs.GetRunAsync(project, run.Id);
        persisted!.Status.ShouldBe("completed");
        persisted.Actions.Count.ShouldBe(3);
        (await service.RunAsync(project, "schedule", Human)).Reason.ShouldBe("no_signal");
        model.Requests.Count.ShouldBe(4);

        await using var c = new MySqlConnection(db.ConnectionString);
        // Retain the old case-insensitive repository/path matching as well as freshness states.
        await c.ExecuteAsync("UPDATE knowledge_entry_files SET repository_name=UPPER(repository_name),path=LOWER(path) WHERE entry_id=@entry", new { entry = entry.Id });
        (await db.Store.GetKnowledgeAsync(entry.Id, false))!.PossiblyStale.ShouldBeFalse();
        var hash = await c.QuerySingleAsync<string>("SELECT content_hash FROM file_hashes WHERE repository='codegraph' AND rel_path='Source.cs'");
        try
        {
            await c.ExecuteAsync("DELETE FROM file_hashes WHERE repository='codegraph' AND rel_path='Source.cs'");
            (await db.Store.GetKnowledgeAsync(entry.Id, false))!.PossiblyStale.ShouldBeTrue();
            await c.ExecuteAsync("UPDATE knowledge_entry_files SET content_hash=NULL WHERE entry_id=@entry", new { entry = entry.Id });
            (await db.Store.GetKnowledgeAsync(entry.Id, false))!.PossiblyStale.ShouldBeFalse();
        }
        finally
        {
            await graph.UpsertFileHashBatchAsync("codegraph", new Dictionary<string, string> { ["Source.cs"] = hash });
        }
        (await c.QuerySingleAsync<string>("SELECT content_hash FROM knowledge_entry_files WHERE entry_id=@entry", new { entry = entry.Id })).ShouldBe(hash);
        (await db.Store.GetKnowledgeAsync(entry.Id, false))!.PossiblyStale.ShouldBeFalse();
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("proposal")]
    [InlineData("rejection")]
    [InlineData("verification")]
    public async Task Mixed_repository_collations_preserve_project_scoped_graph_evidence(string surface)
    {
        var project = await ProjectAsync();
        await using var c = new MySqlConnection(db.ConnectionString);
        var node = await c.ExecuteScalarAsync<long>("INSERT INTO nodes(repository,label,name,qualified_name,file_path) VALUES('codegraph','Method','Collation',@name,''); SELECT LAST_INSERT_ID()", new { name = "Collation.Method" + Guid.NewGuid().ToString("N") });
        var foreign = await c.ExecuteScalarAsync<long>("INSERT INTO nodes(repository,label,name,qualified_name,file_path) VALUES('inference','Method','Private',@name,''); SELECT LAST_INSERT_ID()", new { name = "Collation.Private" + Guid.NewGuid().ToString("N") });
        var entry = (await ((IKnowledgeStore)db.Store).RecordKnowledgeAsync(project,
            new(KnowledgeKind.Fact, "Graph collation evidence", null, KnowledgeConfidence.High, null, null, ["codegraph"], [], [node], null), Human, null)).Entry;
        var runId = Guid.NewGuid();
        await runs.SaveRunAsync(new(runId, project, "schedule", Human, "running", db.Clock.GetUtcNow().UtcDateTime,
            null, "scripted", "scripted", 0, 0, 0, 0, 0, null, []));
        DreamProposalResult? proposal = null;
        if (surface == "rejection")
        {
            using var initialScope = DreamExecutionScope.Begin(runId, project, "scripted");
            proposal = await db.Store.CreateDreamProposalAsync(project, runId,
                new(DreamProposalAction.Retract, entry.Id, entry.Version, "Review the graph", "one node", [new(DreamEvidenceKind.Entry, entry.Id.ToString())]), initialScope.Actor);
        }
        var nativeRepositoryCollation = await c.QuerySingleAsync<string>("SELECT collation_name FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='nodes' AND column_name='repository'");
        await using var work = await ColumnCollationAsync("work_project_repositories", "repository_name", nativeRepositoryCollation == "utf8mb4_uca1400_ai_ci" ? "utf8mb4_unicode_ci" : "utf8mb4_uca1400_ai_ci");
        if (surface == "rejection")
        {
            var reviewed = await db.Store.ReviewDreamProposalAsync(project, proposal!.Proposal!.Id, proposal.Proposal.Version, DreamReviewAction.Reject, "Keep current knowledge", Human);
            reviewed.Proposal.Status.ShouldBe(DreamProposalStatus.Rejected);
            return;
        }
        using var scope = DreamExecutionScope.Begin(runId, project, "scripted");
        if (surface == "proposal")
        {
            var result = await db.Store.CreateDreamProposalAsync(project, runId,
                new(DreamProposalAction.Retract, entry.Id, entry.Version, "Review the graph", "one node", [new(DreamEvidenceKind.GraphNode, node.ToString())]), scope.Actor);
            result.Created.ShouldBeTrue();
            (await Should.ThrowAsync<WorkException>(() => db.Store.CreateDreamProposalAsync(project, runId,
                new(DreamProposalAction.Retract, entry.Id, entry.Version, "Foreign node", "one node", [new(DreamEvidenceKind.GraphNode, foreign.ToString())]), scope.Actor))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        }
        else if (surface == "verification")
        {
            await sources.ExecuteAsync(project, "dream_read_source", JsonSerializer.SerializeToElement(new { node_id = node }), default);
            await ((IKnowledgeDreamActions)db.Service).ExecuteToolAsync("dream_verify", JsonSerializer.SerializeToElement(Verify(entry)), scope.Actor);
            (await db.Store.GetKnowledgeAsync(entry.Id, false))!.Version.ShouldBe(entry.Version + 1);
        }
        else
        {
            await db.Store.ValidateDreamEvidenceAsync(project, [new("graph_node", node.ToString(), "In this project")]);
            (await Should.ThrowAsync<WorkException>(() => db.Store.ValidateDreamEvidenceAsync(project, [new("graph_node", foreign.ToString(), "Foreign")]))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        }
    }

    [Fact]
    public async Task Mixed_run_and_revision_session_collations_allow_digest_exclusion_of_completed_outputs()
    {
        var project = await ProjectAsync();
        var entry = await RecordAsync(project, "External signal");
        await using var runIds = await ColumnCollationAsync("dreaming_runs", "id", "utf8mb4_uca1400_ai_ci");
        await using var sessions = await ColumnCollationAsync("knowledge_revisions", "author_session_id", "utf8mb4_unicode_ci");
        var model = new ScriptedModel(Response(Call("own", "dream_record", new
        {
            entry = new { kind = "fact", summary = "Completed dream output", confidence = "high" },
            evidence = Evidence(entry), prevalence = "one source entry"
        })), Response());
        var service = await ServiceAsync(model);
        var run = await service.RunAsync(project, "schedule", Human);
        run.Status.ShouldBe("completed", run.Reason);
        (await service.RunAsync(project, "schedule", Human)).Reason.ShouldBe("no_signal");
        model.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Mixed_trail_identifiers_and_json_collations_keep_every_kind_and_full_content_reachable()
    {
        var project = await ProjectAsync();
        await RecordAsync(project, "Revision 界");
        var item = (await db.NewItemAsync(project.ToString(), "Trail item")).GetProperty("id").GetGuid();
        await using var c = new MySqlConnection(db.ConnectionString);
        var now = db.Clock.GetUtcNow().UtcDateTime;
        await c.ExecuteAsync("INSERT INTO work_attempts(id,item_id,number,holder,marker,outcome,started_at) VALUES(@id,@item,1,'alice','marker','success',@now)", new { id = Guid.NewGuid(), item, now });
        await c.ExecuteAsync("INSERT INTO work_handoffs(id,item_id,author,done,decisions_json,open_issues_json,corrections_json,created_at) VALUES(@id,@item,'alice','Done 界','[]','[]','[]',@now)", new { id = Guid.NewGuid(), item, now });
        await c.ExecuteAsync("INSERT INTO work_item_comments(id,item_id,author,body,created_at) VALUES(@id,@item,'alice','Thread 界',@now)", new { id = Guid.NewGuid(), item, now });
        await c.ExecuteAsync("INSERT INTO work_github_evidence(id,item_id,kind,evidence_key,author_login,source,priority,body,created_at,updated_at) VALUES(@id,@item,'review_comment',@key,'reviewer','github','medium','Review 界',@now,@now)", new { id = Guid.NewGuid(), key = Guid.NewGuid().ToString(), item, now });
        await db.CallAsync(WorkTools.Post, new { project, item, body = "Message 界", kind = "info" });
        await using var attempts = await ColumnCollationAsync("work_attempts", "id", "utf8mb4_unicode_ci");
        await using var reviews = await ColumnCollationAsync("work_github_evidence", "id", "utf8mb4_uca1400_ai_ci");
        await using var revisions = await ColumnCollationAsync("knowledge_revisions", "snapshot_json", "utf8mb4_unicode_ci");
        await using var messageItems = await ColumnCollationAsync("work_messages", "item_id", "utf8mb4_uca1400_ai_ci");
        using var scope = DreamExecutionScope.Begin(Guid.NewGuid(), project, "scripted");
        var references = await db.Store.ListDreamWorkTrailAsync(project, null);
        references.Select(row => row.Kind).Order().ShouldBe(new[] { "attempt", "handoff", "message", "review", "revision", "thread" });
        foreach (var reference in references)
        {
            var row = await db.Store.ReadDreamWorkTrailItemAsync(project, reference);
            row.GetProperty("id").GetString().ShouldBe(reference.Id);
            if (reference.Kind == "revision")
            {
                reference.ItemId.ShouldBeNull();
                row.TryGetProperty("item_id", out _).ShouldBeFalse(); // WorkJson omits null fields.
            }
            else
            {
                reference.ItemId.ShouldBe(item);
                row.GetProperty("item_id").GetGuid().ShouldBe(item);
            }
            if (reference.Kind != "attempt") row.GetProperty("data").GetRawText().ShouldContain("界");
        }
        // The same UNION participates in the scheduled digest's activity fingerprint.
        (await db.Store.ReadDreamDigestAsync(project, null)).HasSignal.ShouldBeTrue();
    }

    [Fact]
    public async Task Mixed_collations_preserve_native_repository_scope_and_distinct_file_identity()
    {
        var native = new WorkDatabase();
        try
        {
            await native.InitializeAsync(useLegacyGraphCollation: true);
            await using var c = new MySqlConnection(native.ConnectionString);
            (await c.QuerySingleAsync<string>("SELECT collation_name FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='repositories' AND column_name='name'")).ShouldBe("utf8mb4_general_ci");
            // general_ci distinguishes these; Unicode/UCA equate them. A blanket COLLATE on
            // the indexed side would authorize the foreign repository and match both files.
            await c.ExecuteAsync("INSERT INTO repositories(name) VALUES('straße'),('strasse')");
            var key = await native.NewProjectAsync("COL", "straße");
            var project = (await native.Store.ResolveAsync(key))!.Id;
            var entry = (await ((IKnowledgeStore)native.Store).RecordKnowledgeAsync(project,
                new(KnowledgeKind.Fact, "Native repository identity", null, KnowledgeConfidence.High, null, null, null, [], [], null), Human, null)).Entry;
            var own = await c.ExecuteScalarAsync<long>("INSERT INTO nodes(repository,label,name,qualified_name,file_path) VALUES('straße','Method','Own','Collation.Own',''); SELECT LAST_INSERT_ID()");
            var foreign = await c.ExecuteScalarAsync<long>("INSERT INTO nodes(repository,label,name,qualified_name,file_path) VALUES('strasse','Method','Foreign','Collation.Foreign',''); SELECT LAST_INSERT_ID()");
            await c.ExecuteAsync("INSERT INTO file_hashes(repository,rel_path,content_hash) VALUES('straße','Straße.cs','unchanged'),('straße','Strasse.cs','other-file')");
            await c.ExecuteAsync("INSERT INTO knowledge_entry_files(entry_id,repository_name,path,content_hash) VALUES(@entry,'straße','Straße.cs','unchanged')", new { entry = entry.Id });
            const string escapedPath = "folder/quote\"\\界\n🙂.cs";
            await c.ExecuteAsync("INSERT INTO file_hashes(repository,rel_path,content_hash) VALUES('straße',@path,'escaped')", new { path = escapedPath });
            await c.ExecuteAsync("INSERT INTO knowledge_entry_files(entry_id,repository_name,path,content_hash) VALUES(@entry,'straße',@path,'escaped')", new { entry = entry.Id, path = escapedPath });
            var runId = Guid.NewGuid();
            await c.ExecuteAsync("INSERT INTO dreaming_runs(id,project_id,status,started_at,run_json) VALUES(@runId,@project,'running',UTC_TIMESTAMP(6),'{}')", new { runId, project });
            await using var projects = await ColumnCollationAsync("work_project_repositories", "repository_name", "utf8mb4_uca1400_ai_ci", native);
            await using var files = await ColumnCollationAsync("knowledge_entry_files", "path", "utf8mb4_uca1400_ai_ci", native);
            await using var repositories = await ColumnCollationAsync("knowledge_entry_files", "repository_name", "utf8mb4_uca1400_ai_ci", native);
            using (var scope = DreamExecutionScope.Begin(runId, project, "scripted"))
            {
                await native.Store.ValidateDreamEvidenceAsync(project, [new("graph_node", own.ToString(), "Current repository")]);
                (await Should.ThrowAsync<WorkException>(() => native.Store.ValidateDreamEvidenceAsync(project,
                    [new("graph_node", foreign.ToString(), "Different repository")]))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
                (await Should.ThrowAsync<WorkException>(() => native.Store.CreateDreamProposalAsync(project, runId,
                    new(DreamProposalAction.Retract, entry.Id, entry.Version, "Foreign graph", "one node", [new(DreamEvidenceKind.GraphNode, foreign.ToString())]), scope.Actor))).Code.ShouldBe(WorkErrorCodes.InvalidArgument);
                var proposal = await native.Store.CreateDreamProposalAsync(project, runId,
                    new(DreamProposalAction.Retract, entry.Id, entry.Version, "Own graph", "one node", [new(DreamEvidenceKind.GraphNode, own.ToString())]), scope.Actor);
                proposal.Created.ShouldBeTrue();
            }
            (await native.Store.GetKnowledgeAsync(entry.Id, false))!.PossiblyStale.ShouldBeFalse();
            var viewed = (await native.Store.GetKnowledgeAsync(entry.Id, false))!;
            viewed.VerifiedFiles!.Count.ShouldBe(2);
            viewed.VerifiedFiles.ShouldContain(file => file.Path == escapedPath);
            (await native.Store.ReadDreamDigestAsync(project, null)).HasSignal.ShouldBeTrue();
        }
        finally { await native.DisposeAsync(); }
    }

    private async Task<IAsyncDisposable> ColumnCollationAsync(string table, string column, string collation, WorkDatabase? database = null)
    {
        var c = new MySqlConnection((database ?? db).ConnectionString);
        await c.OpenAsync();
        var definition = await c.QuerySingleAsync<(string Type, string Nullable, string Collation)>(
            "SELECT column_type,is_nullable,collation_name FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table AND column_name=@column", new { table, column });
        string Sql(string value) => $"ALTER TABLE `{table}` MODIFY `{column}` {definition.Type} CHARACTER SET utf8mb4 COLLATE {value} {(definition.Nullable == "YES" ? "NULL" : "NOT NULL")}";
        // Leave the indexed FK family at its native collation when it already matches.
        if (definition.Collation == collation) return new RestoreColumnCollation(c, "SELECT 1");
        try { await c.ExecuteAsync(Sql(collation)); }
        catch { await c.DisposeAsync(); throw; }
        return new RestoreColumnCollation(c, Sql(definition.Collation));
    }

    private sealed class RestoreColumnCollation(MySqlConnection connection, string sql) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await connection.ExecuteAsync(sql); }
            finally { await connection.DisposeAsync(); }
        }
    }
}
