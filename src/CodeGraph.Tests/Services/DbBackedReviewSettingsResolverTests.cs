using CodeGraph.Data;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class DbBackedReviewSettingsResolverTests
{
    [Fact]
    public async Task GetReviewAsync_FallsBackToAnalysisOptions_WhenRepositoryIsUnavailable()
    {
        var services = new ServiceCollection();
        var invalidator = new LlmConfigInvalidator();
        services.AddSingleton<ILlmConfigInvalidator>(invalidator);
        services.AddSingleton<ILlmCatalogValidator>(CreateValidator());
        services.AddSingleton(Options.Create(new AnalysisOptions
        {
            DefaultProvider = "openai",
            Review = new ReviewOptions
            {
                Model = "gpt-review",
                MaxFilesToInspect = 7,
                MaxSourceCharsPerFile = 8000,
                MaxInspectionPasses = 3,
                MaxFindings = 9
            }
        }));
        var provider = services.BuildServiceProvider();
        var resolver = new DbBackedReviewSettingsResolver(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<AnalysisOptions>>(),
            provider.GetRequiredService<ILlmCatalogValidator>(),
            invalidator);

        var config = await resolver.GetReviewAsync();

        config.DefaultProvider.ShouldBe("openai");
        config.DefaultModel.ShouldBe("gpt-review");
        config.MaxFilesToInspect.ShouldBe(7);
        config.MaxSourceCharsPerFile.ShouldBe(8000);
        config.MaxInspectionPasses.ShouldBe(3);
        config.MaxFindings.ShouldBe(9);
        config.HasDbConfig.ShouldBeFalse();
    }

    [Fact]
    public async Task GetReviewAsync_OverlaysDatabaseFieldsAndValidatesCatalog()
    {
        var repository = new RecordingLlmConfigRepository
        {
            Review = new LlmReviewConfig(
                DefaultProvider: "anthropic",
                DefaultModel: "claude-review",
                MaxFilesToInspect: 11,
                MaxSourceCharsPerFile: 22000,
                MaxInspectionPasses: 5,
                MaxFindings: 13,
                UpdatedBy: "michael",
                UpdatedAtUtc: DateTime.UtcNow),
            Providers =
            {
                ["anthropic"] = new LlmProviderConfig(
                    "anthropic",
                    HasToken: true,
                    EndpointUrl: null,
                    ApiVersion: null,
                    Models: ["claude-review"],
                    UpdatedBy: null,
                    UpdatedAtUtc: null)
            }
        };
        var (resolver, _) = CreateResolver(repository);

        var config = await resolver.GetReviewAsync();

        config.DefaultProvider.ShouldBe("anthropic");
        config.DefaultModel.ShouldBe("claude-review");
        config.MaxFilesToInspect.ShouldBe(11);
        config.MaxSourceCharsPerFile.ShouldBe(22000);
        config.MaxInspectionPasses.ShouldBe(5);
        config.MaxFindings.ShouldBe(13);
        config.HasDbConfig.ShouldBeTrue();
    }

    [Fact]
    public async Task GetReviewAsync_CachesUntilReviewInvalidates()
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

        (await resolver.GetReviewAsync()).DefaultModel.ShouldBe("gpt-fallback-review");

        repository.Review = new LlmReviewConfig(
            DefaultProvider: "openai",
            DefaultModel: "gpt-db",
            MaxFilesToInspect: 10,
            MaxSourceCharsPerFile: 20,
            MaxInspectionPasses: 3,
            MaxFindings: 4,
            UpdatedBy: null,
            UpdatedAtUtc: null);

        (await resolver.GetReviewAsync()).DefaultModel.ShouldBe("gpt-fallback-review");

        invalidator.InvalidateReview();
        (await resolver.GetReviewAsync()).DefaultModel.ShouldBe("gpt-db");
    }

    [Fact]
    public async Task GetReviewAsync_ThrowsTypedCatalogException_WhenDatabaseSelectionIsInvalid()
    {
        var repository = new RecordingLlmConfigRepository
        {
            Review = new LlmReviewConfig(
                DefaultProvider: "openai",
                DefaultModel: "missing-model",
                MaxFilesToInspect: null,
                MaxSourceCharsPerFile: null,
                MaxInspectionPasses: null,
                MaxFindings: null,
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

        var ex = await Should.ThrowAsync<LlmCatalogValidationException>(() => resolver.GetReviewAsync());
        ex.Result.Errors.Single().Field.ShouldBe("default_model");
    }

    [Fact]
    public void AddCodeGraphOptions_RegistersDbBackedReviewSettingsResolver()
    {
        var services = new ServiceCollection();
        services.AddCodeGraphOptions(new ConfigurationBuilder().Build());

        var descriptor = services.Single(d => d.ServiceType == typeof(IDbBackedReviewSettingsResolver));
        descriptor.ImplementationType.ShouldBe(typeof(DbBackedReviewSettingsResolver));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    private static (DbBackedReviewSettingsResolver Resolver, LlmConfigInvalidator Invalidator) CreateResolver(
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
            Review = new ReviewOptions
            {
                Model = "gpt-fallback-review",
                MaxFilesToInspect = 1,
                MaxSourceCharsPerFile = 2,
                MaxInspectionPasses = 3,
                MaxFindings = 4
            }
        }));
        var provider = services.BuildServiceProvider();
        return (new DbBackedReviewSettingsResolver(
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
        public LlmReviewConfig? Review { get; set; }

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
            Task.FromResult(Review);

        public Task SetReviewAsync(LlmReviewWrite write, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LlmAssistantConfig?> GetAssistantAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SetAssistantAsync(LlmAssistantWrite write, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
