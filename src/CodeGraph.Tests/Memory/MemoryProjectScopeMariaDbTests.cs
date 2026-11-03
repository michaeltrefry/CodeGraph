using System.Text.Json;
using System.Threading.Channels;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Memory;
using CodeGraph.Models.Work;
using CodeGraph.Services.Embeddings;
using CodeGraph.Services.Memory;
using CodeGraph.Services.Work;
using CodeGraph.Tests.Work;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Memory;

/// <summary>
/// Project scoping for memory (CG-156) against a real MariaDB: a write is scoped to its session's
/// project (or an explicit one), migration 114 backfills memory that names a member repository as a
/// whole token, and graph, search, entity, subgraph, query, frontier and summary reads filtered by
/// project answer only that project's entities and claims, per user.
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
    public async Task A_write_naming_neither_project_nor_session_takes_the_project_all_the_callers_live_sessions_share()
    {
        var first = await db.NewProjectAsync("ML");
        var second = await db.NewProjectAsync("MN");
        var user = $"mem-{Guid.NewGuid():N}";
        var caller = new WorkActor(user);
        var resolver = new WorkMemoryProjectResolver(db.Store);

        // No live session: unscoped, and an unknown caller is never looked up.
        (await resolver.ResolveAsync(null, null, caller)).ShouldBeNull();
        (await resolver.ResolveAsync(null, null, null)).ShouldBeNull();

        var firstSession = await StartSessionAsync(first, user);
        (await resolver.ResolveAsync(null, null, caller)).ShouldBe(first);
        // Another identity's session (a person's, or an agent's of the same name) is not the caller's.
        (await resolver.ResolveAsync(null, null, new WorkActor($"other-{user}"))).ShouldBeNull();
        await StartSessionAsync(second, $"other-{user}");
        var agent = await db.AgentAsync(user);
        await db.CallAsAsync(WorkTools.StartSession, new { project = second, harness = "codex" }, agent);
        (await resolver.ResolveAsync(null, null, caller)).ShouldBe(first);
        (await resolver.ResolveAsync(null, null, agent)).ShouldBe(second);

        // A second live session in the same project still names it; one in another project makes it ambiguous: unscoped.
        var sameProjectSession = await StartSessionAsync(first, user);
        (await resolver.ResolveAsync(null, null, caller)).ShouldBe(first);
        await db.CallAsync(WorkTools.EndSession, new { session_id = sameProjectSession }, user);
        var secondSession = await StartSessionAsync(second, user);
        (await resolver.ResolveAsync(null, null, caller)).ShouldBeNull();

        // An explicit project or session wins over the inference, as before.
        (await resolver.ResolveAsync(second, null, caller)).ShouldBe(second);
        (await resolver.ResolveAsync(null, firstSession, caller)).ShouldBe(first);

        // A session gone silent past the idle threshold is not live; nor is an ended one.
        await using (var connection = new MySqlConnection(db.ConnectionString))
            await connection.ExecuteAsync("UPDATE work_sessions SET last_seen_at = @at WHERE id = @id",
                new { at = DateTime.UtcNow.AddSeconds(-WorkDatabase.IdleSeconds - 60), id = firstSession });
        (await resolver.ResolveAsync(null, null, caller)).ShouldBe(second);
        await db.CallAsync(WorkTools.EndSession, new { session_id = secondSession }, user);
        (await resolver.ResolveAsync(null, null, caller)).ShouldBeNull();
        (await resolver.ResolveAsync(null, secondSession, caller)).ShouldBe(second);
    }

    private async Task<string> StartSessionAsync(string project, string user) =>
        (await db.CallAsync(WorkTools.StartSession, new { project, harness = "claude-code" }, user))
            .GetProperty("session").GetProperty("id").GetString()!;

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
        await store.AddProjectScopeAsync(project, ["repo"], ["claim_inside", "claim_inside_conflict"]);
        // One observation is about the project's claim only; the other also about a claim scoped elsewhere.
        await store.CreateObservationAsync(Observation("obs_inside", ["claim_inside"]));
        await store.CreateObservationAsync(Observation("obs_mixed", ["claim_inside", "claim_outside_conflict"]));

        // Without a project nothing is filtered.
        var unscoped = (await store.GetClaimBundleAsync("claim_inside"))!;
        unscoped.Observations.Select(observation => observation.Id).ShouldBe(["obs_inside", "obs_mixed"], ignoreOrder: true);
        (await store.GetEntityBundleAsync("repo"))!.Observations.Select(observation => observation.Id)
            .ShouldBe(["obs_inside", "obs_mixed"], ignoreOrder: true);
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
        scoped.Observations.Select(observation => observation.Id).ShouldBe(["obs_inside"]);
        (await store.GetEntityBundleAsync("repo", projectKey: project))!.Observations.Select(observation => observation.Id)
            .ShouldBe(["obs_inside"]);

        // A claim outside the project (or in another project) is not found in it.
        (await store.GetClaimBundleAsync("claim_elsewhere", projectKey: project)).ShouldBeNull();
        (await store.GetClaimBundleAsync("claim_inside", projectKey: "ZZNOPE")).ShouldBeNull();
        (await store.GetClaimBundleAsync("missing", projectKey: project)).ShouldBeNull();
    }

    [Fact]
    public async Task Subgraph_query_frontier_and_summary_honour_project_per_user_and_are_unchanged_without_one()
    {
        var project = await db.NewProjectAsync("MS");
        var user = $"user:subgraph-{Guid.NewGuid():N}";
        var other = $"user:subgraph-other-{Guid.NewGuid():N}";
        var tenant = new MemoryTenantContext();
        var store = new MySqlMemoryGraphStore(StorageOptions(), tenant);
        var retrieval = new MemoryRetrievalService(store, new NoEmbeddings(), NullLogger<MemoryRetrievalService>.Instance);

        using (tenant.Enter(user))
        {
            await store.UpsertEntitiesBatchAsync([Entity("repo", "Repo"), Entity("db", "Db"), Entity("other", "Other")]);
            await store.UpsertClaimsBatchAsync(
            [
                Claim("claim_repo_db", "repo", "db"),
                Claim("claim_repo_other", "repo", "other"),
                Claim("claim_other_db", "other", "db"),
            ]);
            // repo and db are both in the project, but this relationship was asserted only outside it.
            var elsewhere = Claim("claim_repo_db_elsewhere", "repo", "db");
            elsewhere.Predicate = "deployed_with";
            await store.UpsertClaimsBatchAsync([elsewhere]);
            await store.UpsertEntityEdgesBatchAsync(
            [
                new MemoryEntityEdge { FromEntityId = "repo", ToEntityId = "db", EdgeType = "relates_to" },
                new MemoryEntityEdge { FromEntityId = "repo", ToEntityId = "other", EdgeType = "relates_to" },
                new MemoryEntityEdge { FromEntityId = "repo", ToEntityId = "db", EdgeType = "deployed_with", BestActiveClaimId = "claim_repo_db_elsewhere" },
            ]);
            // Scopes repo, db (the claim's subject and object) and claim_repo_db; other and its claims stay outside.
            await store.AddProjectScopeAsync(project, [], ["claim_repo_db"]);
            await store.CreateObservationAsync(Observation("obs_inside", ["claim_repo_db"]));
            await store.CreateObservationAsync(Observation("obs_mixed", ["claim_repo_db", "claim_repo_other"]));
        }

        using (tenant.Enter(other))
        {
            // Another user's memory in the same project, under the same entity ids.
            await store.UpsertEntitiesBatchAsync([Entity("repo", "Repo"), Entity("db", "Db")]);
            await store.UpsertClaimsBatchAsync([Claim("claim_theirs", "repo", "db")]);
            await store.AddProjectScopeAsync(project, [], ["claim_theirs"]);
        }

        using (tenant.Enter(user))
        {
            // Subgraph: without a project nothing is filtered.
            var unscoped = await retrieval.GetMemorySubgraphAsync(new MemorySubgraphRequest
            {
                SeedEntityIds = ["repo", "db", "other"],
                SeedClaimIds = ["claim_other_db"],
            });
            unscoped.Entities.Select(entity => entity.Entity.Id).ShouldBe(["repo", "db", "other"], ignoreOrder: true);
            unscoped.Claims.Select(claim => claim.Claim.Id)
                .ShouldBe(["claim_repo_db", "claim_repo_db_elsewhere", "claim_repo_other", "claim_other_db"], ignoreOrder: true);
            unscoped.EntityEdges.Select(edge => $"{edge.ToEntityId}:{edge.EdgeType}").ShouldBe(["db:relates_to", "other:relates_to", "db:deployed_with"], ignoreOrder: true);
            unscoped.Observations.Select(observation => observation.Id).ShouldBe(["obs_inside", "obs_mixed"], ignoreOrder: true);

            // In the project: seeds outside it are dropped, as are claims, edges and observations reaching outside it.
            var scoped = await retrieval.GetMemorySubgraphAsync(new MemorySubgraphRequest
            {
                SeedEntityIds = ["repo", "db", "other"],
                SeedClaimIds = ["claim_other_db"],
                ProjectKey = project,
            });
            scoped.Entities.Select(entity => entity.Entity.Id).ShouldBe(["repo", "db"], ignoreOrder: true);
            scoped.Claims.Select(claim => claim.Claim.Id).ShouldBe(["claim_repo_db"]);
            // Only the edge the project's own claim supports, with that claim as its best one.
            var edge = scoped.EntityEdges.ShouldHaveSingleItem();
            (edge.ToEntityId, edge.EdgeType, edge.BestActiveClaimId).ShouldBe(("db", "relates_to", "claim_repo_db"));
            scoped.Observations.Select(observation => observation.Id).ShouldBe(["obs_inside"]);
            (await retrieval.GetMemorySubgraphAsync(new MemorySubgraphRequest { SeedEntityIds = ["repo"], ProjectKey = "ZZNOPE" }))
                .Entities.ShouldBeEmpty();

            // Query (topic search then subgraph).
            var queried = await retrieval.QueryAsync("Repo", maxNodes: 10);
            queried.Subgraph!.Claims.Select(claim => claim.Claim.Id).ShouldContain("claim_repo_other");
            var scopedQuery = await retrieval.QueryAsync("Repo", maxNodes: 10, projectKey: project);
            scopedQuery.Subgraph!.Entities.Select(entity => entity.Entity.Id).ShouldContain("repo");
            scopedQuery.Subgraph.Entities.Select(entity => entity.Entity.Id).ShouldBeSubsetOf(["repo", "db"]);
            scopedQuery.Subgraph.Claims.Select(claim => claim.Claim.Id).ShouldBe(["claim_repo_db"]);
            scopedQuery.Conflicts.Select(observation => observation.Id).ShouldBe(["obs_inside"]);
            (await retrieval.QueryAsync("Other", projectKey: project)).FormattedText.ShouldBe("No relevant memories found.");
            (await retrieval.QueryAsync("Other")).Subgraph!.Entities.Select(entity => entity.Entity.Id).ShouldContain("other");

            // Frontier expansion.
            (await retrieval.ExpandMemoryFrontierAsync(new MemoryFrontierExpansionRequest { FrontierEntityIds = ["repo"] }))
                .AddedClaims.Select(claim => claim.Claim.Id).ShouldBe(["claim_repo_db", "claim_repo_db_elsewhere", "claim_repo_other"], ignoreOrder: true);
            (await retrieval.ExpandMemoryFrontierAsync(new MemoryFrontierExpansionRequest { FrontierEntityIds = ["repo"], ProjectKey = project }))
                .AddedClaims.Select(claim => claim.Claim.Id).ShouldBe(["claim_repo_db"]);
            var outside = await retrieval.ExpandMemoryFrontierAsync(new MemoryFrontierExpansionRequest
            {
                FrontierClaimIds = ["claim_repo_other"],
                ProjectKey = project,
            });
            outside.AddedEntities.ShouldBeEmpty();
            outside.AddedClaims.ShouldBeEmpty();

            // Render summary.
            var summary = await retrieval.RenderMemorySummaryAsync(new MemorySummaryRenderRequest { EntityIds = ["repo"], Style = "plain" });
            summary.Text.ShouldContain("repo relates_to other");
            var scopedSummary = await retrieval.RenderMemorySummaryAsync(new MemorySummaryRenderRequest
            {
                EntityIds = ["repo"],
                Style = "plain",
                ProjectKey = project,
            });
            scopedSummary.Text.ShouldContain("repo relates_to db");
            scopedSummary.Text.ShouldNotContain("repo relates_to other");
        }

        using (tenant.Enter(other))
        {
            var theirs = await retrieval.GetMemorySubgraphAsync(new MemorySubgraphRequest
            {
                SeedEntityIds = ["repo", "db", "other"],
                ProjectKey = project,
            });
            theirs.Entities.Select(entity => entity.Entity.Id).ShouldBe(["repo", "db"], ignoreOrder: true);
            theirs.Claims.Select(claim => claim.Claim.Id).ShouldBe(["claim_theirs"]);
            theirs.Observations.ShouldBeEmpty();
        }
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

    private Task<string[]> ScopesAsync(string username) => ScopesAsync(db.ConnectionString, username);

    /// <summary>Every project scope of <paramref name="username"/>'s memory, as kind:id@project:origin, sorted.</summary>
    internal static async Task<string[]> ScopesAsync(string connectionString, string username)
    {
        await using var connection = new MySqlConnection(connectionString);
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

    internal static MemoryEntity Entity(string id, string label) => new() { Id = id, Label = label, Type = "concept", Summary = "", Source = "test" };

    internal static MemoryClaim Claim(string id, string subject, string obj) => new()
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

    internal static MemoryEvidence Evidence(string id, string claimId, string sourceRef) => new()
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

    private static MemoryObservation Observation(string id, List<string> claimIds) => new()
    {
        Id = id,
        Claim = "the first statement",
        ConflictsWith = "the second statement",
        Source = "test",
        AboutEntityIds = ["repo"],
        AboutClaimIds = claimIds,
    };
}

/// <summary>
/// The project-scope backfill run by the work service (CG-167), against its own database: the
/// migration-114 replays of <see cref="MemoryProjectScopeMariaDbTests"/> scope every user's memory to
/// every project, so a project naming codegraph there would leak into these expectations and back.
/// </summary>
public sealed class MemoryProjectScopeBackfillMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    [Fact]
    public async Task Creating_a_project_or_adding_a_repository_scopes_every_users_memory_naming_it_and_a_replay_changes_nothing()
    {
        var key = $"BF{Guid.NewGuid().ToString("N")[..6]}".ToUpperInvariant();
        var alice = $"user:alice-{Guid.NewGuid():N}";
        var bob = $"user:bob-{Guid.NewGuid():N}";
        var options = Options.Create(new MariaDbStorageOptions { ConnectionString = db.ConnectionString });
        var tenant = new MemoryTenantContext();
        var store = new MySqlMemoryGraphStore(options, tenant);
        await using (var connection = new MySqlConnection(db.ConnectionString))
            await connection.ExecuteAsync("INSERT IGNORE INTO repositories (name) VALUES ('ab')");

        using (tenant.Enter(alice))
        {
            await store.UpsertEntitiesBatchAsync(
            [
                MemoryProjectScopeMariaDbTests.Entity("codegraph", "CodeGraph"),
                MemoryProjectScopeMariaDbTests.Entity("mariadb", "MariaDB"),
                MemoryProjectScopeMariaDbTests.Entity("codegraph_api", "CodeGraph.Api"),
                MemoryProjectScopeMariaDbTests.Entity("unrelated", "Unrelated"),
                MemoryProjectScopeMariaDbTests.Entity("kanban", "Kanban"),
                MemoryProjectScopeMariaDbTests.Entity("board", "Board"),
                MemoryProjectScopeMariaDbTests.Entity("ab", "AB"),
                MemoryProjectScopeMariaDbTests.Entity("notes", "Notes"),
            ]);
            await store.UpsertClaimsBatchAsync(
            [
                MemoryProjectScopeMariaDbTests.Claim("claim_codegraph_uses_mariadb", "codegraph", "mariadb"),
                MemoryProjectScopeMariaDbTests.Claim("claim_unrelated_wraps_api", "unrelated", "codegraph_api"),
                MemoryProjectScopeMariaDbTests.Claim("claim_kanban_tracks_board", "kanban", "board"),
                MemoryProjectScopeMariaDbTests.Claim("claim_ab_has_notes", "ab", "notes"),
            ]);
            await store.AddEvidenceBatchAsync(
            [
                // Neither names codegraph: "CodeGraph.Api" and "codegraph-web" are other tokens.
                MemoryProjectScopeMariaDbTests.Evidence("evidence_api", "claim_unrelated_wraps_api", "src/CodeGraph.Api/Startup.cs"),
                MemoryProjectScopeMariaDbTests.Evidence("evidence_web", "claim_unrelated_wraps_api", "codegraph-web/src/app.ts"),
                MemoryProjectScopeMariaDbTests.Evidence("evidence_kanban", "claim_kanban_tracks_board", "https://github.com/michaeltrefry/mlx-rs/pull/7"),
                // A repository name shorter than three characters is never matched.
                MemoryProjectScopeMariaDbTests.Evidence("evidence_ab", "claim_ab_has_notes", "ab/README.md"),
            ]);
            // Scoped by a write before the project's repositories were known: it stays a write.
            await store.AddProjectScopeAsync(key, ["mariadb"], []);
        }

        using (tenant.Enter(bob))
            await store.UpsertEntitiesBatchAsync([MemoryProjectScopeMariaDbTests.Entity("codegraph", "CodeGraph")]);

        // The real queue and worker, draining into the MariaDB backfill; the test awaits each run.
        var queue = new MemoryScopeBackfillQueue(NullLogger<MemoryScopeBackfillQueue>.Instance);
        var backfill = new SignallingBackfill(new MySqlMemoryProjectScopeBackfill(options));
        await using var provider = new ServiceCollection().AddSingleton<IMemoryProjectScopeBackfill>(backfill).BuildServiceProvider();
        using var worker = new MemoryScopeBackfillWorker(queue, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MemoryScopeBackfillWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);

        var work = new WorkService(db.Store, db.Clock, Options.Create(new WorkSessionOptions { IdleSeconds = WorkDatabase.IdleSeconds }),
            memoryScopeBackfillQueue: queue);
        await CallAsync(work, WorkTools.CreateWork, new
        {
            new_project = new { key, name = "Backfill on create", repositories = new[] { new { repository = "codegraph", role = "dependency" } } },
        });
        (await backfill.NextRunAsync()).ShouldBe((key, "codegraph"));

        string[] afterCreate =
        [
            $"claim:claim_codegraph_uses_mariadb@{key}:backfill",
            $"entity:codegraph@{key}:backfill",
            $"entity:mariadb@{key}:write",
        ];
        (await MemoryProjectScopeMariaDbTests.ScopesAsync(db.ConnectionString, alice)).ShouldBe(afterCreate);
        (await MemoryProjectScopeMariaDbTests.ScopesAsync(db.ConnectionString, bob)).ShouldBe([$"entity:codegraph@{key}:backfill"]);

        await new MySqlMemoryProjectScopeBackfill(options).BackfillAsync(key, ["codegraph"]);
        (await MemoryProjectScopeMariaDbTests.ScopesAsync(db.ConnectionString, alice)).ShouldBe(afterCreate);

        var version = (await CallAsync(work, WorkTools.GetWork, new { id = key })).GetProperty("version").GetInt64();
        await CallAsync(work, WorkTools.UpdateWork, new
        {
            id = key,
            expected_version = version,
            repositories = new[]
            {
                new { repository = "codegraph", role = "dependency" },
                new { repository = "mlx-rs", role = "dependency" },
                new { repository = "ab", role = "dependency" },
            },
        });
        // Only the repositories the update added are backfilled.
        (await backfill.NextRunAsync()).ShouldBe((key, "ab,mlx-rs"));

        string[] afterAdd =
        [
            $"claim:claim_codegraph_uses_mariadb@{key}:backfill",
            $"claim:claim_kanban_tracks_board@{key}:backfill",
            $"entity:board@{key}:backfill",
            $"entity:codegraph@{key}:backfill",
            $"entity:kanban@{key}:backfill",
            $"entity:mariadb@{key}:write",
        ];
        (await MemoryProjectScopeMariaDbTests.ScopesAsync(db.ConnectionString, alice)).ShouldBe(afterAdd);
        await new MySqlMemoryProjectScopeBackfill(options).BackfillAsync(key, ["codegraph", "mlx-rs", "ab"]);
        (await MemoryProjectScopeMariaDbTests.ScopesAsync(db.ConnectionString, alice)).ShouldBe(afterAdd);
        (await MemoryProjectScopeMariaDbTests.ScopesAsync(db.ConnectionString, bob)).ShouldBe([$"entity:codegraph@{key}:backfill"]);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Only_a_created_project_or_the_repositories_an_update_added_are_queued_for_a_backfill_and_the_write_never_waits_for_it()
    {
        var key = $"BX{Guid.NewGuid().ToString("N")[..6]}".ToUpperInvariant();
        var repository = $"repo-{key.ToLowerInvariant()}";
        await using (var connection = new MySqlConnection(db.ConnectionString))
            await connection.ExecuteAsync("INSERT IGNORE INTO repositories (name) VALUES (@repository), ('mlx-rs')", new { repository });
        // A queue that never runs a backfill: every project write still stands without one.
        var queue = new RecordingQueue();
        var work = new WorkService(db.Store, db.Clock, Options.Create(new WorkSessionOptions { IdleSeconds = WorkDatabase.IdleSeconds }),
            memoryScopeBackfillQueue: queue);

        var create = new
        {
            new_project = new { key, name = "Backfill fails", repositories = new[] { new { repository, role = "owned" } } },
            idempotency_key = $"create-{key}",
        };
        var created = await CallAsync(work, WorkTools.CreateWork, create);
        created.GetProperty("created").GetBoolean().ShouldBeTrue();
        // An idempotent replay creates nothing and queues nothing.
        (await CallAsync(work, WorkTools.CreateWork, create)).GetProperty("created").GetBoolean().ShouldBeFalse();
        queue.Enqueued.ShouldBe([$"{key}:{repository}"]);

        var version = (await CallAsync(work, WorkTools.GetWork, new { id = key })).GetProperty("version").GetInt64();
        var updated = await CallAsync(work, WorkTools.UpdateWork, new
        {
            id = key,
            expected_version = version,
            repositories = new[] { new { repository, role = "owned" }, new { repository = "mlx-rs", role = "dependency" } },
        });
        updated.GetProperty("project").GetProperty("repositories").GetArrayLength().ShouldBe(2);
        queue.Enqueued.ShouldBe([$"{key}:{repository}", $"{key}:mlx-rs"]);

        // An update that adds no repository queues nothing.
        version = updated.GetProperty("project").GetProperty("version").GetInt64();
        await CallAsync(work, WorkTools.UpdateWork, new
        {
            id = key,
            expected_version = version,
            repositories = new[] { new { repository = "mlx-rs", role = "dependency" }, new { repository, role = "owned" } },
        });
        queue.Enqueued.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_daily_reconciliation_lists_every_project_with_repositories_and_scopes_one_whose_backfill_failed()
    {
        var key = $"RC{Guid.NewGuid().ToString("N")[..6]}".ToUpperInvariant();
        var empty = $"RE{Guid.NewGuid().ToString("N")[..6]}".ToUpperInvariant();
        var owned = key.ToLowerInvariant();
        var dependency = $"{owned}dep";
        var carol = $"user:carol-{Guid.NewGuid():N}";
        await using (var connection = new MySqlConnection(db.ConnectionString))
            await connection.ExecuteAsync("INSERT IGNORE INTO repositories (name) VALUES (@owned), (@dependency)", new { owned, dependency });
        var options = Options.Create(new MariaDbStorageOptions { ConnectionString = db.ConnectionString });
        var tenant = new MemoryTenantContext();
        using (tenant.Enter(carol))
            await new MySqlMemoryGraphStore(options, tenant).UpsertEntitiesBatchAsync([MemoryProjectScopeMariaDbTests.Entity(owned, owned)]);

        // The backfill queued on create never runs (the queue was full, or it failed): the project misses its scopes.
        var work = new WorkService(db.Store, db.Clock, Options.Create(new WorkSessionOptions { IdleSeconds = WorkDatabase.IdleSeconds }),
            memoryScopeBackfillQueue: new RecordingQueue());
        await CallAsync(work, WorkTools.CreateWork, new
        {
            new_project = new { key, name = "Reconciled", repositories = new[] { new { repository = owned, role = "owned" }, new { repository = dependency, role = "dependency" } } },
        });
        await CallAsync(work, WorkTools.CreateWork, new { new_project = new { key = empty, name = "No repositories" } });
        (await MemoryProjectScopeMariaDbTests.ScopesAsync(db.ConnectionString, carol)).ShouldBeEmpty();

        var listed = await db.Store.ListProjectRepositoriesAsync();
        listed.Single(project => project.ProjectKey == key).Repositories.ShouldBe([owned, dependency]);
        listed.ShouldNotContain(project => project.ProjectKey == empty);

        var result = await new CodeGraph.Jobs.Jobs.ReconcileMemoryProjectScopesJob(db.Store, new MySqlMemoryScopeReconciliationStore(options),
            new MySqlMemoryProjectScopeBackfill(options), NullLogger<CodeGraph.Jobs.Jobs.ReconcileMemoryProjectScopesJob>.Instance)
            .ExecuteAsync(new CodeGraph.Jobs.Jobs.EmptyJobRequest());
        result.Success.ShouldBeTrue(result.Message);
        (await MemoryProjectScopeMariaDbTests.ScopesAsync(db.ConnectionString, carol)).ShouldBe([$"entity:{owned}@{key}:backfill"]);
    }

    /// <summary>A queue that accepts and keeps every backfill, as "project:repository,…", and never runs one.</summary>
    private sealed class RecordingQueue : IMemoryScopeBackfillQueue
    {
        public List<string> Enqueued { get; } = [];

        public bool Enqueue(string projectKey, IReadOnlyCollection<string> repositories)
        {
            Enqueued.Add($"{projectKey}:{string.Join(",", repositories)}");
            return true;
        }
    }

    /// <summary>Runs the backfill and then reports the run, as (project, sorted repositories).</summary>
    private sealed class SignallingBackfill(IMemoryProjectScopeBackfill inner) : IMemoryProjectScopeBackfill
    {
        private readonly Channel<(string, string)> runs = Channel.CreateUnbounded<(string, string)>();

        public async Task BackfillAsync(string projectKey, IReadOnlyCollection<string> repositories, CancellationToken ct = default)
        {
            try
            {
                await inner.BackfillAsync(projectKey, repositories, ct);
            }
            finally
            {
                runs.Writer.TryWrite((projectKey, string.Join(",", repositories.Order(StringComparer.Ordinal))));
            }
        }

        public async Task<(string, string)> NextRunAsync() => await runs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));

        public Task<MemoryScopeWatermark> GetWatermarkAsync(MemoryScopeWatermark? since, CancellationToken ct = default) => inner.GetWatermarkAsync(since, ct);

        public Task BackfillRangeAsync(IReadOnlyList<ProjectRepositories> projects, MemoryScopeWatermark after, MemoryScopeWatermark upTo, CancellationToken ct = default) =>
            inner.BackfillRangeAsync(projects, after, upTo, ct);
    }

    internal static async Task<JsonElement> CallAsync(WorkService work, string tool, object arguments)
    {
        var result = await work.ExecuteAsync(tool, JsonSerializer.SerializeToElement(arguments, WorkJson.Options), new WorkActor("alice"));
        return JsonSerializer.SerializeToElement(result, result.GetType(), WorkJson.Options);
    }

    /// <summary>A queue that accepts every backfill and never runs one: only the pending rows the project writes recorded remain.</summary>
    internal sealed class NeverRunQueue : IMemoryScopeBackfillQueue
    {
        public bool Enqueue(string projectKey, IReadOnlyCollection<string> repositories) => true;
    }
}

