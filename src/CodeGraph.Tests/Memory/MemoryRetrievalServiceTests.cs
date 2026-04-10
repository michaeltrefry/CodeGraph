using CodeGraph.Data;
using Shouldly;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Embeddings;
using CodeGraph.Services.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeGraph.Tests.Memory;

public class MemoryRetrievalServiceTests
{
    [Fact]
    public void CosineSimilarity_IdenticalVectors_Returns1()
    {
        var v = new float[] { 1, 0, 0 };
        MemoryRetrievalService.CosineSimilarity(v, v).ShouldBe(1.0, tolerance: 0.0001);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_Returns0()
    {
        var a = new float[] { 1, 0, 0 };
        var b = new float[] { 0, 1, 0 };
        MemoryRetrievalService.CosineSimilarity(a, b).ShouldBe(0.0, tolerance: 0.0001);
    }

    [Fact]
    public void CosineSimilarity_DifferentLengths_Returns0()
    {
        var a = new float[] { 1, 0 };
        var b = new float[] { 1, 0, 0 };
        MemoryRetrievalService.CosineSimilarity(a, b).ShouldBe(0.0);
    }

    [Fact]
    public void CosineSimilarity_ZeroVector_Returns0()
    {
        var a = new float[] { 0, 0, 0 };
        var b = new float[] { 1, 0, 0 };
        MemoryRetrievalService.CosineSimilarity(a, b).ShouldBe(0.0);
    }

    [Fact]
    public void NormalizeSearchText_StripsPunctuationAndCollapsesWhitespace()
    {
        var result = MemoryRetrievalService.NormalizeSearchText(" Michael-Smoke (Test) ");
        result.ShouldBe("michael smoke test");
    }

    [Fact]
    public void PassesLexicalEntityFilter_RequiresAllTokensForMultiWordQueries()
    {
        var entity = new MemoryEntity
        {
            Id = "michael_user",
            Label = "Michael",
            Type = "person",
            Summary = "",
            Source = "test",
        };

        var matches = MemoryRetrievalService.PassesLexicalEntityFilter(
            entity,
            MemoryRetrievalService.NormalizeSearchText("Michael Smoke"),
            MemoryRetrievalService.TokenizeSearchText("Michael Smoke"));

        matches.ShouldBeFalse();
    }

    [Fact]
    public void FormatForLlm_EmptyEntities_ReturnsHeader()
    {
        var result = MemoryRetrievalService.FormatForLlm([], [], null);
        result.ShouldContain("## Relevant Memory");
    }

    [Fact]
    public void FormatForLlm_WithEntities_FormatsCorrectly()
    {
        var entities = new List<MemoryEntityWithRelationships>
        {
            new()
            {
                Entity = new MemoryEntity
                {
                    Id = "test_entity",
                    Label = "Test Entity",
                    Type = "concept",
                    Summary = "A test concept",
                    Source = "test",
                },
                VectorScore = 0.9,
                Relationships =
                [
                    new MemoryRelationshipDetail
                    {
                        Direction = "outgoing",
                        RelationshipType = "uses",
                        TargetLabel = "Other",
                        TargetId = "other",
                        Context = "for testing",
                        Timestamp = DateTime.UtcNow,
                    }
                ],
            }
        };

        var result = MemoryRetrievalService.FormatForLlm(entities, [], null);
        result.ShouldContain("**Test Entity** (concept) — A test concept");
        result.ShouldContain("uses → Other: for testing");
    }

    [Fact]
    public void FormatForLlm_WithConflicts_IncludesWarnings()
    {
        var conflicts = new List<MemoryObservation>
        {
            new()
            {
                Id = "obs_1",
                Claim = "X uses Y",
                ConflictsWith = "previous knowledge",
                Source = "test",
                Timestamp = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            }
        };

        var result = MemoryRetrievalService.FormatForLlm([], conflicts, null);
        result.ShouldContain("CONFLICT (unresolved)");
        result.ShouldContain("X uses Y");
    }

    [Fact]
    public void BuildDirectSeedPaths_ReturnsPathsForDirectEntityAndClaimSeeds()
    {
        var result = new MemorySubgraphResult
        {
            Entities =
            [
                new MemorySubgraphEntity
                {
                    Entity = new MemoryEntity
                    {
                        Id = "michael",
                        Label = "Michael",
                        Type = "person",
                        Summary = "",
                        Source = "test",
                    },
                    Score = 100,
                    HopDistance = 0,
                    IsDirectSeed = true,
                }
            ],
            Claims =
            [
                new MemorySubgraphClaim
                {
                    Claim = new MemoryClaim
                    {
                        Id = "claim_1",
                        ClaimKey = "claim_key",
                        FactGroupKey = "fact_group_key",
                        SubjectEntityId = "michael",
                        Predicate = "prefers",
                        NormalizedText = "michael prefers clean slate design",
                        Source = "test",
                    },
                    Score = 90,
                    HopDistance = 0,
                    IsDirectSeed = true,
                }
            ],
        };

        var paths = MemoryRetrievalService.BuildDirectSeedPaths(result);

        paths.Count.ShouldBe(2);
        paths.ShouldContain(path => path.SeedId == "michael" && path.DestinationId == "michael");
        paths.ShouldContain(path => path.SeedId == "claim_1" && path.DestinationId == "claim_1");
    }

    [Fact]
    public void BuildDirectSeedPaths_IgnoresNonSeedItems()
    {
        var result = new MemorySubgraphResult
        {
            Entities =
            [
                new MemorySubgraphEntity
                {
                    Entity = new MemoryEntity
                    {
                        Id = "memory_system",
                        Label = "Memory System",
                        Type = "concept",
                        Summary = "",
                        Source = "test",
                    },
                    Score = 40,
                    HopDistance = 1,
                    IsDirectSeed = false,
                }
            ],
            Claims = [],
        };

        var paths = MemoryRetrievalService.BuildDirectSeedPaths(result);
        paths.ShouldBeEmpty();
    }

    [Fact]
    public void FormatSubgraphForLlm_IncludesClaimsAndConflicts()
    {
        var result = new MemorySubgraphResult
        {
            Entities =
            [
                new MemorySubgraphEntity
                {
                    Entity = new MemoryEntity
                    {
                        Id = "michael",
                        Label = "Michael",
                        Type = "person",
                        Summary = "Maintainer",
                        Source = "test",
                    },
                    Score = 100,
                    HopDistance = 0,
                    IsDirectSeed = true,
                }
            ],
            Claims =
            [
                new MemorySubgraphClaim
                {
                    Claim = new MemoryClaim
                    {
                        Id = "claim_1",
                        ClaimKey = "claim_key_1",
                        FactGroupKey = "fact_group_key_1",
                        SubjectEntityId = "michael",
                        Predicate = "prefers",
                        NormalizedText = "michael prefers clean slate design",
                        Source = "test",
                    },
                    Score = 95,
                    HopDistance = 0,
                    IsDirectSeed = true,
                }
            ],
            Observations =
            [
                new MemoryObservation
                {
                    Id = "obs_1",
                    Claim = "michael prefers clean slate design",
                    ConflictsWith = "michael prefers incremental refactor",
                    Source = "test",
                    Timestamp = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc),
                }
            ],
        };

        var formatted = MemoryRetrievalService.FormatSubgraphForLlm(result);

        formatted.ShouldContain("**Michael** (person) — Maintainer");
        formatted.ShouldContain("michael prefers clean slate design");
        formatted.ShouldContain("CONFLICT (unresolved)");
    }

