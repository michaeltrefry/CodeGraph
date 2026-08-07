using CodeGraph.Services;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class RepositoryIdentityTests
{
    [Fact]
    public void FromRemote_DerivesOneProviderQualifiedIdentityAndSourceGroup()
    {
        var resolved = RepositoryIdentity.FromRemote(
            "github", "CodeGraph", "https://GitHub.com/michaeltrefry/CodeGraph.git");

        resolved.CanonicalIdentity.ShouldBe("github:https://github.com/michaeltrefry/CodeGraph");
        resolved.SourceGroup.ShouldBe("michaeltrefry");
    }

    [Fact]
    public void FromRemote_RejectsRepositoryNameThatDisagreesWithUrl()
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            RepositoryIdentity.FromRemote(
                "github", "CodeGraph", "https://github.com/attacker/OtherRepo.git"));

        exception.Message.ShouldContain("inconsistent");
    }
}
