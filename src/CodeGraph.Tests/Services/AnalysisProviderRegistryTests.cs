using Shouldly;
using Microsoft.Extensions.Options;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Tests.Services;

public class AnalysisProviderRegistryTests
{
    [Fact]
    public void GetProvider_UsesConfiguredDefaultProvider()
    {
        var registry = new AnalysisProviderRegistry(
            [
                new FakeProvider("anthropic"),
                new FakeProvider("openai"),
                new FakeProvider("gemini"),
                new FakeProvider("local")
            ],
            Options.Create(new AnalysisOptions { DefaultProvider = "openai" }));

        var provider = registry.GetProvider();

        provider.ProviderName.ShouldBe("openai");
    }

    [Fact]
    public void GetProvider_AllowsExplicitOverride()
    {
        var registry = new AnalysisProviderRegistry(
            [
                new FakeProvider("anthropic"),
                new FakeProvider("openai"),
                new FakeProvider("gemini"),
                new FakeProvider("local")
            ],
            Options.Create(new AnalysisOptions { DefaultProvider = "anthropic" }));

        var provider = registry.GetProvider("openai");

        provider.ProviderName.ShouldBe("openai");
    }

    [Fact]
    public void LegacyAnthropicProperties_ProxyNestedAnthropicOptions()
    {
        var options = new AnalysisOptions
        {
            ApiKey = "secret",
            MessagesApiUrl = "https://example.test/messages",
            BatchApiBaseUrl = "https://example.test/batches",
            AnthropicVersion = "2026-01-01"
        };

        options.Anthropic.ApiKey.ShouldBe("secret");
        options.Anthropic.MessagesApiUrl.ShouldBe("https://example.test/messages");
        options.Anthropic.BatchApiBaseUrl.ShouldBe("https://example.test/batches");
        options.Anthropic.Version.ShouldBe("2026-01-01");
    }

    private sealed class FakeProvider(string name) : IAnalysisModelProvider
    {
        public string ProviderName => name;

        public AnalysisProviderCapabilities Capabilities { get; } =
            new(SupportsBatch: false, SupportsStructuredJson: true, SupportsStreaming: false, SupportsLargeContext: false);

        public Task<AnalysisTextResponse> ExecuteAsync(
            AnalysisPrompt prompt,
            AnalysisRequestOptions request,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<AnalysisBatchSubmissionResult> SubmitBatchAsync(
            IReadOnlyList<AnalysisBatchRequestItem> items,
            AnalysisRequestOptions request,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<AnalysisBatchStatusResult> GetBatchStatusAsync(string batchId, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<AnalysisBatchItemResult>> GetBatchResultsAsync(
            string batchId,
            IReadOnlyList<string>? requestIds = null,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }
    }
}
