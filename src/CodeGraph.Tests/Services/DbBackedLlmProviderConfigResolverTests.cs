using CodeGraph.Data;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class DbBackedLlmProviderConfigResolverTests
{
    [Fact]
    public async Task GetProviderAsync_FallsBackToAnalysisOptions_WhenRepositoryIsUnavailable()
    {
        var services = new ServiceCollection();
        var invalidator = new LlmConfigInvalidator();
        services.AddSingleton<ILlmConfigInvalidator>(invalidator);
        services.AddSingleton(Options.Create(new AnalysisOptions
        {
            OpenAi = new OpenAiAnalysisProviderOptions
            {
                ApiKey = "fallback-key",
                BaseUrl = "https://fallback.example/v1",
                Model = "gpt-fallback"
            }
        }));
        var provider = services.BuildServiceProvider();
        var resolver = new DbBackedLlmProviderConfigResolver(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<AnalysisOptions>>(),
            invalidator);

        var config = await resolver.GetProviderAsync("openai");

        config.ApiKey.ShouldBe("fallback-key");
        config.EndpointUrl.ShouldBe("https://fallback.example/v1");
        config.Model.ShouldBe("gpt-fallback");
        config.HasDbConfig.ShouldBeFalse();
        config.HasDbToken.ShouldBeFalse();
    }

    [Fact]
    public async Task GetProviderAsync_OverlaysDatabaseProviderFieldsOverOptionsFallback()
    {
        var repository = new RecordingLlmConfigRepository
        {
            Providers =
            {
                ["anthropic"] = new LlmProviderConfig(
                    "anthropic",
                    HasToken: true,
                    EndpointUrl: "https://anthropic-db.example/v1",
                    ApiVersion: "2024-01-01",
                    Models: ["claude-db"],
                    UpdatedBy: "michael",
                    UpdatedAtUtc: DateTime.UtcNow)
            },
            Tokens =
            {
                ["anthropic"] = "db-token"
            }
        };
        var (resolver, _) = CreateResolver(repository);

        var config = await resolver.GetProviderAsync("anthropic");

        config.ApiKey.ShouldBe("db-token");
        config.EndpointUrl.ShouldBe("https://anthropic-db.example/v1");
        config.ApiVersion.ShouldBe("2024-01-01");
        config.Model.ShouldBe("claude-fallback");
        config.Models.ShouldBe(["claude-db"]);
        config.HasDbConfig.ShouldBeTrue();
        config.HasDbToken.ShouldBeTrue();
    }

    [Fact]
    public async Task GetProviderAsync_CachesUntilProviderInvalidates()
    {
        var repository = new RecordingLlmConfigRepository();
        var (resolver, invalidator) = CreateResolver(repository);

        (await resolver.GetProviderAsync("openai")).ApiKey.ShouldBe("openai-fallback");

        repository.Providers["openai"] = new LlmProviderConfig(
            "openai",
            HasToken: true,
            EndpointUrl: "https://db.example/v1",
            ApiVersion: null,
            Models: ["gpt-db"],
            UpdatedBy: null,
            UpdatedAtUtc: null);
        repository.Tokens["openai"] = "db-token";

        (await resolver.GetProviderAsync("openai")).ApiKey.ShouldBe("openai-fallback");

        invalidator.InvalidateProvider("openai");

        var refreshed = await resolver.GetProviderAsync("openai");
        refreshed.ApiKey.ShouldBe("db-token");
        refreshed.EndpointUrl.ShouldBe("https://db.example/v1");
    }

    private static (DbBackedLlmProviderConfigResolver Resolver, LlmConfigInvalidator Invalidator) CreateResolver(
        RecordingLlmConfigRepository repository)
    {
        var services = new ServiceCollection();
        var invalidator = new LlmConfigInvalidator();
        services.AddSingleton<ILlmConfigRepository>(repository);
        services.AddSingleton<ILlmConfigInvalidator>(invalidator);
        services.AddSingleton(Options.Create(new AnalysisOptions
        {
            Model = "claude-fallback",
            Anthropic = new AnthropicAnalysisProviderOptions
            {
                ApiKey = "anthropic-fallback",
                Version = "2023-06-01"
            },
            OpenAi = new OpenAiAnalysisProviderOptions
            {
                ApiKey = "openai-fallback",
                BaseUrl = "https://openai-fallback.example/v1",
                Model = "gpt-fallback"
            }
        }));
        var provider = services.BuildServiceProvider();
        return (new DbBackedLlmProviderConfigResolver(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<AnalysisOptions>>(),
            invalidator), invalidator);
    }

    private sealed class RecordingLlmConfigRepository : ILlmConfigRepository
    {
        public Dictionary<string, LlmProviderConfig> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Tokens { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<LlmProviderConfig?> GetProviderAsync(string providerKey, CancellationToken ct = default) =>
            Task.FromResult(Providers.GetValueOrDefault(providerKey));

        public Task<string?> GetProviderTokenAsync(string providerKey, CancellationToken ct = default) =>
            Task.FromResult<string?>(Tokens.GetValueOrDefault(providerKey));

        public Task SetProviderAsync(LlmProviderWrite write, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LlmAnalysisConfig?> GetAnalysisAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SetAnalysisAsync(LlmAnalysisWrite write, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LlmReviewConfig?> GetReviewAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SetReviewAsync(LlmReviewWrite write, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LlmAssistantConfig?> GetAssistantAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SetAssistantAsync(LlmAssistantWrite write, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
