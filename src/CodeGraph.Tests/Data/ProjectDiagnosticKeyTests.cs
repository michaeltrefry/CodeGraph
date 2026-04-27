using CodeGraph.Data;
using Shouldly;

namespace CodeGraph.Tests.Data;

public class ProjectDiagnosticKeyTests
{
    [Fact]
    public void Create_ReturnsStableBoundedKeyForLongDiagnosticMaterial()
    {
        var longPath = $"src/{new string('a', 320)}/Widget.cs";
        var longMessage = $"This diagnostic includes a very long analyzer message: {new string('b', 1_000)}";

        var first = ProjectDiagnosticKey.Create(
            "CodeFlow.Api",
            "CS8602",
            longPath,
            41,
            41,
            longMessage);

        var second = ProjectDiagnosticKey.Create(
            "CodeFlow.Api",
            "CS8602",
            longPath,
            41,
            41,
            longMessage);

        first.Length.ShouldBeLessThanOrEqualTo(ProjectDiagnosticKey.MaxLength);
        second.ShouldBe(first);
        first.ShouldStartWith("CS8602|");
    }

    [Fact]
    public void Create_ChangesWhenDiagnosticLocationOrMessageChanges()
    {
        var first = ProjectDiagnosticKey.Create("CodeFlow.Api", "CS8602", "src/Widget.cs", 41, 41, "first");
        var second = ProjectDiagnosticKey.Create("CodeFlow.Api", "CS8602", "src/Widget.cs", 42, 42, "first");
        var third = ProjectDiagnosticKey.Create("CodeFlow.Api", "CS8602", "src/Widget.cs", 41, 41, "second");

        second.ShouldNotBe(first);
        third.ShouldNotBe(first);
    }

    [Fact]
    public void EnsureWithinLimit_HashesOversizedLegacyKeys()
    {
        var oversized = $"CodeFlow.Api|CS8602|{new string('x', 500)}";

        var normalized = ProjectDiagnosticKey.EnsureWithinLimit(oversized);

        normalized.Length.ShouldBeLessThanOrEqualTo(ProjectDiagnosticKey.MaxLength);
        normalized.ShouldNotBe(oversized);
        normalized.ShouldContain("|");
    }
}
