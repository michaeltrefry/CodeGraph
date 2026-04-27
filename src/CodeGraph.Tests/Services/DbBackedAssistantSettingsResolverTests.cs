using CodeGraph.Data;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class DbBackedAssistantSettingsResolverTests
{
    [Fact]
    public async Task GetAssistantAsync_FallsBackToAnalysisOptions_WhenRepositoryIsUnavailable()
    {
        var services = new ServiceCollection();
        var invalidator = new LlmConfigInvalidator();
        services.AddSingleton<ILlmConfigInvalidator>(invalidator);
        services.AddSingleton<ILlmCatalogValidator>(CreateValidator());
        services.AddSingleton(Options.Create(new AnalysisOptions
        {
            DefaultProvider = "openai",
            Assistant = new AssistantOptions
            {
                Provider = "lmstudio",
                Model = "qwen3",
                MaxTokens = 7000,
                MaxTurns = 8
            }
        }));
        var provider = services.BuildServiceProvider();
        var resolver = new DbBackedAssistantSettingsResolver(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<AnalysisOptions>>(),
            provider.GetRequiredService<ILlmCatalogValidator>(),
            invalidator);

        var config = await resolver.GetAssistantAsync();

        config.DefaultProvider.ShouldBe("lmstudio");
        config.DefaultModel.ShouldBe("qwen3");
        config.MaxTokens.ShouldBe(7000);
        config.MaxTurns.ShouldBe(8);
        config.HasDbConfig.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAssistantAsync_OverlaysDatabaseFieldsAndValidatesCatalog()
    {
        var repository = new RecordingLlmConfigRepository
        {
            Assistant = new LlmAssistantConfig(
                DefaultProvider: "anthropic",
                DefaultModel: "claude-assistant",
                MaxTokens: 11000,
                MaxTurns: 5,
                UpdatedBy: "michael",
                UpdatedAtUtc: DateTime.UtcNow),
            Providers =
            {
                ["anthropic"] = new LlmProviderConfig(
                    "anthropic",
                    HasToken: true,
                    EndpointUrl: null,
                    ApiVersion: null,
                    Models: ["claude-assistant"],
                    UpdatedBy: null,
                    UpdatedAtUtc: null)
            }
        };
        var (resolver, _) = CreateResolver(repository);

        var config = await resolver.GetAssistantAsync();

        config.DefaultProvider.ShouldBe("anthropic");
        config.DefaultModel.ShouldBe("claude-assistant");
        config.MaxTokens.ShouldBe(11000);
        config.MaxTurns.ShouldBe(5);
        config.HasDbConfig.ShouldBeTrue();
    }

    [Fact]
    public async Task GetAssistantAsync_CachesUntilAssistantInvalidates()
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

        (await resolver.GetAssistantAsync()).DefaultModel.ShouldBe("gpt-fallback-assistant");

        repository.Assistant = new LlmAssistantConfig(
            DefaultProvider: "openai",
            DefaultModel: "gpt-db",
            MaxTokens: 9000,
            MaxTurns: 6,
            UpdatedBy: null,
            UpdatedAtUtc: null);

        (await resolver.GetAssistantAsync()).DefaultModel.ShouldBe("gpt-fallback-assistant");

        invalidator.InvalidateAssistant();
        (await resolver.GetAssistantAsync()).DefaultModel.ShouldBe("gpt-db");
    }

    [Fact]
    public async Task GetAssistantAsync_ThrowsTypedCatalogException_WhenDatabaseSelectionIsInvalid()
    {
        var repository = new RecordingLlmConfigRepository
        {
            Assistant = new LlmAssistantConfig(
                DefaultProvider: "openai",
                DefaultModel: "missing-model",
                MaxTokens: null,
                MaxTurns: null,
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

        var ex = await Should.ThrowAsync<LlmCatalogValidationException>(() => resolver.GetAssistantAsync());
        ex.Result.Errors.Single().Field.ShouldBe("default_model");
    }

    [Fact]
    public void AddCodeGraphOptions_RegistersDbBackedAssistantSettingsResolver()
    {
        var services = new ServiceCollection();
        services.AddCodeGraphOptions(new ConfigurationBuilder().Build());

        var descriptor = services.Single(d => d.ServiceType == typeof(IDbBackedAssistantSettingsResolver));
        descriptor.ImplementationType.ShouldBe(typeof(DbBackedAssistantSettingsResolver));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    private static (DbBackedAssistantSettingsResolver Resolver, LlmConfigInvalidator Invalidator) CreateResolver(
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
            Assistant = new AssistantOptions
            {
                Model = "gpt-fallback-assistant",
                MaxTokens = 1,
                MaxTurns = 2
            }
        }));
        var provider = services.BuildServiceProvider();
        return (new DbBackedAssistantSettingsResolver(
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
        public LlmAssistantConfig? Assistant { get; set; }

        public Task<LlmProviderConfig?> GetProviderAsync(string providerKey, CancellationToken ct = default) =>
            Task.FromResult(Providers.GetValueOrDefault(providerKey));

        public Task<string?> GetProviderTokenAsync(string providerKey, CancellationToken ct = default) =>
            throw new NotSupportedException();

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
            Task.FromResult(Assistant);

        public Task SetAssistantAsync(LlmAssistantWrite write, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
