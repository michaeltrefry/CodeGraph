using CodeGraph.Services.Configuration;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class IndexingOptionsTrustTests
{
    [Fact]
    public void IsDotnetToolingTrusted_DefaultsToUntrusted()
    {
        new IndexingOptions().IsDotnetToolingTrusted("github:https://github.com/example/Demo")
            .ShouldBeFalse();
    }

    [Theory]
    [InlineData("github:https://github.com/example/Demo", "github:https://github.com/example/Demo/", true)]
    [InlineData("folder:group/Demo", "folder:group/Demo", true)]
    [InlineData("github:https://github.com/example/Demo", "github:https://github.com/attacker/Demo", false)]
    [InlineData("github:https://github.com/example/Demo", "github:https://github.com/example/demo", false)]
    [InlineData("folder:group/Demo", "folder:attacker/Demo", false)]
    public void IsDotnetToolingTrusted_RequiresExactCanonicalIdentity(
        string trustedIdentity,
        string? resolvedIdentity,
        bool expected)
    {
        var options = new IndexingOptions { TrustedDotnetRepositories = trustedIdentity };

        options.IsDotnetToolingTrusted(resolvedIdentity).ShouldBe(expected);
    }

    [Fact]
    public void IsDotnetToolingTrusted_FolderPathUsesHostFilesystemCaseRules()
    {
        var options = new IndexingOptions { TrustedDotnetRepositories = "folder:group/demo" };

        options.IsDotnetToolingTrusted("folder:group/Demo")
            .ShouldBe(OperatingSystem.IsWindows());
    }
}
