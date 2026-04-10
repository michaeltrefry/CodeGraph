using CodeGraph.Services.Memory;
using Shouldly;

namespace CodeGraph.Tests.Memory;

public class MemoryClaimIngestionServiceTests
{
    [Theory]
    [InlineData("Prefers CleanSlateDesign", "prefers_clean_slate_design")]
    [InlineData("uses", "uses")]
    [InlineData("PublishedToQueue", "published_to_queue")]
    public void NormalizePredicate_ConvertsToSnakeCase(string input, string expected)
    {
        var result = MemoryClaimIngestionService.NormalizePredicate(input);
        result.ShouldBe(expected);
    }

    [Fact]
    public void NormalizeFreeText_LowercasesAndCollapsesWhitespace()
    {
        var result = MemoryClaimIngestionService.NormalizeFreeText("  Michael   Prefers \n Clean Slate Design ");
        result.ShouldBe("michael prefers clean slate design");
    }

    [Fact]
    public void ComputeClaimKey_IsStableForIdenticalInputs()
    {
        var first = MemoryClaimIngestionService.ComputeClaimKey(
            "michael",
            "prefers",
            null,
            "clean slate design",
            null,
            "michael prefers clean slate design");

        var second = MemoryClaimIngestionService.ComputeClaimKey(
            "michael",
            "prefers",
            null,
            "clean slate design",
            null,
            "michael prefers clean slate design");

        first.ShouldBe(second);
    }

    [Fact]
    public void ComputeFactGroupKey_IgnoresWordingChangesWhenFactValueMatches()
    {
        var first = MemoryClaimIngestionService.ComputeFactGroupKey(
            "michael",
            "prefers",
            null,
            "clean slate design",
            null);

        var second = MemoryClaimIngestionService.ComputeFactGroupKey(
            "michael",
            "prefers",
            null,
            "clean slate design",
            null);

        first.ShouldBe(second);
    }

    [Fact]
    public void ComputeFactGroupKey_ChangesWhenResolvedValueChanges()
    {
        var first = MemoryClaimIngestionService.ComputeFactGroupKey(
            "michael",
            "prefers",
            null,
            "clean slate design",
            null);

        var second = MemoryClaimIngestionService.ComputeFactGroupKey(
            "michael",
            "prefers",
            null,
            "incremental refactor",
            null);

        first.ShouldNotBe(second);
    }
}
