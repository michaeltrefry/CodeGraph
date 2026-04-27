using System.Security.Claims;
using CodeGraph.Api.Controllers;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CodeGraph.Tests.Controllers;

public class LlmProvidersControllerTests
{
    [Fact]
    public async Task ListProviders_ReturnsAllKnownProviders()
    {
        var repository = new RecordingLlmConfigRepository
        {
            Providers =
            {
                ["anthropic"] = new LlmProviderConfig(
                    "anthropic",
                    HasToken: true,
                    EndpointUrl: "https://anthropic.example/v1",
                    ApiVersion: "2024-01-01",
                    Models: ["claude-sonnet-4-6"],
                    UpdatedBy: "michael",
                    UpdatedAtUtc: DateTime.UtcNow)
            }
        };
        var controller = CreateController(repository);

        var result = await controller.ListProviders(CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var providers = ok.Value.ShouldBeAssignableTo<IReadOnlyList<LlmProviderResponse>>();
        providers.ShouldNotBeNull();
        providers.Select(provider => provider.Provider).ShouldBe(["anthropic", "openai", "lmstudio"]);
        providers.Single(provider => provider.Provider == "anthropic").HasToken.ShouldBeTrue();
        providers.Single(provider => provider.Provider == "openai").Models.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpdateProvider_PersistsWriteAndInvalidatesProvider()
    {
        var repository = new RecordingLlmConfigRepository();
        var invalidator = new LlmConfigInvalidator();
        var invalidatedProvider = "";
        invalidator.ProviderChanged += provider => invalidatedProvider = provider;
        var controller = CreateController(repository, invalidator);

        var result = await controller.UpdateProvider(
            " Anthropic ",
            new LlmProviderWriteRequest(
                EndpointUrl: " https://anthropic.example/v1 ",
                ApiVersion: " 2024-01-01 ",
                Models: ["claude-sonnet-4-6"],
                Token: new LlmProviderTokenActionRequest(LlmProviderTokenActionKindRequest.Replace, "sk-ant")),
            CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBeOfType<LlmProviderResponse>().HasToken.ShouldBeTrue();
        repository.LastWrite.ShouldNotBeNull();
        repository.LastWrite.ProviderKey.ShouldBe("anthropic");
        repository.LastWrite.Token!.Action.ShouldBe(LlmProviderTokenActionKind.Replace);
        repository.LastWrite.UpdatedBy.ShouldBe("Michael");
        invalidatedProvider.ShouldBe("anthropic");
    }

    [Fact]
    public async Task ListProviderModels_ReturnsFlatCatalog()
    {
        var repository = new RecordingLlmConfigRepository
        {
            Providers =
            {
                ["anthropic"] = new LlmProviderConfig(
                    "anthropic",
                    HasToken: true,
                    EndpointUrl: null,
                    ApiVersion: null,
                    Models: ["claude-sonnet-4-6", "claude-opus-4-1"],
                    UpdatedBy: null,
                    UpdatedAtUtc: null),
                ["openai"] = new LlmProviderConfig(
                    "openai",
                    HasToken: true,
                    EndpointUrl: null,
                    ApiVersion: null,
                    Models: ["gpt-5"],
                    UpdatedBy: null,
                    UpdatedAtUtc: null)
            }
        };
        var controller = CreateController(repository);

        var result = await controller.ListProviderModels(CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var models = ok.Value.ShouldBeAssignableTo<IReadOnlyList<LlmProviderModelResponse>>();
        models.ShouldNotBeNull();
        models.ShouldContain(model => model.Provider == "anthropic" && model.Model == "claude-opus-4-1");
        models.ShouldContain(model => model.Provider == "openai" && model.Model == "gpt-5");
        models.ShouldNotContain(model => model.Provider == "lmstudio");
    }

    [Fact]
    public async Task GetAnalysis_ReturnsResolvedFallbackValues()
    {
        var repository = new RecordingLlmConfigRepository();
        var controller = CreateController(
            repository,
            analysisSettingsResolver: new RecordingAnalysisSettingsResolver(
                new LlmAnalysisRuntimeConfig(
                    "openai",
                    "gpt-fallback",
                    100,
                    200,
                    300,
                    4,
                    500,
                    UpdatedBy: null,
                    UpdatedAtUtc: null,
                    HasDbConfig: false)));

        var result = await controller.GetAnalysis(CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<LlmAnalysisResponse>();
        response.DefaultProvider.ShouldBe("openai");
        response.DefaultModel.ShouldBe("gpt-fallback");
        response.MaxParallelAnalyses.ShouldBe(4);
    }

    [Fact]
    public async Task UpdateAnalysis_ValidationFailure_ReturnsFieldKeyed422()
    {
        var controller = CreateController(
            new RecordingLlmConfigRepository(),
            analysisSettingsResolver: new RecordingAnalysisSettingsResolver(),
            catalogValidator: new RecordingCatalogValidator(
                LlmCatalogValidationResult.Failure("default_model", "model missing is not in the configured model list for anthropic")));

        var result = await controller.UpdateAnalysis(
            new LlmAnalysisWriteRequest("anthropic", "missing", 100, 200, 300, 4, 500),
            CancellationToken.None);

        var objectResult = result.Result.ShouldBeOfType<UnprocessableEntityObjectResult>();
        objectResult.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        var problem = objectResult.Value.ShouldBeOfType<ValidationProblemDetails>();
        problem.Errors["default_model"].ShouldBe(["model missing is not in the configured model list for anthropic"]);
        problem.Extensions.ShouldContainKey("errors");
    }

    [Fact]
    public async Task UpdateAnalysis_PersistsWriteAndInvalidatesAnalysis()
    {
        var repository = new RecordingLlmConfigRepository();
        var invalidator = new LlmConfigInvalidator();
        var invalidated = false;
        invalidator.AnalysisChanged += () => invalidated = true;
        var resolver = new RepositoryBackedAnalysisSettingsResolver(repository);
        var controller = CreateController(
            repository,
            invalidator,
            analysisSettingsResolver: resolver,
            catalogValidator: new RecordingCatalogValidator(LlmCatalogValidationResult.Success));

        var result = await controller.UpdateAnalysis(
            new LlmAnalysisWriteRequest(" Anthropic ", " claude-sonnet-4-6 ", 1000, 2000, 3000, 4, 5000),
            CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<LlmAnalysisResponse>();
        response.DefaultProvider.ShouldBe("Anthropic");
        response.DefaultModel.ShouldBe("claude-sonnet-4-6");
        repository.LastAnalysisWrite.ShouldNotBeNull();
        repository.LastAnalysisWrite.UpdatedBy.ShouldBe("Michael");
        invalidated.ShouldBeTrue();
    }

    [Fact]
    public async Task GetReview_ReturnsResolvedFallbackValues()
    {
        var repository = new RecordingLlmConfigRepository();
        var controller = CreateController(
            repository,
            reviewSettingsResolver: new RecordingReviewSettingsResolver(
                new LlmReviewRuntimeConfig(
                    "openai",
                    "gpt-review",
                    7,
                    8000,
                    3,
                    9,
                    UpdatedBy: null,
                    UpdatedAtUtc: null,
                    HasDbConfig: false)));

        var result = await controller.GetReview(CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<LlmReviewResponse>();
        response.DefaultProvider.ShouldBe("openai");
        response.DefaultModel.ShouldBe("gpt-review");
        response.MaxFilesToInspect.ShouldBe(7);
    }

    [Fact]
    public async Task UpdateReview_ValidationFailure_ReturnsFieldKeyed422()
    {
        var controller = CreateController(
            new RecordingLlmConfigRepository(),
            reviewSettingsResolver: new RecordingReviewSettingsResolver(),
            catalogValidator: new RecordingCatalogValidator(
                LlmCatalogValidationResult.Failure("default_model", "model missing is not in the configured model list for anthropic")));

        var result = await controller.UpdateReview(
            new LlmReviewWriteRequest("anthropic", "missing", 10, 12000, 4, 20),
            CancellationToken.None);

        var objectResult = result.Result.ShouldBeOfType<UnprocessableEntityObjectResult>();
        objectResult.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        var problem = objectResult.Value.ShouldBeOfType<ValidationProblemDetails>();
        problem.Errors["default_model"].ShouldBe(["model missing is not in the configured model list for anthropic"]);
        problem.Extensions.ShouldContainKey("errors");
    }

    [Fact]
    public async Task UpdateReview_PersistsWriteAndInvalidatesReview()
    {
        var repository = new RecordingLlmConfigRepository();
        var invalidator = new LlmConfigInvalidator();
        var invalidated = false;
        invalidator.ReviewChanged += () => invalidated = true;
        var resolver = new RepositoryBackedReviewSettingsResolver(repository);
        var controller = CreateController(
            repository,
            invalidator,
            reviewSettingsResolver: resolver,
            catalogValidator: new RecordingCatalogValidator(LlmCatalogValidationResult.Success));

        var result = await controller.UpdateReview(
            new LlmReviewWriteRequest(" Anthropic ", " claude-sonnet-4-6 ", 11, 22000, 5, 13),
            CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<LlmReviewResponse>();
        response.DefaultProvider.ShouldBe("Anthropic");
        response.DefaultModel.ShouldBe("claude-sonnet-4-6");
        repository.LastReviewWrite.ShouldNotBeNull();
        repository.LastReviewWrite.UpdatedBy.ShouldBe("Michael");
        invalidated.ShouldBeTrue();
    }

    [Fact]
    public async Task GetAssistant_ReturnsResolvedFallbackValues()
    {
        var repository = new RecordingLlmConfigRepository();
        var controller = CreateController(
            repository,
            assistantSettingsResolver: new RecordingAssistantSettingsResolver(
                new LlmAssistantRuntimeConfig(
                    "openai",
                    "gpt-assistant",
                    7000,
                    8,
                    UpdatedBy: null,
                    UpdatedAtUtc: null,
                    HasDbConfig: false)));

        var result = await controller.GetAssistant(CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<LlmAssistantResponse>();
        response.DefaultProvider.ShouldBe("openai");
        response.DefaultModel.ShouldBe("gpt-assistant");
        response.MaxTokens.ShouldBe(7000);
        response.MaxTurns.ShouldBe(8);
    }

    [Fact]
    public async Task UpdateAssistant_ValidationFailure_ReturnsFieldKeyed422()
    {
        var controller = CreateController(
            new RecordingLlmConfigRepository(),
            assistantSettingsResolver: new RecordingAssistantSettingsResolver(),
            catalogValidator: new RecordingCatalogValidator(
                LlmCatalogValidationResult.Failure("default_model", "model missing is not in the configured model list for anthropic")));

        var result = await controller.UpdateAssistant(
            new LlmAssistantWriteRequest("anthropic", "missing", 7000, 8),
            CancellationToken.None);

        var objectResult = result.Result.ShouldBeOfType<UnprocessableEntityObjectResult>();
        objectResult.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        var problem = objectResult.Value.ShouldBeOfType<ValidationProblemDetails>();
        problem.Errors["default_model"].ShouldBe(["model missing is not in the configured model list for anthropic"]);
        problem.Extensions.ShouldContainKey("errors");
    }

    [Fact]
    public async Task UpdateAssistant_PersistsWriteAndInvalidatesAssistant()
    {
        var repository = new RecordingLlmConfigRepository();
        var invalidator = new LlmConfigInvalidator();
        var invalidated = false;
        invalidator.AssistantChanged += () => invalidated = true;
        var resolver = new RepositoryBackedAssistantSettingsResolver(repository);
        var controller = CreateController(
            repository,
            invalidator,
            assistantSettingsResolver: resolver,
            catalogValidator: new RecordingCatalogValidator(LlmCatalogValidationResult.Success));

        var result = await controller.UpdateAssistant(
            new LlmAssistantWriteRequest(" Anthropic ", " claude-sonnet-4-6 ", 9000, 6),
            CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<LlmAssistantResponse>();
        response.DefaultProvider.ShouldBe("Anthropic");
        response.DefaultModel.ShouldBe("claude-sonnet-4-6");
        repository.LastAssistantWrite.ShouldNotBeNull();
        repository.LastAssistantWrite.UpdatedBy.ShouldBe("Michael");
        invalidated.ShouldBeTrue();
    }

    private static LlmProvidersController CreateController(
        RecordingLlmConfigRepository repository,
        ILlmConfigInvalidator? invalidator = null,
        IDbBackedAnalysisSettingsResolver? analysisSettingsResolver = null,
        IDbBackedReviewSettingsResolver? reviewSettingsResolver = null,
        IDbBackedAssistantSettingsResolver? assistantSettingsResolver = null,
        ILlmCatalogValidator? catalogValidator = null)
    {
        return new LlmProvidersController(
            repository,
            invalidator ?? new LlmConfigInvalidator(),
            analysisSettingsResolver,
            reviewSettingsResolver,
            assistantSettingsResolver,
            catalogValidator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("preferred_username", "Michael")
                    ], "test"))
                }
            }
        };
    }

    private sealed class RecordingLlmConfigRepository : ILlmConfigRepository
    {
        public Dictionary<string, LlmProviderConfig> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public LlmProviderWrite? LastWrite { get; private set; }
        public LlmAnalysisWrite? LastAnalysisWrite { get; private set; }
        public LlmReviewWrite? LastReviewWrite { get; private set; }
        public LlmAssistantWrite? LastAssistantWrite { get; private set; }
        public LlmAnalysisConfig? Analysis { get; private set; }
        public LlmReviewConfig? Review { get; private set; }
        public LlmAssistantConfig? Assistant { get; private set; }

        public Task<LlmProviderConfig?> GetProviderAsync(string providerKey, CancellationToken ct = default) =>
            Task.FromResult(Providers.GetValueOrDefault(providerKey));

        public Task<string?> GetProviderTokenAsync(string providerKey, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task SetProviderAsync(LlmProviderWrite write, CancellationToken ct = default)
        {
            LastWrite = write;
            Providers[write.ProviderKey] = new LlmProviderConfig(
                write.ProviderKey,
                HasToken: write.Token?.Action == LlmProviderTokenActionKind.Replace,
                EndpointUrl: write.EndpointUrl?.Trim(),
                ApiVersion: write.ApiVersion?.Trim(),
                Models: write.Models ?? [],
                UpdatedBy: write.UpdatedBy,
                UpdatedAtUtc: DateTime.UtcNow);
            return Task.CompletedTask;
        }

        public Task<LlmAnalysisConfig?> GetAnalysisAsync(CancellationToken ct = default) =>
            Task.FromResult(Analysis);

        public Task SetAnalysisAsync(LlmAnalysisWrite write, CancellationToken ct = default)
        {
            LastAnalysisWrite = write;
            Analysis = new LlmAnalysisConfig(
                write.DefaultProvider.Trim(),
                write.DefaultModel.Trim(),
                write.MaxTokensPerAnalysis,
                write.MaxTokensPerSynthesis,
                write.MaxFileSizeKb,
                write.MaxParallelAnalyses,
                write.MaxSourceChars,
                write.UpdatedBy,
                DateTime.UtcNow);
            return Task.CompletedTask;
        }

        public Task<LlmReviewConfig?> GetReviewAsync(CancellationToken ct = default) =>
            Task.FromResult(Review);

        public Task SetReviewAsync(LlmReviewWrite write, CancellationToken ct = default)
        {
            LastReviewWrite = write;
            Review = new LlmReviewConfig(
                write.DefaultProvider.Trim(),
                write.DefaultModel.Trim(),
                write.MaxFilesToInspect,
                write.MaxSourceCharsPerFile,
                write.MaxInspectionPasses,
                write.MaxFindings,
                write.UpdatedBy,
                DateTime.UtcNow);
            return Task.CompletedTask;
        }

        public Task<LlmAssistantConfig?> GetAssistantAsync(CancellationToken ct = default) =>
            Task.FromResult(Assistant);

        public Task SetAssistantAsync(LlmAssistantWrite write, CancellationToken ct = default)
        {
            LastAssistantWrite = write;
            Assistant = new LlmAssistantConfig(
                write.DefaultProvider.Trim(),
                write.DefaultModel.Trim(),
                write.MaxTokens,
                write.MaxTurns,
                write.UpdatedBy,
                DateTime.UtcNow);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAnalysisSettingsResolver(
        LlmAnalysisRuntimeConfig? config = null) : IDbBackedAnalysisSettingsResolver
    {
        public Task<LlmAnalysisRuntimeConfig> GetAnalysisAsync(CancellationToken ct = default) =>
            Task.FromResult(config ?? new LlmAnalysisRuntimeConfig(
                "anthropic",
                "claude-sonnet-4-6",
                100,
                200,
                300,
                4,
                500,
                UpdatedBy: null,
                UpdatedAtUtc: null,
                HasDbConfig: false));
    }

    private sealed class RepositoryBackedAnalysisSettingsResolver(
        RecordingLlmConfigRepository repository) : IDbBackedAnalysisSettingsResolver
    {
        public async Task<LlmAnalysisRuntimeConfig> GetAnalysisAsync(CancellationToken ct = default)
        {
            var config = await repository.GetAnalysisAsync(ct);
            config.ShouldNotBeNull();
            return new LlmAnalysisRuntimeConfig(
                config.DefaultProvider ?? "",
                config.DefaultModel ?? "",
                config.MaxTokensPerAnalysis ?? 0,
                config.MaxTokensPerSynthesis ?? 0,
                config.MaxFileSizeKb ?? 0,
                config.MaxParallelAnalyses ?? 0,
                config.MaxSourceChars ?? 0,
                config.UpdatedBy,
                config.UpdatedAtUtc,
                HasDbConfig: true);
        }
    }

    private sealed class RecordingReviewSettingsResolver(
        LlmReviewRuntimeConfig? config = null) : IDbBackedReviewSettingsResolver
    {
        public Task<LlmReviewRuntimeConfig> GetReviewAsync(CancellationToken ct = default) =>
            Task.FromResult(config ?? new LlmReviewRuntimeConfig(
                "anthropic",
                "claude-sonnet-4-6",
                25,
                12000,
                4,
                20,
                UpdatedBy: null,
                UpdatedAtUtc: null,
                HasDbConfig: false));
    }

    private sealed class RepositoryBackedReviewSettingsResolver(
        RecordingLlmConfigRepository repository) : IDbBackedReviewSettingsResolver
    {
        public async Task<LlmReviewRuntimeConfig> GetReviewAsync(CancellationToken ct = default)
        {
            var config = await repository.GetReviewAsync(ct);
            config.ShouldNotBeNull();
            return new LlmReviewRuntimeConfig(
                config.DefaultProvider ?? "",
                config.DefaultModel ?? "",
                config.MaxFilesToInspect ?? 0,
                config.MaxSourceCharsPerFile ?? 0,
                config.MaxInspectionPasses ?? 0,
                config.MaxFindings ?? 0,
                config.UpdatedBy,
                config.UpdatedAtUtc,
                HasDbConfig: true);
        }
    }

    private sealed class RecordingAssistantSettingsResolver(
        LlmAssistantRuntimeConfig? config = null) : IDbBackedAssistantSettingsResolver
    {
        public Task<LlmAssistantRuntimeConfig> GetAssistantAsync(CancellationToken ct = default) =>
            Task.FromResult(config ?? new LlmAssistantRuntimeConfig(
                "anthropic",
                "claude-sonnet-4-6",
                10000,
                10,
                UpdatedBy: null,
                UpdatedAtUtc: null,
                HasDbConfig: false));
    }

    private sealed class RepositoryBackedAssistantSettingsResolver(
        RecordingLlmConfigRepository repository) : IDbBackedAssistantSettingsResolver
    {
        public async Task<LlmAssistantRuntimeConfig> GetAssistantAsync(CancellationToken ct = default)
        {
            var config = await repository.GetAssistantAsync(ct);
            config.ShouldNotBeNull();
            return new LlmAssistantRuntimeConfig(
                config.DefaultProvider ?? "",
                config.DefaultModel ?? "",
                config.MaxTokens ?? 0,
                config.MaxTurns ?? 0,
                config.UpdatedBy,
                config.UpdatedAtUtc,
                HasDbConfig: true);
        }
    }

    private sealed class RecordingCatalogValidator(
        LlmCatalogValidationResult result) : ILlmCatalogValidator
    {
        public Task<LlmCatalogValidationResult> ValidateProviderModelAsync(
            string? provider,
            string? model,
            CancellationToken ct = default) =>
            Task.FromResult(result);

        public Task EnsureProviderModelAsync(
            string? provider,
            string? model,
            CancellationToken ct = default)
        {
            if (!result.IsValid)
            {
                throw new LlmCatalogValidationException(result);
            }

            return Task.CompletedTask;
        }
    }
}
