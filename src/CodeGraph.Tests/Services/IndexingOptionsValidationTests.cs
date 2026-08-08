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
}
