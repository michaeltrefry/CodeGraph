using Shouldly;
using CodeGraph.Services.Analyzers;

namespace CodeGraph.Tests.Services;

public class LocalAnalysisProviderTests
{
    [Fact]
    public void NormalizeBaseUrl_RewritesLocalhost_WhenRunningInContainer()
    {
        var previous = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", "true");

        try
        {
            var normalized = LocalAnalysisProvider.NormalizeBaseUrl("http://localhost:1234/v1");

            normalized.ShouldBe("http://host.docker.internal:1234/v1");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", previous);
        }
    }

    [Fact]
    public void NormalizeBaseUrl_LeavesLocalhostAlone_WhenNotRunningInContainer()
    {
        var previous = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", "false");

        try
        {
            var normalized = LocalAnalysisProvider.NormalizeBaseUrl("http://localhost:1234/v1");

            normalized.ShouldBe("http://localhost:1234/v1");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", previous);
        }
    }
}
