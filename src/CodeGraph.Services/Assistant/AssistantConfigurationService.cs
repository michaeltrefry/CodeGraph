using CodeGraph.Models.Responses;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Assistant;

public class AssistantConfigurationService(IOptions<AnalysisOptions> optionsAccessor) : IAssistantConfigurationService
{
    private readonly AnalysisOptions options = optionsAccessor.Value;

    public Task<AssistantConfigurationResponse> GetConfigurationAsync(CancellationToken ct = default)
    {
        var providers = BuildProviders();
        var defaultProvider = ResolveName(options.Assistant.Provider, options.DefaultProvider, "anthropic");
        var defaultModel = providers
            .FirstOrDefault(provider => string.Equals(provider.Name, defaultProvider, StringComparison.OrdinalIgnoreCase))
            ?.DefaultModel
            ?? ResolveAssistantModel(defaultProvider);

        return Task.FromResult(new AssistantConfigurationResponse(
            defaultProvider,
            defaultModel,
            providers,
            new IndexingConfigurationResponse(
                ResolveName(options.DefaultProvider, "anthropic"),
                FirstNonEmpty(options.Model, ResolveIndexingModel()))));
    }

    private IReadOnlyList<AssistantProviderOptionResponse> BuildProviders()
    {
        var configuredDefault = ResolveName(options.Assistant.Provider, options.DefaultProvider, "anthropic");

        var providers = new List<AssistantProviderOptionResponse>
        {
            BuildProvider("anthropic", "Anthropic", ResolveAssistantModel("anthropic")),
            BuildProvider("openai", "OpenAI", ResolveAssistantModel("openai")),
            BuildProvider("local", "Local", ResolveAssistantModel("local"))
        };

        if (!providers.Any(provider => string.Equals(provider.Name, configuredDefault, StringComparison.OrdinalIgnoreCase)))
            providers.Add(BuildProvider(configuredDefault, configuredDefault, ResolveAssistantModel(configuredDefault)));

        return providers
            .Where(provider => !string.IsNullOrWhiteSpace(provider.DefaultModel) ||
                               string.Equals(provider.Name, configuredDefault, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static AssistantProviderOptionResponse BuildProvider(string name, string displayName, string defaultModel)
    {
        var models = string.IsNullOrWhiteSpace(defaultModel)
            ? Array.Empty<string>()
            : new[] { defaultModel };

        return new AssistantProviderOptionResponse(name, displayName, defaultModel, models);
    }

    private string ResolveAssistantModel(string provider) =>
        provider.ToLowerInvariant() switch
        {
            "anthropic" => FirstNonEmpty(options.Assistant.Model, options.Model),
            "openai" => FirstNonEmpty(options.Assistant.Model, options.OpenAi.Model),
            "local" => FirstNonEmpty(options.Assistant.Model, options.Local.Model),
            _ => FirstNonEmpty(options.Assistant.Model, options.Model)
        };

    private string ResolveIndexingModel()
    {
        return ResolveName(options.DefaultProvider, "anthropic") switch
        {
            "openai" => FirstNonEmpty(options.Model, options.OpenAi.Model),
            "gemini" => FirstNonEmpty(options.Model, options.Gemini.Model),
            "local" => FirstNonEmpty(options.Model, options.Local.Model),
            _ => FirstNonEmpty(options.Model)
        };
    }

    private static string ResolveName(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim().ToLowerInvariant() ?? "";

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}
