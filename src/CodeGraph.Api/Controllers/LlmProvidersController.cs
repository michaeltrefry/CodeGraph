using CodeGraph.Api.Auth;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CodeGraph.Api.Controllers;

[ApiController]
public sealed class LlmProvidersController(
    ILlmConfigRepository repository,
    ILlmConfigInvalidator invalidator,
    IDbBackedAnalysisSettingsResolver? analysisSettingsResolver = null,
    IDbBackedReviewSettingsResolver? reviewSettingsResolver = null,
    IDbBackedAssistantSettingsResolver? assistantSettingsResolver = null,
    ILlmCatalogValidator? catalogValidator = null) : ControllerBase
{
    [Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
    [HttpGet("api/admin/llm-providers")]
    public async Task<ActionResult<IReadOnlyList<LlmProviderResponse>>> ListProviders(CancellationToken ct)
    {
        var providers = new List<LlmProviderResponse>();
        foreach (var provider in LlmProviderKeys.All)
        {
            providers.Add(MapProvider(provider, await repository.GetProviderAsync(provider, ct)));
        }

        return Ok(providers);
    }

    [Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
    [HttpPut("api/admin/llm-providers/{provider}")]
    public async Task<ActionResult<LlmProviderResponse>> UpdateProvider(
        string provider,
        [FromBody] LlmProviderWriteRequest request,
        CancellationToken ct)
    {
        if (!LlmProviderKeys.IsKnown(provider))
        {
            return BadRequest($"Provider must be one of: {string.Join(", ", LlmProviderKeys.All)}.");
        }

        try
        {
            var normalizedProvider = LlmProviderKeys.Normalize(provider);
            await repository.SetProviderAsync(new LlmProviderWrite(
                normalizedProvider,
                request.EndpointUrl,
                request.ApiVersion,
                request.Models,
                MapToken(request.Token),
                UpdatedBy()), ct);
            invalidator.InvalidateProvider(normalizedProvider);

            return Ok(MapProvider(normalizedProvider, await repository.GetProviderAsync(normalizedProvider, ct)));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [Authorize(Policy = CodeGraphAuthenticationDefaults.UserPolicy)]
    [HttpGet("api/llm-providers/models")]
    public async Task<ActionResult<IReadOnlyList<LlmProviderModelResponse>>> ListProviderModels(CancellationToken ct)
    {
        var models = new List<LlmProviderModelResponse>();
        foreach (var provider in LlmProviderKeys.All)
        {
            var config = await repository.GetProviderAsync(provider, ct);
            if (config is null)
            {
                continue;
            }

            models.AddRange(config.Models.Select(model => new LlmProviderModelResponse(provider, model)));
        }

        return Ok(models);
    }

    [Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
    [HttpGet("api/admin/llm-analysis")]
    public async Task<ActionResult<LlmAnalysisResponse>> GetAnalysis(CancellationToken ct)
    {
        if (analysisSettingsResolver is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Analysis settings resolver is not configured.");
        }

        var config = await analysisSettingsResolver.GetAnalysisAsync(ct);
        return Ok(MapAnalysis(config));
    }

    [Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
    [HttpPut("api/admin/llm-analysis")]
    public async Task<ActionResult<LlmAnalysisResponse>> UpdateAnalysis(
        [FromBody] LlmAnalysisWriteRequest request,
        CancellationToken ct)
    {
        if (catalogValidator is null || analysisSettingsResolver is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "LLM analysis settings services are not configured.");
        }

        var validation = await catalogValidator.ValidateProviderModelAsync(
            request.DefaultProvider,
            request.DefaultModel,
            ct);
        if (!validation.IsValid)
        {
            return ValidationFailed(validation);
        }

        await repository.SetAnalysisAsync(new LlmAnalysisWrite(
            request.DefaultProvider,
            request.DefaultModel,
            request.MaxTokensPerAnalysis,
            request.MaxTokensPerSynthesis,
            request.MaxFileSizeKb,
            request.MaxParallelAnalyses,
            request.MaxSourceChars,
            UpdatedBy()), ct);
        invalidator.InvalidateAnalysis();

        return Ok(MapAnalysis(await analysisSettingsResolver.GetAnalysisAsync(ct)));
    }

    [Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
    [HttpGet("api/admin/llm-review")]
    public async Task<ActionResult<LlmReviewResponse>> GetReview(CancellationToken ct)
    {
        if (reviewSettingsResolver is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Review settings resolver is not configured.");
        }

        var config = await reviewSettingsResolver.GetReviewAsync(ct);
        return Ok(MapReview(config));
    }

    [Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
    [HttpPut("api/admin/llm-review")]
    public async Task<ActionResult<LlmReviewResponse>> UpdateReview(
        [FromBody] LlmReviewWriteRequest request,
        CancellationToken ct)
    {
        if (catalogValidator is null || reviewSettingsResolver is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "LLM review settings services are not configured.");
        }

        var validation = await catalogValidator.ValidateProviderModelAsync(
            request.DefaultProvider,
            request.DefaultModel,
            ct);
        if (!validation.IsValid)
        {
            return ValidationFailed(validation);
        }

        await repository.SetReviewAsync(new LlmReviewWrite(
            request.DefaultProvider,
            request.DefaultModel,
            request.MaxFilesToInspect,
            request.MaxSourceCharsPerFile,
            request.MaxInspectionPasses,
            request.MaxFindings,
            UpdatedBy()), ct);
        invalidator.InvalidateReview();

        return Ok(MapReview(await reviewSettingsResolver.GetReviewAsync(ct)));
    }

    [Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
    [HttpGet("api/admin/llm-assistant")]
    public async Task<ActionResult<LlmAssistantResponse>> GetAssistant(CancellationToken ct)
    {
        if (assistantSettingsResolver is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Assistant settings resolver is not configured.");
        }

        var config = await assistantSettingsResolver.GetAssistantAsync(ct);
        return Ok(MapAssistant(config));
    }

    [Authorize(Policy = CodeGraphAuthenticationDefaults.AdminPolicy)]
    [HttpPut("api/admin/llm-assistant")]
    public async Task<ActionResult<LlmAssistantResponse>> UpdateAssistant(
        [FromBody] LlmAssistantWriteRequest request,
        CancellationToken ct)
    {
        if (catalogValidator is null || assistantSettingsResolver is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "LLM assistant settings services are not configured.");
        }

        var validation = await catalogValidator.ValidateProviderModelAsync(
            request.DefaultProvider,
            request.DefaultModel,
            ct);
        if (!validation.IsValid)
        {
            return ValidationFailed(validation);
        }

        await repository.SetAssistantAsync(new LlmAssistantWrite(
            request.DefaultProvider,
            request.DefaultModel,
            request.MaxTokens,
            request.MaxTurns,
            UpdatedBy()), ct);
        invalidator.InvalidateAssistant();

        return Ok(MapAssistant(await assistantSettingsResolver.GetAssistantAsync(ct)));
    }

    private string? UpdatedBy() => User.GetUsername()?.Trim();

    private static LlmProviderResponse MapProvider(string provider, LlmProviderConfig? config) =>
        new(
            provider,
            config?.HasToken ?? false,
            config?.EndpointUrl,
            config?.ApiVersion,
            config?.Models ?? [],
            config?.UpdatedBy,
            config?.UpdatedAtUtc);

    private static LlmAnalysisResponse MapAnalysis(LlmAnalysisRuntimeConfig config) =>
        new(
            config.DefaultProvider,
            config.DefaultModel,
            config.MaxTokensPerAnalysis,
            config.MaxTokensPerSynthesis,
            config.MaxFileSizeKb,
            config.MaxParallelAnalyses,
            config.MaxSourceChars,
            config.UpdatedBy,
            config.UpdatedAtUtc);

    private static LlmReviewResponse MapReview(LlmReviewRuntimeConfig config) =>
        new(
            config.DefaultProvider,
            config.DefaultModel,
            config.MaxFilesToInspect,
            config.MaxSourceCharsPerFile,
            config.MaxInspectionPasses,
            config.MaxFindings,
            config.UpdatedBy,
            config.UpdatedAtUtc);

    private static LlmAssistantResponse MapAssistant(LlmAssistantRuntimeConfig config) =>
        new(
            config.DefaultProvider,
            config.DefaultModel,
            config.MaxTokens,
            config.MaxTurns,
            config.UpdatedBy,
            config.UpdatedAtUtc);

    private UnprocessableEntityObjectResult ValidationFailed(LlmCatalogValidationResult validation)
    {
        var fields = validation.Errors
            .GroupBy(error => error.Field, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Message).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var problem = new ValidationProblemDetails(fields)
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "LLM catalog validation failed."
        };
        problem.Extensions["errors"] = validation.Errors
            .Select(error => new { field = error.Field, message = error.Message })
            .ToArray();
        return UnprocessableEntity(problem);
    }

    private static LlmProviderTokenWrite? MapToken(LlmProviderTokenActionRequest? token)
    {
        if (token is null)
        {
            return null;
        }

        return new LlmProviderTokenWrite(token.Action switch
        {
            LlmProviderTokenActionKindRequest.Preserve => LlmProviderTokenActionKind.Preserve,
            LlmProviderTokenActionKindRequest.Replace => LlmProviderTokenActionKind.Replace,
            LlmProviderTokenActionKindRequest.Clear => LlmProviderTokenActionKind.Clear,
            _ => throw new ArgumentOutOfRangeException(nameof(token), token.Action, "Unknown token action.")
        }, token.Value);
    }
}
