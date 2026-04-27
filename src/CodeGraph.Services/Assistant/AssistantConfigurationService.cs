using CodeGraph.Models.Responses;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Assistant;

public class AssistantConfigurationService(
    IOptions<AnalysisOptions> optionsAccessor,
    IDbBackedAssistantSettingsResolver? assistantSettingsResolver = null,
    IDbBackedLlmProviderConfigResolver? providerConfigResolver = null) : IAssistantConfigurationService
{
    private readonly AnalysisOptions options = optionsAccessor.Value;

    public async Task<AssistantConfigurationResponse> GetConfigurationAsync(CancellationToken ct = default)
    {
        var assistantConfig = await ResolveAssistantConfigAsync(ct);
        var providers = await BuildProvidersAsync(assistantConfig, ct);
        var defaultProvider = ResolveName(assistantConfig.DefaultProvider, options.DefaultProvider, "anthropic");
        var defaultModel = providers
            .FirstOrDefault(provider => string.Equals(provider.Name, defaultProvider, StringComparison.OrdinalIgnoreCase))
            ?.DefaultModel
            ?? assistantConfig.DefaultModel;

        return new AssistantConfigurationResponse(
            defaultProvider,
            defaultModel,
            providers,
            new IndexingConfigurationResponse(
                ResolveName(options.DefaultProvider, "anthropic"),
                FirstNonEmpty(options.Model, ResolveIndexingModel())));
    }

    private async Task<IReadOnlyList<AssistantProviderOptionResponse>> BuildProvidersAsync(
        LlmAssistantRuntimeConfig assistantConfig,
        CancellationToken ct)
    {
        var configuredDefault = ResolveName(assistantConfig.DefaultProvider, options.DefaultProvider, "anthropic");

        var providers = new List<AssistantProviderOptionResponse>
        {
            await BuildProviderAsync("anthropic", "Anthropic", assistantConfig, ct),
            await BuildProviderAsync("openai", "OpenAI", assistantConfig, ct),
            await BuildProviderAsync("lmstudio", "LM Studio", assistantConfig, ct)
        };

        if (!providers.Any(provider => string.Equals(provider.Name, configuredDefault, StringComparison.OrdinalIgnoreCase)))
            providers.Add(BuildProvider(configuredDefault, configuredDefault, assistantConfig.DefaultModel, [assistantConfig.DefaultModel]));

        return providers
            .Where(provider => !string.IsNullOrWhiteSpace(provider.DefaultModel) ||
                               string.Equals(provider.Name, configuredDefault, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<AssistantProviderOptionResponse> BuildProviderAsync(
        string name,
        string displayName,
        LlmAssistantRuntimeConfig assistantConfig,
        CancellationToken ct)
    {
        var providerConfig = await ResolveProviderConfigAsync(name, ct);
        var defaultModel = string.Equals(name, assistantConfig.DefaultProvider, StringComparison.OrdinalIgnoreCase)
            ? assistantConfig.DefaultModel
            : providerConfig.Model;
        var models = providerConfig.Models.Count == 0 && !string.IsNullOrWhiteSpace(defaultModel)
            ? [defaultModel]
            : providerConfig.Models;

        return BuildProvider(name, displayName, defaultModel, models);
    }

    private static AssistantProviderOptionResponse BuildProvider(
        string name,
        string displayName,
        string defaultModel,
        IReadOnlyList<string> models)
    {
        var resolvedModels = models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AssistantProviderOptionResponse(name, displayName, defaultModel, resolvedModels);
    }

    private string ResolveIndexingModel()
    {
        return ResolveName(options.DefaultProvider, "anthropic") switch
        {
            "openai" => FirstNonEmpty(options.Model, options.OpenAi.Model),
            "lmstudio" => FirstNonEmpty(options.Model, options.LmStudio.Model),
            _ => FirstNonEmpty(options.Model)
        };
    }

    private static string ResolveName(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim().ToLowerInvariant() ?? "";

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private Task<LlmAssistantRuntimeConfig> ResolveAssistantConfigAsync(CancellationToken ct) =>
        assistantSettingsResolver?.GetAssistantAsync(ct)
        ?? Task.FromResult(LlmAssistantRuntimeConfig.FromOptions(options));

    private Task<LlmProviderRuntimeConfig> ResolveProviderConfigAsync(string provider, CancellationToken ct) =>
        providerConfigResolver?.GetProviderAsync(provider, ct)
        ?? Task.FromResult(LlmProviderRuntimeConfig.FromOptions(provider, options));
}
