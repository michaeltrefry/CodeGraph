using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class LlmConfigInvalidatorTests
{
    [Fact]
    public void InvalidateMethods_InvokeMatchingSubscribers()
    {
        var invalidator = new LlmConfigInvalidator();
        var providers = new List<string>();
        var analysisCount = 0;
        var reviewCount = 0;
        var assistantCount = 0;

        invalidator.ProviderChanged += providers.Add;
        invalidator.AnalysisChanged += () => analysisCount++;
        invalidator.ReviewChanged += () => reviewCount++;
        invalidator.AssistantChanged += () => assistantCount++;

        invalidator.InvalidateProvider(" Anthropic ");
        invalidator.InvalidateAnalysis();
        invalidator.InvalidateReview();
        invalidator.InvalidateAssistant();

        providers.ShouldBe(["anthropic"]);
        analysisCount.ShouldBe(1);
        reviewCount.ShouldBe(1);
        assistantCount.ShouldBe(1);
    }

    [Fact]
    public void AddCodeGraphOptions_RegistersLlmConfigInvalidatorAsSingleton()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddCodeGraphOptions(configuration);

        var descriptor = services.Single(d => d.ServiceType == typeof(ILlmConfigInvalidator));
        descriptor.ImplementationType.ShouldBe(typeof(LlmConfigInvalidator));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }
}
