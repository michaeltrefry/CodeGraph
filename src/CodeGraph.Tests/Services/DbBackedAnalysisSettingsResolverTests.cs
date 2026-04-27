using CodeGraph.Data;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class DbBackedAnalysisSettingsResolverTests
{
    [Fact]
    public async Task GetAnalysisAsync_FallsBackToAnalysisOptions_WhenRepositoryIsUnavailable()
    {
        var services = new ServiceCollection();
        var invalidator = new LlmConfigInvalidator();
        services.AddSingleton<ILlmConfigInvalidator>(invalidator);
        services.AddSingleton<ILlmCatalogValidator>(CreateValidator());
        services.AddSingleton(Options.Create(new AnalysisOptions
        {
            DefaultProvider = "openai",
            Model = "gpt-fallback",
            MaxTokensPerAnalysis = 123,
            MaxTokensPerSynthesis = 456,
            MaxFileSizeKb = 789,
            MaxParallelAnalyses = 2,
            MaxSourceChars = 321
        }));
        var provider = services.BuildServiceProvider();
        var resolver = new DbBackedAnalysisSettingsResolver(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<AnalysisOptions>>(),
            provider.GetRequiredService<ILlmCatalogValidator>(),
            invalidator);

        var config = await resolver.GetAnalysisAsync();

        config.DefaultProvider.ShouldBe("openai");
        config.DefaultModel.ShouldBe("gpt-fallback");
        config.MaxTokensPerAnalysis.ShouldBe(123);
        config.MaxTokensPerSynthesis.ShouldBe(456);
        config.MaxFileSizeKb.ShouldBe(789);
        config.MaxParallelAnalyses.ShouldBe(2);
        config.MaxSourceChars.ShouldBe(321);
        config.HasDbConfig.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAnalysisAsync_OverlaysDatabaseFieldsAndValidatesCatalog()
    {
        var repository = new RecordingLlmConfigRepository
        {
            Analysis = new LlmAnalysisConfig(
                DefaultProvider: "anthropic",
                DefaultModel: "claude-db",
                MaxTokensPerAnalysis: 1000,
                MaxTokensPerSynthesis: 2000,
                MaxFileSizeKb: 3000,
                MaxParallelAnalyses: 4,
                MaxSourceChars: 5000,
                UpdatedBy: "michael",
                UpdatedAtUtc: DateTime.UtcNow),
            Providers =
            {
                ["anthropic"] = new LlmProviderConfig(
                    "anthropic",
                    HasToken: true,
                    EndpointUrl: null,
                    ApiVersion: null,
                    Models: ["claude-db"],
                    UpdatedBy: null,
                    UpdatedAtUtc: null)
            }
        };
        var (resolver, _) = CreateResolver(repository);

        var config = await resolver.GetAnalysisAsync();

        config.DefaultProvider.ShouldBe("anthropic");
        config.DefaultModel.ShouldBe("claude-db");
        config.MaxTokensPerAnalysis.ShouldBe(1000);
        config.MaxTokensPerSynthesis.ShouldBe(2000);
        config.MaxFileSizeKb.ShouldBe(3000);
        config.MaxParallelAnalyses.ShouldBe(4);
        config.MaxSourceChars.ShouldBe(5000);
        config.HasDbConfig.ShouldBeTrue();
    }

    [Fact]
    public async Task GetAnalysisAsync_CachesUntilAnalysisInvalidates()
    {
        var repository = new RecordingLlmConfigRepository
        {
            Providers =
            {
                ["openai"] = new LlmProviderConfig(
                    "openai",
                    HasToken: true,
                    EndpointUrl: null,
                    ApiVersion: null,
                    Models: ["gpt-db"],
                    UpdatedBy: null,
                    UpdatedAtUtc: null)
            }
        };
        var (resolver, invalidator) = CreateResolver(repository);

        (await resolver.GetAnalysisAsync()).DefaultModel.ShouldBe("gpt-fallback");

        repository.Analysis = new LlmAnalysisConfig(
            DefaultProvider: "openai",
            DefaultModel: "gpt-db",
            MaxTokensPerAnalysis: 111,
            MaxTokensPerSynthesis: 222,
            MaxFileSizeKb: 333,
            MaxParallelAnalyses: 4,
            MaxSourceChars: 555,
            UpdatedBy: null,
            UpdatedAtUtc: null);

        (await resolver.GetAnalysisAsync()).DefaultModel.ShouldBe("gpt-fallback");

        invalidator.InvalidateAnalysis();
        (await resolver.GetAnalysisAsync()).DefaultModel.ShouldBe("gpt-db");
    }

    [Fact]
    public async Task GetAnalysisAsync_ThrowsTypedCatalogException_WhenDatabaseSelectionIsInvalid()
    {
        var repository = new RecordingLlmConfigRepository
        {
            Analysis = new LlmAnalysisConfig(
                DefaultProvider: "openai",
                DefaultModel: "missing-model",
                MaxTokensPerAnalysis: null,
                MaxTokensPerSynthesis: null,
                MaxFileSizeKb: null,
                MaxParallelAnalyses: null,
                MaxSourceChars: null,
                UpdatedBy: null,
                UpdatedAtUtc: null),
            Providers =
            {
                ["openai"] = new LlmProviderConfig(
                    "openai",
                    HasToken: true,
                    EndpointUrl: null,
                    ApiVersion: null,
                    Models: ["gpt-db"],
                    UpdatedBy: null,
                    UpdatedAtUtc: null)
            }
        };
        var (resolver, _) = CreateResolver(repository);

        var ex = await Should.ThrowAsync<LlmCatalogValidationException>(() => resolver.GetAnalysisAsync());
        ex.Result.Errors.Single().Field.ShouldBe("default_model");
    }

    [Fact]
    public void AddCodeGraphOptions_RegistersDbBackedAnalysisSettingsResolver()
    {
        var services = new ServiceCollection();
        services.AddCodeGraphOptions(new ConfigurationBuilder().Build());

        var descriptor = services.Single(d => d.ServiceType == typeof(IDbBackedAnalysisSettingsResolver));
        descriptor.ImplementationType.ShouldBe(typeof(DbBackedAnalysisSettingsResolver));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    private static (DbBackedAnalysisSettingsResolver Resolver, LlmConfigInvalidator Invalidator) CreateResolver(
        RecordingLlmConfigRepository repository)
    {
        var services = new ServiceCollection();
        var invalidator = new LlmConfigInvalidator();
        services.AddSingleton<ILlmConfigRepository>(repository);
        services.AddSingleton<ILlmConfigInvalidator>(invalidator);
        services.AddSingleton<ILlmCatalogValidator>(CreateValidator(repository, invalidator));
        services.AddSingleton(Options.Create(new AnalysisOptions
        {
            DefaultProvider = "openai",
            Model = "gpt-fallback",
            MaxTokensPerAnalysis = 10,
            MaxTokensPerSynthesis = 20,
            MaxFileSizeKb = 30,
            MaxParallelAnalyses = 1,
            MaxSourceChars = 40
        }));
        var provider = services.BuildServiceProvider();
        return (new DbBackedAnalysisSettingsResolver(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<AnalysisOptions>>(),
            provider.GetRequiredService<ILlmCatalogValidator>(),
            invalidator), invalidator);
    }

    private static LlmCatalogValidator CreateValidator(
        RecordingLlmConfigRepository? repository = null,
        LlmConfigInvalidator? invalidator = null)
    {
        repository ??= new RecordingLlmConfigRepository();
        return new LlmCatalogValidator(repository.GetProviderAsync, invalidator ?? new LlmConfigInvalidator());
    }

    private sealed class RecordingLlmConfigRepository : ILlmConfigRepository
    {
        public Dictionary<string, LlmProviderConfig> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public LlmAnalysisConfig? Analysis { get; set; }

        public Task<LlmProviderConfig?> GetProviderAsync(string providerKey, CancellationToken ct = default) =>
            Task.FromResult(Providers.GetValueOrDefault(providerKey));

        public Task<string?> GetProviderTokenAsync(string providerKey, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SetProviderAsync(LlmProviderWrite write, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LlmAnalysisConfig?> GetAnalysisAsync(CancellationToken ct = default) =>
            Task.FromResult(Analysis);

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
