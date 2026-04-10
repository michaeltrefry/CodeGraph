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
}
