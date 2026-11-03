using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Embeddings;
using CodeGraph.Services.Memory;
using CodeGraph.Services.Work;
using CodeGraph.Tests.Work;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Memory;

/// <summary>
/// Project scoping for memory (CG-156) against a real MariaDB: a write is scoped to its session's
/// project (or an explicit one), migration 114 backfills memory that names a member repository as a
/// whole token, and graph, search and entity reads filtered by project answer only that project's
/// entities and claims, per user.
/// </summary>
public sealed class MemoryProjectScopeMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    [Fact]
    public async Task The_resolver_scopes_a_write_to_an_explicit_project_or_to_the_project_of_the_writing_session()
    {
        var project = await db.NewProjectAsync("MR", "sceneworks");
        // No project named: start_session resolves the project owning the session's repository.
        var started = await db.CallAsync(WorkTools.StartSession, new { repository = "sceneworks", harness = "claude-code" });
        var sessionId = started.GetProperty("session").GetProperty("id").GetString();
        var resolver = new WorkMemoryProjectResolver(db.Store);

        (await resolver.ResolveAsync(null, sessionId)).ShouldBe(project);
        (await resolver.ResolveAsync(project, null)).ShouldBe(project);
        (await resolver.ResolveAsync(null, null)).ShouldBeNull();
        await Should.ThrowAsync<MemoryProjectResolutionException>(() => resolver.ResolveAsync("ZZNOPE", null));
        await Should.ThrowAsync<MemoryProjectResolutionException>(() => resolver.ResolveAsync(null, Guid.NewGuid().ToString()));
        await Should.ThrowAsync<MemoryProjectResolutionException>(() => resolver.ResolveAsync(null, "not-a-session"));
    }

    [Fact]
    public async Task A_scoped_write_tags_its_entities_and_claims_and_project_reads_answer_only_that_projects_memory()
    {
        var project = await db.NewProjectAsync("MW");
        var writer = $"user:writer-{Guid.NewGuid():N}";
        var other = $"user:other-{Guid.NewGuid():N}";
        var tenant = new MemoryTenantContext();
        var store = new MySqlMemoryGraphStore(StorageOptions(), tenant);
        var ingestion = new MemoryClaimIngestionService(store, new NoEmbeddings(), NullLogger<MemoryClaimIngestionService>.Instance);

        using (tenant.Enter(writer))
        {
            await ingestion.NormalizeAndUpsertClaimsAsync(new MemoryClaimExtractionResult
            {
                Entities = [new MemoryExtractedEntity { Id = "codegraph", Label = "CodeGraph", Type = "repository" }],
                Claims = [new MemoryExtractedClaim { Id = "claim_codegraph_uses_mariadb", Subject = "codegraph", Predicate = "uses", Object = "mariadb" }],
                Evidence = [new MemoryExtractedEvidence { ClaimId = "claim_codegraph_uses_mariadb", EvidenceType = "note", SourceRef = "session" }],
            }, "test", project);
            // Unscoped: a write without a project, about a second repository sharing the MariaDB entity.
            await ingestion.NormalizeAndUpsertClaimsAsync(new MemoryClaimExtractionResult
            {
                Entities = [new MemoryExtractedEntity { Id = "sceneworks", Label = "SceneWorks", Type = "repository" }],
                Claims = [new MemoryExtractedClaim { Id = "claim_sceneworks_uses_mariadb", Subject = "sceneworks", Predicate = "uses", Object = "mariadb" }],
            }, "test");
        }

        using (tenant.Enter(other))
        {
            await ingestion.NormalizeAndUpsertClaimsAsync(new MemoryClaimExtractionResult
            {
                Entities = [new MemoryExtractedEntity { Id = "codegraph", Label = "CodeGraph", Type = "repository" }],
            }, "test");
        }

        (await ScopesAsync(writer)).ShouldBe(
        [
            $"claim:claim_codegraph_uses_mariadb@{project}:write",
            $"entity:codegraph@{project}:write",
            $"entity:mariadb@{project}:write",
        ]);
        (await ScopesAsync(other)).ShouldBeEmpty();

        using (tenant.Enter(writer))
        {
            var graph = await store.GetFullGraphAsync(projectKey: project);
            graph.Nodes.Select(node => node.Id).ShouldBe(["codegraph", "mariadb"], ignoreOrder: true);
            graph.TotalNodeCount.ShouldBe(2);
            graph.Links.ShouldHaveSingleItem().Source.ShouldBe("codegraph");
            (await store.GetFullGraphAsync()).Nodes.Count.ShouldBe(3);

            (await store.TextSearchAsync("SceneWorks", 5, project)).ShouldBeEmpty();
            (await store.TextSearchAsync("SceneWorks", 5)).ShouldHaveSingleItem();
            (await store.SearchClaimsAsync("uses mariadb", null, 5, projectKey: project))
                .Select(seed => seed.Claim.Id).ShouldBe(["claim_codegraph_uses_mariadb"]);
            (await store.SearchClaimsAsync("uses mariadb", null, 5)).Count.ShouldBe(2);

            // MariaDB is shared: in the project its bundle and graph carry only the project's claim and neighbor.
            (await store.GetEntityBundleAsync("sceneworks", projectKey: project)).ShouldBeNull();
            var bundle = (await store.GetEntityBundleAsync("mariadb", projectKey: project))!;
            bundle.ActiveClaims.Select(claim => claim.Id).ShouldBe(["claim_codegraph_uses_mariadb"]);
            bundle.NeighborEdges.ShouldHaveSingleItem().FromEntityId.ShouldBe("codegraph");
            (await store.GetEntityBundleAsync("mariadb"))!.ActiveClaims.Count.ShouldBe(2);
            (await store.GetEntityGraphAsync("mariadb", 200, project)).Nodes.Select(node => node.Id)
                .ShouldBe(["mariadb", "codegraph"], ignoreOrder: true);
            (await store.GetEntityGraphAsync("sceneworks", 200, project)).Nodes.ShouldBeEmpty();

            await store.UpsertEntitiesBatchAsync(
            [
                new MemoryEntity { Id = "codegraph", Label = "CodeGraph", Type = "repository", Summary = "", Source = "test", Embedding = [1f, 0f, 0f] },
                new MemoryEntity { Id = "sceneworks", Label = "SceneWorks", Type = "repository", Summary = "", Source = "test", Embedding = [1f, 0f, 0f] },
            ]);
            (await store.VectorSearchAsync([1f, 0f, 0f], 5, project)).ShouldHaveSingleItem().Entity.Id.ShouldBe("codegraph");

            var retrieval = new MemoryRetrievalService(store, new NoEmbeddings(), NullLogger<MemoryRetrievalService>.Instance);
            var hidden = await retrieval.SearchAsync("sceneworks", projectKey: project);
            hidden.Entities.ShouldBeEmpty();
            hidden.Claims.ShouldBeEmpty();
            (await retrieval.SearchAsync("sceneworks")).Entities.ShouldNotBeEmpty();
            (await retrieval.SearchAsync("codegraph", projectKey: project)).Entities.First().EntityId.ShouldBe("codegraph");
        }

        using (tenant.Enter(other))
            (await store.GetFullGraphAsync(projectKey: project)).Nodes.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_claim_bundle_honours_project_and_is_unchanged_without_one()
    {
        var project = await db.NewProjectAsync("MC");
        var user = $"user:bundle-{Guid.NewGuid():N}";
        var tenant = new MemoryTenantContext();
        var store = new MySqlMemoryGraphStore(StorageOptions(), tenant);
        using var _ = tenant.Enter(user);

        await store.UpsertEntitiesBatchAsync([Entity("repo", "Repo"), Entity("db", "Db"), Entity("other", "Other")]);
        // Claim in the project, with a fact-group peer, a conflict and a supersession peer outside it
        // and a conflict inside it.
        var inside = Claim("claim_inside", "repo", "db");
        var outsideConflict = Claim("claim_outside_conflict", "repo", "other");
        var outsideOlder = Claim("claim_outside_older", "repo", "other");
        var insideConflict = Claim("claim_inside_conflict", "repo", "db");
        var elsewhere = Claim("claim_elsewhere", "other", "db");
        inside.FactGroupKey = outsideConflict.FactGroupKey = "group_inside";
        await store.UpsertClaimsBatchAsync([inside, outsideConflict, outsideOlder, insideConflict, elsewhere]);
        await store.AddClaimEdgesBatchAsync(
        [
            Edge("claim_inside", "claim_outside_conflict", "conflicts_with"),
            Edge("claim_inside", "claim_inside_conflict", "conflicts_with"),
            Edge("claim_inside", "claim_outside_older", "supersedes"),
        ]);
        await store.AddEvidenceBatchAsync([Evidence("evidence_inside", "claim_inside", "note")]);
        await store.AddProjectScopeAsync(project, [], ["claim_inside", "claim_inside_conflict"]);

        // Without a project nothing is filtered.
        var unscoped = (await store.GetClaimBundleAsync("claim_inside"))!;
        unscoped.Conflicts.Select(claim => claim.Id).ShouldBe(["claim_outside_conflict", "claim_inside_conflict"], ignoreOrder: true);
        unscoped.SupersessionChain.Select(claim => claim.Id).ShouldBe(["claim_outside_older"]);
        unscoped.FactGroupPeers.Select(claim => claim.Id).ShouldBe(["claim_outside_conflict"]);
        (await store.GetClaimBundleAsync("claim_elsewhere"))!.Claim.Id.ShouldBe("claim_elsewhere");

        // In the project the claim keeps its evidence but drops the peers scoped elsewhere.
        var scoped = (await store.GetClaimBundleAsync("claim_inside", projectKey: project))!;
        scoped.Claim.Id.ShouldBe("claim_inside");
        scoped.Conflicts.Select(claim => claim.Id).ShouldBe(["claim_inside_conflict"]);
        scoped.SupersessionChain.ShouldBeEmpty();
        scoped.FactGroupPeers.ShouldBeEmpty();
        scoped.Evidence.Select(evidence => evidence.Id).ShouldBe(["evidence_inside"]);

        // A claim outside the project (or in another project) is not found in it.
        (await store.GetClaimBundleAsync("claim_elsewhere", projectKey: project)).ShouldBeNull();
        (await store.GetClaimBundleAsync("claim_inside", projectKey: "ZZNOPE")).ShouldBeNull();
        (await store.GetClaimBundleAsync("missing", projectKey: project)).ShouldBeNull();
    }

    [Fact]
    public async Task The_backfill_scopes_memory_naming_a_member_repository_as_a_whole_token_and_is_safe_to_replay()
    {
        var project = $"MB{Guid.NewGuid().ToString("N")[..6]}".ToUpperInvariant();
        await db.CallAsync(WorkTools.CreateWork, new
        {
            new_project = new
            {
                key = project,
                name = "Backfill",
                repositories = new[] { new { repository = "codegraph", role = "owned" }, new { repository = "mlx-rs", role = "dependency" } },
            },
        });
        var user = $"user:backfill-{Guid.NewGuid():N}";
        var tenant = new MemoryTenantContext();
        var store = new MySqlMemoryGraphStore(StorageOptions(), tenant);
        using (tenant.Enter(user))
        {
            await store.UpsertEntitiesBatchAsync(
            [
                Entity("codegraph", "CodeGraph"),
                Entity("mariadb", "MariaDB"),
                Entity("kanban", "Kanban"),
                Entity("board", "Board"),
                // Not CodeGraph: the repository name is only part of a token.
                Entity("codegraph_api", "CodeGraph.Api"),
                Entity("unrelated", "Unrelated"),
            ]);
            await store.UpsertClaimsBatchAsync(
            [
                Claim("claim_codegraph_uses_mariadb", "codegraph", "mariadb"),
                Claim("claim_kanban_tracks_board", "kanban", "board"),
                Claim("claim_unrelated_wraps_api", "unrelated", "codegraph_api"),
            ]);
            await store.AddEvidenceBatchAsync(
            [
                // A dependency repository in a URL path names the project.
                Evidence("evidence_kanban", "claim_kanban_tracks_board", "https://github.com/michaeltrefry/mlx-rs/pull/7"),
                // Neither names codegraph: "CodeGraph.Api" and "codegraph-web" are other tokens.
                Evidence("evidence_api", "claim_unrelated_wraps_api", "src/CodeGraph.Api/Startup.cs"),
                Evidence("evidence_web", "claim_unrelated_wraps_api", "codegraph-web/src/app.ts"),
            ]);
        }

        string[] expected =
        [
            $"claim:claim_codegraph_uses_mariadb@{project}:backfill",
            $"claim:claim_kanban_tracks_board@{project}:backfill",
            $"entity:board@{project}:backfill",
            $"entity:codegraph@{project}:backfill",
            $"entity:kanban@{project}:backfill",
            $"entity:mariadb@{project}:backfill",
        ];
        await RunBackfillAsync();
        (await ScopesAsync(user)).ShouldBe(expected);
        await RunBackfillAsync();
        (await ScopesAsync(user)).ShouldBe(expected);

        using (tenant.Enter(user))
            (await store.GetFullGraphAsync(projectKey: project)).Nodes.Select(node => node.Id)
                .ShouldBe(["codegraph", "mariadb", "kanban", "board"], ignoreOrder: true);
    }

    private IOptions<MariaDbStorageOptions> StorageOptions() =>
        Options.Create(new MariaDbStorageOptions { ConnectionString = db.ConnectionString });

    private async Task RunBackfillAsync()
    {
        var script = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "../../../../../sql/migrations/114_memory_project_scopes.sql"));
        await using var connection = new MySqlConnection(db.ConnectionString);
        foreach (var statement in MariaDbMigrationRunner.SplitStatements(script))
            await connection.ExecuteAsync(statement);
    }

    private async Task<string[]> ScopesAsync(string username)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        var rows = await connection.QueryAsync<string>(
            """
            SELECT CONCAT('entity:', entity.external_id, '@', scope.project_key, ':', scope.origin)
            FROM memory_entity_projects scope JOIN memory_entities_v2 entity ON entity.id = scope.entity_id
            WHERE entity.username = @username
            UNION ALL
            SELECT CONCAT('claim:', claim.external_id, '@', scope.project_key, ':', scope.origin)
            FROM memory_claim_projects scope JOIN memory_claims claim ON claim.id = scope.claim_id
            WHERE claim.username = @username
            """,
            new { username });
        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    private static MemoryEntity Entity(string id, string label) => new() { Id = id, Label = label, Type = "concept", Summary = "", Source = "test" };

    private static MemoryClaim Claim(string id, string subject, string obj) => new()
    {
        Id = id,
        ClaimKey = id,
        FactGroupKey = id,
        SubjectEntityId = subject,
        Predicate = "relates_to",
        ObjectEntityId = obj,
        NormalizedText = $"{subject} relates_to {obj}",
        Status = MemoryClaimStatus.Active,
        Source = "test",
    };

    private static MemoryClaimEdge Edge(string from, string to, string edgeType) => new()
    {
        FromClaimId = from,
        ToClaimId = to,
        EdgeType = edgeType,
        Source = "test",
    };

    private static MemoryEvidence Evidence(string id, string claimId, string sourceRef) => new()
    {
        Id = id,
        ClaimId = claimId,
        EvidenceType = "note",
        SourceRef = sourceRef,
    };

    private sealed class NoEmbeddings : IEmbeddingService
    {
        public bool IsAvailable => false;
        public string ModelName => "none";
        public int Dimensions => 0;
        public float[] GenerateEmbedding(string text) => [];
        public IReadOnlyList<float[]> GenerateEmbeddings(IReadOnlyList<string> texts) => [];
    }
}