    [Fact]
    public void FormatSubgraphSummary_PlainStyle_OmitsMarkdownFormatting()
    {
        var result = new MemorySubgraphResult
        {
            Entities =
            [
                new MemorySubgraphEntity
                {
                    Entity = new MemoryEntity
                    {
                        Id = "michael",
                        Label = "Michael",
                        Type = "person",
                        Summary = "Maintainer",
                        Source = "test",
                    },
                    Score = 100,
                    HopDistance = 0,
                    IsDirectSeed = true,
                }
            ],
        };

        var formatted = MemoryRetrievalService.FormatSubgraphSummary(result, "plain");

        formatted.ShouldContain("Relevant Memory");
        formatted.ShouldContain("Michael (person) - Maintainer");
        formatted.ShouldNotContain("## Relevant Memory");
        formatted.ShouldNotContain("**Michael**");
    }

    [Fact]
    public async Task SearchAsync_FusesEntityAndClaimSeeds()
    {
        var store = new FakeMemoryGraphStore
        {
            ExactEntity = new MemoryEntity
            {
                Id = "michael",
                ExternalId = "michael",
                Label = "Michael",
                Type = "person",
                Summary = "Maintainer",
                Source = "test",
            },
            TextSearchResults =
            [
                new MemoryEntity
                {
                    Id = "memory_system",
                    Label = "Memory System",
                    Type = "concept",
                    Summary = "Claim graph",
                    Source = "test",
                }
            ],
            ClaimSearchResults =
            [
                (
                    new MemoryClaim
                    {
                        Id = "claim_1",
                        ClaimKey = "claim_key_1",
                        FactGroupKey = "fact_group_key_1",
                        SubjectEntityId = "michael",
                        Predicate = "prefers",
                        NormalizedText = "michael prefers clean slate design",
                        Source = "test",
                    },
                    88d,
                    "lexical"
                )
            ]
        };

        var service = CreateService(store);
        var result = await service.SearchAsync("Michael");

        result.Entities.ShouldContain(seed => seed.EntityId == "michael" && seed.MatchKind == "exact");
        result.Claims.ShouldContain(seed => seed.ClaimId == "claim_1" && seed.MatchKind == "lexical");
    }

