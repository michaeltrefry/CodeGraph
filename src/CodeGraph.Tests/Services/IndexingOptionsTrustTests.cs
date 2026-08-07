using CodeGraph.Services.Configuration;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class IndexingOptionsTrustTests
{
    [Fact]
    public void IsDotnetToolingTrusted_DefaultsToUntrusted()
    {
        new IndexingOptions().IsDotnetToolingTrusted(
            "Demo", "https://github.com/example/Demo", "example").ShouldBeFalse();
    }

    [Theory]
    [InlineData("https://github.com/example/Demo", "https://github.com/example/Demo/", "other", true)]
    [InlineData("example/Demo", "https://github.com/other/Demo", "example", true)]
    [InlineData("local:Demo", null, null, true)]
    [InlineData("local:Demo", "https://github.com/attacker/Demo", "attacker", false)]
    [InlineData("example/Demo", "https://github.com/attacker/Demo", "attacker", false)]
    public void IsDotnetToolingTrusted_RequiresExactCanonicalIdentity(
        string trustedIdentity,
        string? repoUrl,
        string? sourceGroup,
        bool expected)
    {
        var options = new IndexingOptions { TrustedDotnetRepositories = trustedIdentity };

        options.IsDotnetToolingTrusted("Demo", repoUrl, sourceGroup).ShouldBe(expected);
    }
}
