using CodeGraph.Models.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>Path and glob rules of knowledge scope (sc-27869).</summary>
public sealed class KnowledgePathsTests
{
    [Theory]
    [InlineData("src/Work", "src/Work/WorkService.cs", true)]
    [InlineData("src/Work/WorkService.cs", "src/Work", true)]
    [InlineData("src/**/*.cs", "src/Work/WorkService.cs", true)]
    [InlineData("src/*.cs", "src/Work/WorkService.cs", false)]
    [InlineData("src/Work/WorkService.cs", "src/Other.cs", false)]
    public void Covers_matches_paths_directories_and_globs(string entry, string query, bool expected) =>
        KnowledgePaths.Covers(entry, query).ShouldBe(expected);

    [Fact]
    public void Normalize_refuses_paths_that_leave_the_repository_and_unifies_separators()
    {
        KnowledgePaths.Normalize(".\\src\\Work\\").ShouldBe("src/Work");
        KnowledgePaths.Normalize("../etc/passwd").ShouldBeNull();
        KnowledgePaths.Normalize("C:/x").ShouldBeNull();
    }

    [Fact]
    public void A_glob_that_takes_too_long_to_match_is_an_invalid_argument()
    {
        var glob = string.Concat(Enumerable.Repeat("*a", 25)) + "*b";
        var refusal = Should.Throw<WorkException>(() => KnowledgePaths.Matches(glob, new string('a', 400)));
        refusal.Code.ShouldBe(WorkErrorCodes.InvalidArgument);
        refusal.Message.ShouldContain("simpler glob");
    }
}