    [Fact]
    public async Task SearchAsync_PrunesWeakLexicalEntitySeedsWhenExactMatchExists()
    {
        var store = new FakeMemoryGraphStore
        {
            ExactEntity = new MemoryEntity
            {
                Id = "michael_smoke",
                ExternalId = "michael_smoke",
                Label = "Michael Smoke",
                Type = "person",
                Summary = "Smoke test entity",
                Source = "test",
            },
            TextSearchResults =
            [
                new MemoryEntity
                {
                    Id = "michael_smoke",
                    ExternalId = "michael_smoke",
                    Label = "Michael Smoke",
                    Type = "person",
                    Summary = "Smoke test entity",
                    Source = "test",
                },
                new MemoryEntity
                {
                    Id = "michael_user",
                    Label = "Michael",
                    Type = "person",
                    Summary = "General Michael entity",
                    Source = "test",
                }
            ]
        };

        var service = CreateService(store);
        var result = await service.SearchAsync("Michael Smoke");

        result.Entities.Select(seed => seed.EntityId).ShouldBe(["michael_smoke"]);
    }

    [Fact]
    public async Task GetMemorySubgraphAsync_AddsDirectSeedPathsWhenStoreDoesNotProvideAny()
    {
        var store = new FakeMemoryGraphStore
        {
            ExactEntity = new MemoryEntity
            {
                Id = "michael",
                ExternalId = "michael",
                Label = "Michael",
                Type = "person",
                Summary = "Maintainer",
                Source = "test",
            },
            SubgraphResult = new MemorySubgraphResult
            {
                Entities =
                [
                    new MemorySubgraphEntity
                    {
                        Entity = new MemoryEntity
                        {
                            Id = "michael",
                            Label = "Michael",
                            Type = "person",
                            Summary = "Maintainer",
                            Source = "test",
                        },
                        Score = 100,
                        HopDistance = 0,
                        IsDirectSeed = true,
                    }
                ],
                Claims =
                [
                    new MemorySubgraphClaim
                    {
                        Claim = new MemoryClaim
                        {
                            Id = "claim_1",
                            ClaimKey = "claim_key_1",
                            FactGroupKey = "fact_group_key_1",
                            SubjectEntityId = "michael",
                            Predicate = "prefers",
                            NormalizedText = "michael prefers clean slate design",
                            Source = "test",
                        },
                        Score = 95,
                        HopDistance = 0,
                        IsDirectSeed = true,
                    }
                ]
            }
        };

        var service = CreateService(store);
        var result = await service.GetMemorySubgraphAsync(new MemorySubgraphRequest { Query = "Michael" });

        result.Paths.ShouldContain(path => path.SeedId == "michael" && path.DestinationId == "michael");
        result.Paths.ShouldContain(path => path.SeedId == "claim_1" && path.DestinationId == "claim_1");
    }