/// <summary>
/// The incremental memory scope reconciliation (CG-172) against its own database, whose watermark and
/// pending rows only this class touches: a project write records its added repositories as pending,
/// a failed backfill stays pending until a run repairs it, a run after no change scans nothing, and
/// memory written after the watermark is scoped for every project without rescanning older memory.
/// </summary>
public sealed class MemoryScopeReconciliationMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    [Fact]
    public async Task Failed_backfills_are_repaired_a_run_without_change_scans_nothing_and_only_new_memory_is_scanned()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var first = $"RA{suffix}".ToUpperInvariant();
        var second = $"RB{suffix}".ToUpperInvariant();
        string repoA = $"ra{suffix}", repoB = $"rb{suffix}", repoC = $"rc{suffix}";
        var dave = $"user:dave-{Guid.NewGuid():N}";
        var options = Options.Create(new MariaDbStorageOptions { ConnectionString = db.ConnectionString });
        var state = new MySqlMemoryScopeReconciliationStore(options);
        var tenant = new MemoryTenantContext();
        var memory = new MySqlMemoryGraphStore(options, tenant);
        await using (var connection = new MySqlConnection(db.ConnectionString))
            await connection.ExecuteAsync("INSERT IGNORE INTO repositories (name) VALUES (@repoA), (@repoB), (@repoC)", new { repoA, repoB, repoC });

        // Memory written before any reconciliation: it names repoA and repoC.
        using (tenant.Enter(dave))
            await memory.UpsertEntitiesBatchAsync([MemoryProjectScopeMariaDbTests.Entity("old_a", repoA), MemoryProjectScopeMariaDbTests.Entity("old_c", repoC)]);

        // The project writes record their added repositories as pending, coalesced per project; the queue never runs them.
        var work = new WorkService(db.Store, db.Clock, Options.Create(new WorkSessionOptions { IdleSeconds = WorkDatabase.IdleSeconds }),
            memoryScopeBackfillQueue: new MemoryProjectScopeBackfillMariaDbTests.NeverRunQueue());
        await MemoryProjectScopeBackfillMariaDbTests.CallAsync(work, WorkTools.CreateWork, new
        {
            new_project = new { key = first, name = "Reconciled", repositories = new[] { new { repository = repoA, role = "owned" } } },
        });
        var version = (await MemoryProjectScopeBackfillMariaDbTests.CallAsync(work, WorkTools.GetWork, new { id = first })).GetProperty("version").GetInt64();
        await MemoryProjectScopeBackfillMariaDbTests.CallAsync(work, WorkTools.UpdateWork, new
        {
            id = first,
            expected_version = version,
            repositories = new[] { new { repository = repoA, role = "owned" }, new { repository = repoB, role = "dependency" } },
        });
        await MemoryProjectScopeBackfillMariaDbTests.CallAsync(work, WorkTools.CreateWork, new
        {
            new_project = new { key = second, name = "Already scoped", repositories = new[] { new { repository = repoC, role = "owned" } } },
        });
        var pending = await state.ListPendingAsync();
        pending.Single(row => row.ProjectKey == first).Repositories.ShouldBe([repoA, repoB]);
        pending.Single(row => row.ProjectKey == second).Repositories.ShouldBe([repoC]);
        // Taken as already backfilled, so only a scan of all memory could scope old_c to the second project.
        await state.CompletePendingAsync(second, [repoC]);
        (await state.ListPendingAsync()).Select(row => row.ProjectKey).ShouldBe([first]);

        // Rows count as settled: the backfill's clock is past the settle time of everything written here.
        var clock = new ShiftableClock();
        clock.Advance(MySqlMemoryProjectScopeBackfill.SettleTime + TimeSpan.FromMinutes(1));
        var real = new MySqlMemoryProjectScopeBackfill(options, clock);

        // 1. The first run records the watermark without a scan; the first project's backfill fails and stays pending.
        var failing = new CountingBackfill(real) { FailingProject = first };
        var result = await RunAsync(state, failing);
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(first);
        failing.RangeScans.ShouldBe(0);
        var watermark = (await state.GetWatermarkAsync()).ShouldNotBeNull();
        watermark.EntityId.ShouldBeGreaterThan(0);
        var failed = (await state.ListPendingAsync()).ShouldHaveSingleItem();
        failed.Attempts.ShouldBe(1);
        failed.LastError.ShouldBe("memory host unreachable");
        (await MemoryProjectScopeMariaDbTests.ScopesAsync(db.ConnectionString, dave)).ShouldBeEmpty();

        // 2. The next run repairs it: a full backfill of its pending repositories, and no new memory to scan.
        var counting = new CountingBackfill(real);
        (result = await RunAsync(state, counting)).Success.ShouldBeTrue(result.Message);
        counting.FullScans.ShouldBe([$"{first}:{repoA},{repoB}"]);
        counting.RangeScans.ShouldBe(0);
        (await state.ListPendingAsync()).ShouldBeEmpty();
        (await MemoryProjectScopeMariaDbTests.ScopesAsync(db.ConnectionString, dave)).ShouldBe([$"entity:old_a@{first}:backfill"]);

        // 3. Nothing changed: no scan, and the watermark stays.
        counting = new CountingBackfill(real);
        (result = await RunAsync(state, counting)).Success.ShouldBeTrue(result.Message);
        counting.FullScans.ShouldBeEmpty();
        counting.RangeScans.ShouldBe(0);
        (await state.GetWatermarkAsync()).ShouldBe(watermark);

        // 4. New memory: an entity naming repoC, and a claim with evidence naming repoB.
        using (tenant.Enter(dave))
        {
            await memory.UpsertEntitiesBatchAsync(
            [
                MemoryProjectScopeMariaDbTests.Entity("new_c", repoC),
                MemoryProjectScopeMariaDbTests.Entity("subject", "Subject"),
                MemoryProjectScopeMariaDbTests.Entity("object", "Object"),
            ]);
            await memory.UpsertClaimsBatchAsync(
            [
                MemoryProjectScopeMariaDbTests.Claim("claim_new", "subject", "object"),
                // A new claim about an old entity whose label names repoA.
                MemoryProjectScopeMariaDbTests.Claim("claim_about_a", "old_a", "object"),
            ]);
            await memory.AddEvidenceBatchAsync([MemoryProjectScopeMariaDbTests.Evidence("evidence_new", "claim_new", $"https://github.com/x/{repoB}/pull/1")]);
        }
        counting = new CountingBackfill(real);
        (result = await RunAsync(state, counting)).Success.ShouldBeTrue(result.Message);
        counting.FullScans.ShouldBeEmpty();
        counting.RangeScans.ShouldBe(1);
        string[] incremental =
        [
            $"claim:claim_about_a@{first}:backfill",
            $"claim:claim_new@{first}:backfill",
            $"entity:new_c@{second}:backfill",
            $"entity:object@{first}:backfill",
            $"entity:old_a@{first}:backfill",
            $"entity:subject@{first}:backfill",
        ];
        // old_c names repoC too, but it was written before the watermark: it is not rescanned.
        (await MemoryProjectScopeMariaDbTests.ScopesAsync(db.ConnectionString, dave)).ShouldBe(incremental);
        var advanced = (await state.GetWatermarkAsync()).ShouldNotBeNull();
        advanced.EntityId.ShouldBeGreaterThan(watermark.EntityId);
        advanced.ClaimId.ShouldBeGreaterThan(watermark.ClaimId);
        advanced.EvidenceId.ShouldBeGreaterThan(watermark.EvidenceId);

        // 5. And again nothing changed.
        counting = new CountingBackfill(real);
        (result = await RunAsync(state, counting)).Success.ShouldBeTrue(result.Message);
        counting.RangeScans.ShouldBe(0);
        (await state.GetWatermarkAsync()).ShouldBe(advanced);

        // A row written within the settle time does not move the watermark yet.
        using (tenant.Enter(dave))
            await memory.UpsertEntitiesBatchAsync([MemoryProjectScopeMariaDbTests.Entity("fresh", repoC)]);
        (await new MySqlMemoryProjectScopeBackfill(options).GetWatermarkAsync(advanced)).ShouldBe(advanced);
        (await real.GetWatermarkAsync(advanced)).EntityId.ShouldBeGreaterThan(advanced.EntityId);

        // The range backfill found what a full backfill finds in the new memory: a full one adds only the
        // old row deliberately left out above (and the fresh row, not yet passed by the watermark).
        await real.BackfillAsync(first, [repoA, repoB]);
        await real.BackfillAsync(second, [repoC]);
        (await MemoryProjectScopeMariaDbTests.ScopesAsync(db.ConnectionString, dave)).ShouldBe(
            incremental.Append($"entity:old_c@{second}:backfill").Append($"entity:fresh@{second}:backfill").Order(StringComparer.Ordinal).ToArray());
    }

    private Task<CodeGraph.Jobs.JobExecutionResult> RunAsync(IMemoryScopeReconciliationStore state, IMemoryProjectScopeBackfill backfill) =>
        new CodeGraph.Jobs.Jobs.ReconcileMemoryProjectScopesJob(db.Store, state, backfill,
            NullLogger<CodeGraph.Jobs.Jobs.ReconcileMemoryProjectScopesJob>.Instance).ExecuteAsync(new CodeGraph.Jobs.Jobs.EmptyJobRequest());

    /// <summary>Counts the scans: full backfills (as project:sorted repositories) and range backfills over a non-empty range.</summary>
    private sealed class CountingBackfill(IMemoryProjectScopeBackfill inner) : IMemoryProjectScopeBackfill
    {
        public string? FailingProject { get; init; }
        public List<string> FullScans { get; } = [];
        public int RangeScans { get; private set; }

        public Task BackfillAsync(string projectKey, IReadOnlyCollection<string> repositories, CancellationToken ct = default)
        {
            FullScans.Add($"{projectKey}:{string.Join(",", repositories.Order(StringComparer.Ordinal))}");
            return projectKey == FailingProject
                ? Task.FromException(new HttpRequestException("memory host unreachable"))
                : inner.BackfillAsync(projectKey, repositories, ct);
        }

        public Task<MemoryScopeWatermark> GetWatermarkAsync(MemoryScopeWatermark? since, CancellationToken ct = default) => inner.GetWatermarkAsync(since, ct);

        public Task BackfillRangeAsync(IReadOnlyList<ProjectRepositories> projects, MemoryScopeWatermark after, MemoryScopeWatermark upTo, CancellationToken ct = default)
        {
            if (upTo != after)
                RangeScans++;
            return inner.BackfillRangeAsync(projects, after, upTo, ct);
        }
    }
}
