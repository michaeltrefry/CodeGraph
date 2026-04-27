using CodeGraph.Data;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class LlmCatalogValidatorTests
{
    [Fact]
    public async Task ValidateProviderModelAsync_ReturnsProviderErrorForUnknownProvider()
    {
        var validator = CreateValidator();

        var result = await validator.ValidateProviderModelAsync("gemini", "gemini-pro");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldBe([
            new LlmCatalogValidationError(
                "default_provider",
                "provider must be one of: anthropic, openai, lmstudio")
        ]);
    }

    [Fact]
    public async Task ValidateProviderModelAsync_ReturnsProviderErrorWhenTokenIsMissing()
    {
        var repository = new RecordingLlmConfigRepository
        {
            Providers =
            {
                ["anthropic"] = new LlmProviderConfig(
                    ProviderKey: "anthropic",
                    HasToken: false,
                    EndpointUrl: null,
                    ApiVersion: null,
                    Models: ["claude-sonnet-4-6"],
                    UpdatedBy: null,
                    UpdatedAtUtc: null)
            }
        };
        var validator = CreateValidator(repository);

        var result = await validator.ValidateProviderModelAsync("anthropic", "claude-sonnet-4-6");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldBe([
            new LlmCatalogValidationError(
                "default_provider",
                "provider anthropic is not configured (set the API token first)")
        ]);
    }

    [Fact]
    public async Task ValidateProviderModelAsync_ReturnsModelErrorWhenModelIsNotInCatalog()
    {
        var validator = CreateValidator(new RecordingLlmConfigRepository
        {
            Providers =
            {
                ["anthropic"] = new LlmProviderConfig(
                    ProviderKey: "anthropic",
                    HasToken: true,
                    EndpointUrl: null,
                    ApiVersion: null,
                    Models: ["claude-sonnet-4-6"],
                    UpdatedBy: null,
                    UpdatedAtUtc: null)
            }
        });

        var result = await validator.ValidateProviderModelAsync("anthropic", "claude-3-7-sonnet");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldBe([
            new LlmCatalogValidationError(
                "default_model",
                "model claude-3-7-sonnet is not in the configured model list for anthropic")
        ]);
    }

    [Fact]
    public async Task ValidateProviderModelAsync_ReturnsSuccessWhenProviderAndModelAreConfigured()
    {
        var validator = CreateValidator(new RecordingLlmConfigRepository
        {
            Providers =
            {
                ["openai"] = new LlmProviderConfig(
                    ProviderKey: "openai",
                    HasToken: true,
                    EndpointUrl: null,
                    ApiVersion: null,
                    Models: ["gpt-5"],
                    UpdatedBy: null,
                    UpdatedAtUtc: null)
            }
        });

        var result = await validator.ValidateProviderModelAsync(" OpenAI ", " gpt-5 ");

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnsureProviderModelAsync_ThrowsTypedExceptionForInvalidCatalogSelection()
    {
        var validator = CreateValidator();

        var ex = await Should.ThrowAsync<LlmCatalogValidationException>(() =>
            validator.EnsureProviderModelAsync("anthropic", "claude-sonnet-4-6"));

        ex.Result.Errors.Single().Field.ShouldBe("default_provider");
        ex.Message.ShouldContain("provider anthropic is not configured");
    }

    [Fact]
    public async Task ValidateProviderModelAsync_CachesResultsUntilProviderInvalidates()
    {
        var repository = new RecordingLlmConfigRepository
        {
            Providers =
            {
                ["anthropic"] = new LlmProviderConfig(
                    ProviderKey: "anthropic",
                    HasToken: false,
                    EndpointUrl: null,
                    ApiVersion: null,
                    Models: ["claude-sonnet-4-6"],
                    UpdatedBy: null,
                    UpdatedAtUtc: null)
            }
        };
        var invalidator = new LlmConfigInvalidator();
        var validator = CreateValidator(repository, invalidator);

        (await validator.ValidateProviderModelAsync("anthropic", "claude-sonnet-4-6")).IsValid.ShouldBeFalse();
        repository.Providers["anthropic"] = repository.Providers["anthropic"] with { HasToken = true };

        (await validator.ValidateProviderModelAsync("anthropic", "claude-sonnet-4-6")).IsValid.ShouldBeFalse();

        invalidator.InvalidateProvider("anthropic");
        (await validator.ValidateProviderModelAsync("anthropic", "claude-sonnet-4-6")).IsValid.ShouldBeTrue();
        repository.GetProviderCallCount.ShouldBe(2);
    }

    [Fact]
    public void AddCodeGraphOptions_RegistersLlmCatalogValidator()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddCodeGraphOptions(configuration);

        var descriptor = services.Single(d => d.ServiceType == typeof(ILlmCatalogValidator));
        descriptor.ImplementationType.ShouldBe(typeof(LlmCatalogValidator));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
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

        public int GetProviderCallCount { get; private set; }

        public Task<LlmProviderConfig?> GetProviderAsync(string providerKey, CancellationToken ct = default)
        {
            GetProviderCallCount++;
            Providers.TryGetValue(providerKey, out var config);
            return Task.FromResult(config);
        }

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
            throw new NotSupportedException();

        public Task SetAssistantAsync(LlmAssistantWrite write, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