    [Fact]
    public async Task QueryAsync_UsesClaimCentricSubgraphAndFormatsLegacyResponse()
    {
        var store = new FakeMemoryGraphStore
        {
            ExactEntity = new MemoryEntity
            {
                Id = "michael",
                ExternalId = "michael",
                Label = "Michael",
                Type = "person",
                Summary = "Maintainer",
                Source = "test",
            },
            SubgraphResult = new MemorySubgraphResult
            {
                Entities =
                [
                    new MemorySubgraphEntity
                    {
                        Entity = new MemoryEntity
                        {
                            Id = "michael",
                            Label = "Michael",
                            Type = "person",
                            Summary = "Maintainer",
                            Source = "test",
                        },
                        Score = 100,
                        HopDistance = 0,
                        IsDirectSeed = true,
                    },
                    new MemorySubgraphEntity
                    {
                        Entity = new MemoryEntity
                        {
                            Id = "memory_system",
                            Label = "Memory System",
                            Type = "concept",
                            Summary = "Claim-centric memory",
                            Source = "test",
                        },
                        Score = 50,
                        HopDistance = 1,
                        IsDirectSeed = false,
                    }
                ],
                Claims =
                [
                    new MemorySubgraphClaim
                    {
                        Claim = new MemoryClaim
                        {
                            Id = "claim_1",
                            ClaimKey = "claim_key_1",
                            FactGroupKey = "fact_group_key_1",
                            SubjectEntityId = "michael",
                            Predicate = "prefers",
                            NormalizedText = "michael prefers clean slate design",
                            Source = "test",
                        },
                        Score = 95,
                        HopDistance = 0,
                        IsDirectSeed = true,
                    }
                ],
                EntityEdges =
                [
                    new MemoryEntityEdge
                    {
                        FromEntityId = "michael",
                        ToEntityId = "memory_system",
                        EdgeType = "uses",
                        BestActiveClaimId = "claim_2",
                        UpdatedAt = DateTime.UtcNow,
                    }
                ],
                Observations =
                [
                    new MemoryObservation
                    {
                        Id = "obs_1",
                        Claim = "michael prefers clean slate design",
                        ConflictsWith = "michael prefers incremental refactor",
                        Source = "test",
                        Timestamp = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc),
                    }
                ]
            }
        };

        var service = CreateService(store);
        var result = await service.QueryAsync("Michael");

