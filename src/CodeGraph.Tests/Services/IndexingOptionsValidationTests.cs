using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class IndexingOptionsValidationTests
{
    [Theory]
    [InlineData("RustSemanticCommandTimeoutSeconds", "0", "timeout")]
    [InlineData("RustSemanticMaxThreads", "0", "max threads")]
    [InlineData("RustSemanticStderrTailCharacters", "128", "stderr tail")]
    [InlineData("RustSemanticCrateGroupSize", "0", "crate group size")]
    [InlineData("RustSemanticGroupTimeoutSeconds", "0", "group timeout")]
    [InlineData("RustSemanticGroupMemoryLimitMb", "16", "group memory limit")]
    [InlineData("RustSemanticProcMacros", "true", "proc-macro expansion requires")]
    public void AddCodeGraphOptions_RejectsInvalidRustSemanticLimits(
        string optionName,
        string optionValue,
        string expectedMessage)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"CodeGraph:IndexingOptions:{optionName}"] = optionValue
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeGraphOptions(configuration);
        using var provider = services.BuildServiceProvider();

        var error = Should.Throw<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<IndexingOptions>>().Value);

        error.Message.ShouldContain(expectedMessage);
    }

    [Fact]
    public void AddCodeGraphOptions_RustSemanticDefaults_GroupCratesWithBuildScriptsAndProcMacrosOff()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeGraphOptions(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<IndexingOptions>>().Value;

        options.RustSemanticCrateGroupSize.ShouldBe(8);
        options.RustSemanticGroupTimeoutSeconds.ShouldBe(600);
        options.RustSemanticGroupMemoryLimitMb.ShouldBe(8192);
        options.RustSemanticBuildScripts.ShouldBeFalse();
        options.RustSemanticProcMacros.ShouldBeFalse();
        options.RustSemanticCommandTimeoutSeconds.ShouldBe(600);
        options.RustSemanticMaxThreads.ShouldBe(2);
    }

    [Fact]
    public void AddCodeGraphOptions_AcceptsProcMacrosWhenBuildScriptsEnabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeGraph:IndexingOptions:RustSemanticBuildScripts"] = "true",
                ["CodeGraph:IndexingOptions:RustSemanticProcMacros"] = "true",
                ["CodeGraph:IndexingOptions:RustSemanticGroupMemoryLimitMb"] = "0"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeGraphOptions(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<IndexingOptions>>().Value;

        options.RustSemanticProcMacros.ShouldBeTrue();
        options.RustSemanticGroupMemoryLimitMb.ShouldBe(0);
    }
}
