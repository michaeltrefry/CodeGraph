using CodeGraph.Data.Neo4j;
using CodeGraph.Models.Memory;
using Neo4j.Driver;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class Neo4jMemoryGraphStoreTests
{
    [Fact]
    public void IsMissingMemoryFulltextIndex_ReturnsTrue_ForExpectedNeo4jError()
    {
        var ex = new ClientException(
            "Neo.ClientError.Procedure.ProcedureCallFailed",
            "Failed to invoke procedure `db.index.fulltext.queryNodes`: Caused by: " +
            "java.lang.IllegalArgumentException: There is no such fulltext schema index: memory_entity_fulltext");

        Neo4jMemoryGraphStore.IsMissingMemoryFulltextIndex(ex).ShouldBeTrue();
    }

    [Fact]
    public void IsMissingMemoryFulltextIndex_ReturnsFalse_ForOtherNeo4jErrors()
    {
        var ex = new ClientException(
            "Neo.ClientError.Statement.SyntaxError",
            "Some other Neo4j problem");

        Neo4jMemoryGraphStore.IsMissingMemoryFulltextIndex(ex).ShouldBeFalse();
    }

    [Fact]
    public void IsMissingMemoryClaimFulltextIndex_ReturnsTrue_ForExpectedNeo4jError()
    {
        var ex = new ClientException(
            "Neo.ClientError.Procedure.ProcedureCallFailed",
            "Failed to invoke procedure `db.index.fulltext.queryNodes`: Caused by: " +
            "java.lang.IllegalArgumentException: There is no such fulltext schema index: memory_claim_fulltext");

        Neo4jMemoryGraphStore.IsMissingMemoryClaimFulltextIndex(ex).ShouldBeTrue();
    }

    [Fact]
    public void IsMissingMemoryClaimFulltextIndex_ReturnsFalse_ForOtherNeo4jErrors()
    {
        var ex = new ClientException(
            "Neo.ClientError.Statement.SyntaxError",
            "Some other Neo4j problem");

        Neo4jMemoryGraphStore.IsMissingMemoryClaimFulltextIndex(ex).ShouldBeFalse();
    }

    [Theory]
    [InlineData("active", MemoryClaimStatus.Active)]
    [InlineData("SUPERSEDED", MemoryClaimStatus.Superseded)]
    [InlineData("Conflicted", MemoryClaimStatus.Conflicted)]
    public void ParseClaimStatus_ParsesKnownValues_CaseInsensitively(string status, MemoryClaimStatus expected)
    {
        Neo4jMemoryGraphStore.ParseClaimStatus(status).ShouldBe(expected);
    }

    [Fact]
    public void ParseClaimStatus_DefaultsToActive_ForUnknownValues()
    {
        Neo4jMemoryGraphStore.ParseClaimStatus("mystery-status").ShouldBe(MemoryClaimStatus.Active);
    }

    [Fact]
    public void NormalizeMemorySearchText_StripsPunctuationAndCollapsesWhitespace()
    {
        Neo4jMemoryGraphStore.NormalizeMemorySearchText(" Memory Smoke Agent (20260410T155937Z)! ")
            .ShouldBe("memory smoke agent 20260410t155937z");
    }

    [Fact]
    public void IsClaimInEntityBundle_ReturnsTrue_WhenEntityIsSubject()
    {
        var claim = new MemoryClaim
        {
            Id = "claim_1",
            ClaimKey = "claim_key_1",
            FactGroupKey = "fact_group_1",
            SubjectEntityId = "entity_a",
            Predicate = "prefers",
            NormalizedText = "entity_a prefers test coverage",
            Source = "test",
        };

        Neo4jMemoryGraphStore.IsClaimInEntityBundle(claim, "entity_a").ShouldBeTrue();
    }

    [Fact]
    public void IsClaimInEntityBundle_ReturnsTrue_WhenEntityIsObject()
    {
        var claim = new MemoryClaim
        {
            Id = "claim_2",
            ClaimKey = "claim_key_2",
            FactGroupKey = "fact_group_2",
            SubjectEntityId = "entity_a",
            Predicate = "uses",
            ObjectEntityId = "entity_b",
            NormalizedText = "entity_a uses entity_b",
            Source = "test",
        };

        Neo4jMemoryGraphStore.IsClaimInEntityBundle(claim, "entity_b").ShouldBeTrue();
    }

    [Fact]
    public void IsClaimInEntityBundle_ReturnsFalse_ForUnrelatedClaim()
    {
        var claim = new MemoryClaim
        {
            Id = "claim_3",
            ClaimKey = "claim_key_3",
            FactGroupKey = "fact_group_3",
            SubjectEntityId = "entity_a",
            Predicate = "uses",
            ObjectEntityId = "entity_b",
            NormalizedText = "entity_a uses entity_b",
            Source = "test",
        };

        Neo4jMemoryGraphStore.IsClaimInEntityBundle(claim, "entity_c").ShouldBeFalse();
    }

    [Theory]
    [InlineData("supersedes", "SUPERSEDES")]
    [InlineData(" conflicts_with ", "CONFLICTS_WITH")]
    [InlineData("supports", "SUPPORTS")]
    [InlineData("DERIVED_FROM", "DERIVED_FROM")]
    public void MapClaimRelationshipType_NormalizesSupportedValues(string edgeType, string expected)
    {
        Neo4jMemoryGraphStore.MapClaimRelationshipType(edgeType).ShouldBe(expected);
    }

    [Fact]
    public void MapClaimRelationshipType_Throws_ForUnsupportedValues()
    {
        Should.Throw<InvalidOperationException>(() => Neo4jMemoryGraphStore.MapClaimRelationshipType("related_to"))
            .Message.ShouldContain("Unsupported memory claim edge type");
    }

    [Fact]
    public void MergeClaimPromotedEntityDistances_AssignsOneHopContextWithoutOverwritingCloserPaths()
    {
        var distances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["direct_seed"] = 0,
            ["already_nearby"] = 1,
            ["far_neighbor"] = 3,
        };

        Neo4jMemoryGraphStore.MergeClaimPromotedEntityDistances(
            distances,
            ["claim_subject", "already_nearby", "far_neighbor"]);

        distances["claim_subject"].ShouldBe(1);
        distances["already_nearby"].ShouldBe(1);
        distances["far_neighbor"].ShouldBe(1);
        distances["direct_seed"].ShouldBe(0);
    }

    [Fact]
    public void RankMemorySubgraphEntityIds_PrioritizesDirectSeedsThenClaimPromotedContext()
    {
        var ranked = Neo4jMemoryGraphStore.RankMemorySubgraphEntityIds(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["neighbor_b"] = 1,
                ["claim_subject"] = 1,
                ["direct_seed"] = 0,
                ["neighbor_c"] = 2,
            },
            new HashSet<string>(["direct_seed"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["claim_subject"], StringComparer.OrdinalIgnoreCase),
            limit: 4);

        ranked.ShouldBe(["direct_seed", "claim_subject", "neighbor_b", "neighbor_c"]);
    }

    [Fact]
    public void IsClaimInMemorySubgraph_ReturnsFalse_ForObjectOnlyMatchesOutsideSelectedSubjects()
    {
        var claim = new MemoryClaim
        {
            Id = "claim_1",
            ClaimKey = "claim_key_1",
            FactGroupKey = "fact_group_1",
            SubjectEntityId = "michael_smoke",
            Predicate = "uses",
            ObjectEntityId = "claim_centric_memory",
            NormalizedText = "michael_smoke uses claim_centric_memory",
            Source = "test",
        };

        Neo4jMemoryGraphStore.IsClaimInMemorySubgraph(
            claim,
            new HashSet<string>(["claim_centric_memory"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ShouldBeFalse();
    }

    [Fact]
    public void IsClaimInMemorySubgraph_ReturnsTrue_WhenClaimStaysInsideSelectedEntitySet()
    {
        var claim = new MemoryClaim
        {
            Id = "claim_2",
            ClaimKey = "claim_key_2",
            FactGroupKey = "fact_group_2",
            SubjectEntityId = "memory_smoke_agent",
            Predicate = "validates",
            ObjectEntityId = "memory_smoke_plan",
            NormalizedText = "memory_smoke_agent validates memory_smoke_plan",
            Source = "test",
        };

        Neo4jMemoryGraphStore.IsClaimInMemorySubgraph(
            claim,
            new HashSet<string>(["memory_smoke_agent", "memory_smoke_plan"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ShouldBeTrue();
    }

    [Fact]
    public void IsClaimInMemorySubgraph_ReturnsTrue_ForDirectSeedClaimEvenWhenEntitiesAreTrimmed()
    {
        var claim = new MemoryClaim
        {
            Id = "claim_seed",
            ClaimKey = "claim_key_seed",
            FactGroupKey = "fact_group_seed",
            SubjectEntityId = "memory_smoke_agent",
            Predicate = "prefers",
            NormalizedText = "memory_smoke_agent prefers structured memory receipts",
            Source = "test",
        };

        Neo4jMemoryGraphStore.IsClaimInMemorySubgraph(
            claim,
            new HashSet<string>(["structured_memory_receipts"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["claim_seed"], StringComparer.OrdinalIgnoreCase)).ShouldBeTrue();
    }

    [Fact]
    public void GetMemorySubgraphClaimHopDistance_UsesNearestAnchoredEntity()
    {
        var claim = new MemoryClaim
        {
            Id = "claim_3",
            ClaimKey = "claim_key_3",
            FactGroupKey = "fact_group_3",
            SubjectEntityId = "memory_smoke_agent",
            Predicate = "validates",
            ObjectEntityId = "memory_smoke_plan",
            NormalizedText = "memory_smoke_agent validates memory_smoke_plan",
            Source = "test",
        };

        Neo4jMemoryGraphStore.GetMemorySubgraphClaimHopDistance(
            claim,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["memory_smoke_agent"] = 2,
                ["memory_smoke_plan"] = 1,
            }).ShouldBe(1);
    }
}