        result.Entities.ShouldContain(entity => entity.Entity.Id == "michael");
        result.Entities.First(entity => entity.Entity.Id == "michael")
            .Relationships.ShouldContain(rel => rel.RelationshipType == "uses" && rel.TargetId == "memory_system");
        result.FormattedText.ShouldContain("michael prefers clean slate design");
        result.FormattedText.ShouldContain("CONFLICT (unresolved)");
    }

    [Fact]
    public async Task ExpandMemoryFrontierAsync_ReturnsNewNodesAndBuildsPaths()
    {
        var store = new FakeMemoryGraphStore
        {
            SubgraphResult = new MemorySubgraphResult
            {
                Entities =
                [
                    new MemorySubgraphEntity
                    {
                        Entity = new MemoryEntity
                        {
                            Id = "michael",
                            Label = "Michael",
                            Type = "person",
                            Summary = "Maintainer",
                            Source = "test",
                        },
                        Score = 100,
                        HopDistance = 0,
                        IsDirectSeed = true,
                    },
                    new MemorySubgraphEntity
                    {
                        Entity = new MemoryEntity
                        {
                            Id = "memory_system",
                            Label = "Memory System",
                            Type = "concept",
                            Summary = "Claim graph",
                            Source = "test",
                        },
                        Score = 50,
                        HopDistance = 1,
                        IsDirectSeed = false,
                    },
                    new MemorySubgraphEntity
                    {
                        Entity = new MemoryEntity
                        {
                            Id = "design_notes",
                            Label = "Design Notes",
                            Type = "document",
                            Summary = "Low-priority follow-up",
                            Source = "test",
                        },
                        Score = 10,
                        HopDistance = 2,
                        IsDirectSeed = false,
                    }
                ],
                Claims =
                [
                    new MemorySubgraphClaim
                    {
                        Claim = new MemoryClaim
                        {
                            Id = "claim_1",
                            ClaimKey = "claim_key_1",
                            FactGroupKey = "fact_group_key_1",
                            SubjectEntityId = "michael",
                            Predicate = "prefers",
                            NormalizedText = "michael prefers clean slate design",
                            Source = "test",
                        },
                        Score = 100,
                        HopDistance = 0,
                        IsDirectSeed = true,
                    },
                    new MemorySubgraphClaim
                    {
                        Claim = new MemoryClaim
                        {
                            Id = "claim_2",
                            ClaimKey = "claim_key_2",
                            FactGroupKey = "fact_group_key_2",
                            SubjectEntityId = "memory_system",
                            Predicate = "supports",
                            NormalizedText = "memory system supports iterative deepening",
                            Source = "test",
                        },
                        Score = 45,
                        HopDistance = 1,
                        IsDirectSeed = false,
                    }
                ],
                EntityEdges =
                [
                    new MemoryEntityEdge
                    {
                        FromEntityId = "michael",
                        ToEntityId = "memory_system",
                        EdgeType = "uses",
                        BestActiveClaimId = "claim_2",
                        UpdatedAt = DateTime.UtcNow,
                    },
                    new MemoryEntityEdge
                    {
                        FromEntityId = "memory_system",
                        ToEntityId = "design_notes",
                        EdgeType = "documents",
                        BestActiveClaimId = "claim_3",
                        UpdatedAt = DateTime.UtcNow,
                    }
                ],
            }
        };

        var service = CreateService(store);
        var result = await service.ExpandMemoryFrontierAsync(new MemoryFrontierExpansionRequest
        {
            FrontierEntityIds = ["michael"],
            FrontierClaimIds = ["claim_1"],
            MaxAdditionalHops = 2,
            FrontierLimit = 3,
            MinScore = 20,
        });

        result.AddedEntities.Select(entity => entity.Entity.Id).ShouldBe(["memory_system"]);
        result.AddedClaims.Select(claim => claim.Claim.Id).ShouldBe(["claim_2"]);
        result.Paths.ShouldContain(path =>
            path.DestinationId == "memory_system"
            && path.SeedId == "michael"
            && path.EdgeSequence.SequenceEqual(new[] { "uses" }));
        result.Paths.ShouldContain(path =>
            path.DestinationId == "claim_2"
            && path.SeedId == "michael"
            && path.EdgeSequence.SequenceEqual(new[] { "uses", "SUBJECT" }));
    }

    [Fact]
    public async Task RenderMemorySummaryAsync_ReturnsRequestedStyle()
    {
        var store = new FakeMemoryGraphStore
        {
            SubgraphResult = new MemorySubgraphResult
            {
                Entities =
                [
                    new MemorySubgraphEntity
                    {
                        Entity = new MemoryEntity
                        {
                            Id = "michael",
                            Label = "Michael",
                            Type = "person",
                            Summary = "Maintainer",
                            Source = "test",
                        },
                        Score = 100,
                        HopDistance = 0,
                        IsDirectSeed = true,
                    }
                ],
                Claims =
                [
                    new MemorySubgraphClaim
                    {
                        Claim = new MemoryClaim
                        {
                            Id = "claim_1",
                            ClaimKey = "claim_key_1",
                            FactGroupKey = "fact_group_key_1",
                            SubjectEntityId = "michael",
                            Predicate = "prefers",
                            NormalizedText = "michael prefers clean slate design",
                            Source = "test",
                        },
                        Score = 100,
                        HopDistance = 0,
                        IsDirectSeed = true,
                    }
                ]
            }
        };

        var service = CreateService(store);
        var result = await service.RenderMemorySummaryAsync(new MemorySummaryRenderRequest
        {
            EntityIds = ["michael"],
            Style = "text",
        });

        result.Style.ShouldBe("plain");
        result.Text.ShouldContain("Relevant Memory");
        result.Text.ShouldContain("Michael (person) - Maintainer");
        result.Text.ShouldContain("michael prefers clean slate design");
        result.Text.ShouldNotContain("## Relevant Memory");
    }

    private static MemoryRetrievalService CreateService(IMemoryGraphStore store) =>
        new(store, new FakeEmbeddingService(), NullLogger<MemoryRetrievalService>.Instance);

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public bool IsAvailable => false;
        public int Dimensions => 0;
        public float[] GenerateEmbedding(string text) => [];
        public IReadOnlyList<float[]> GenerateEmbeddings(IReadOnlyList<string> texts) => [];
    }

    private sealed class FakeMemoryGraphStore : IMemoryGraphStore
    {
        public MemoryEntity? ExactEntity { get; set; }
        public List<MemoryEntity> TextSearchResults { get; set; } = [];
        public List<(MemoryClaim Claim, double Score, string MatchKind)> ClaimSearchResults { get; set; } = [];
        public MemorySubgraphResult SubgraphResult { get; set; } = new();

        public Task UpsertEntitiesBatchAsync(IReadOnlyList<MemoryEntity> entities) => Task.CompletedTask;
        public Task UpsertClaimsBatchAsync(IReadOnlyList<MemoryClaim> claims) => Task.CompletedTask;
        public Task AddClaimEdgesBatchAsync(IReadOnlyList<MemoryClaimEdge> edges) => Task.CompletedTask;
        public Task AddEvidenceBatchAsync(IReadOnlyList<MemoryEvidence> evidence) => Task.CompletedTask;
        public Task UpsertEntityEdgesBatchAsync(IReadOnlyList<MemoryEntityEdge> edges) => Task.CompletedTask;
        public Task CreateObservationAsync(MemoryObservation obs) => Task.CompletedTask;
        public Task ResolveObservationAsync(string observationId, string resolution, string? resolvedByMemoryId) => Task.CompletedTask;
        public Task<List<(MemoryEntity Entity, double Score)>> VectorSearchAsync(float[] queryEmbedding, int topK = 5) => Task.FromResult(new List<(MemoryEntity Entity, double Score)>());
        public Task<List<MemoryEntity>> TextSearchAsync(string query, int limit = 5) => Task.FromResult(TextSearchResults.Take(limit).ToList());
        public Task<List<MemoryRelationshipDetail>> GetRelationshipsAsync(string entityId) => Task.FromResult(new List<MemoryRelationshipDetail>());
        public Task<List<MemoryObservation>> GetUnresolvedObservationsAsync(IEnumerable<string> entityIds, IEnumerable<string> claimIds) => Task.FromResult(new List<MemoryObservation>());
        public Task<List<MemoryObservation>> GetUnresolvedObservationsForEntitiesAsync(IEnumerable<string> entityIds) => Task.FromResult(new List<MemoryObservation>());
        public Task<MemoryGraphSnapshot> GetFullGraphAsync(int limit = 200, int skip = 0) => Task.FromResult(new MemoryGraphSnapshot());
        public Task<List<string>> FindCandidateEntityIdsAsync(string candidateId, int limit = 20) => Task.FromResult(new List<string>());
        public Task<MemoryEntity?> GetEntityAsync(string entityId) => Task.FromResult(entityId == ExactEntity?.Id ? ExactEntity : null);
        public Task<MemoryEntity?> GetEntityByExternalIdAsync(string externalId) => Task.FromResult(externalId == ExactEntity?.ExternalId ? ExactEntity : null);
        public Task<List<MemoryLegacyRelationship>> GetLegacyRelationshipsAsync() => Task.FromResult(new List<MemoryLegacyRelationship>());
        public Task<List<MemoryObservation>> GetAllObservationsAsync() => Task.FromResult(new List<MemoryObservation>());
        public Task<MemoryClaim?> GetClaimAsync(string claimId)
        {
            var match = ClaimSearchResults.FirstOrDefault(item => item.Claim.Id == claimId);
            return Task.FromResult<MemoryClaim?>(match.Claim);
        }
        public Task<List<MemoryClaim>> GetClaimsByFactGroupAsync(string factGroupKey) => Task.FromResult(new List<MemoryClaim>());
        public Task<List<MemoryClaim>> GetClaimsBySubjectPredicateAsync(string subjectEntityId, string predicate) => Task.FromResult(new List<MemoryClaim>());
        public Task<MemoryEntityBundle?> GetEntityBundleAsync(string entityId, bool includeSuperseded = false, bool includeConflicts = true, int neighborLimit = 20)
            => Task.FromResult<MemoryEntityBundle?>(null);
        public Task<MemoryClaimBundle?> GetClaimBundleAsync(string claimId, bool includeSupersessionChain = true, bool includeConflicts = true, bool includeEvidence = true)
            => Task.FromResult<MemoryClaimBundle?>(null);
        public Task<List<(MemoryClaim Claim, double Score, string MatchKind)>> SearchClaimsAsync(string query, float[]? queryEmbedding, int limit = 5, bool includeSuperseded = false)
            => Task.FromResult(ClaimSearchResults.Take(limit).ToList());
        public Task<MemorySubgraphResult> GetMemorySubgraphAsync(MemorySubgraphQuery query, int maxHops = 2, int maxReturnedEntities = 20, int maxReturnedClaims = 40, bool includeSuperseded = false, bool includeConflicts = true)
            => Task.FromResult(SubgraphResult);
    }
}
