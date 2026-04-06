using Shouldly;
using CodeGraph.Models.Memory;
using CodeGraph.Services.Memory;

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
}
