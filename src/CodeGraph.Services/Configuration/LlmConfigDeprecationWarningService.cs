using CodeGraph.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Configuration;

public interface ILlmConfigDeprecationWarningService
{
    Task LogWarningsAsync(CancellationToken ct = default);
}

public sealed class LlmConfigDeprecationWarningService(
    IServiceScopeFactory scopeFactory,
    IOptions<AnalysisOptions> optionsAccessor,
    ILogger<LlmConfigDeprecationWarningService> logger) : ILlmConfigDeprecationWarningService
{
    public async Task LogWarningsAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetService<ILlmConfigRepository>();
        if (repository is null)
        {
            return;
        }

        try
        {
            var options = optionsAccessor.Value;
            var defaults = new AnalysisOptions();
            var configs = await LoadConfigsAsync(repository, ct);

            foreach (var warning in BuildWarnings(options, defaults, configs))
            {
                logger.LogWarning(
                    "llm.config.deprecation: LLM setting '{SettingKey}' in section '{Section}' is sourced from appsettings; configure it on the LLM Configuration admin page to enable runtime updates without redeploy.",
                    warning.SettingKey,
                    warning.Section);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "llm.config.deprecation: Failed to inspect LLM appsettings fallback usage.");
        }
    }

    private static async Task<LlmConfigSnapshot> LoadConfigsAsync(ILlmConfigRepository repository, CancellationToken ct)
    {
        var providers = new Dictionary<string, LlmProviderConfig?>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in LlmProviderKeys.All)
        {
            providers[provider] = await repository.GetProviderAsync(provider, ct);
        }

        return new LlmConfigSnapshot(
            providers,
            await repository.GetAnalysisAsync(ct),
            await repository.GetReviewAsync(ct),
            await repository.GetAssistantAsync(ct));
    }

    private static IEnumerable<LlmConfigDeprecationWarning> BuildWarnings(
        AnalysisOptions options,
        AnalysisOptions defaults,
        LlmConfigSnapshot configs)
    {
        foreach (var warning in ProviderWarnings(options, defaults, configs))
        {
            yield return warning;
        }

        foreach (var warning in AnalysisWarnings(options, defaults, configs.Analysis))
        {
            yield return warning;
        }

        foreach (var warning in ReviewWarnings(options, defaults, configs.Review))
        {
            yield return warning;
        }

        foreach (var warning in AssistantWarnings(options, defaults, configs.Assistant))
        {
            yield return warning;
        }
    }

    private static IEnumerable<LlmConfigDeprecationWarning> ProviderWarnings(
        AnalysisOptions options,
        AnalysisOptions defaults,
        LlmConfigSnapshot configs)
    {
        foreach (var warning in ProviderTokenWarning(LlmProviderKeys.Anthropic, options.Anthropic.ApiKey, configs))
        {
            yield return warning;
        }

        foreach (var warning in ProviderTokenWarning(LlmProviderKeys.OpenAi, options.OpenAi.ApiKey, configs))
        {
            yield return warning;
        }

        foreach (var warning in ProviderTokenWarning(LlmProviderKeys.LmStudio, options.LmStudio.ApiKey, configs))
        {
            yield return warning;
        }

        if (IsCustomized(options.Anthropic.Version, defaults.Anthropic.Version)
            && string.IsNullOrWhiteSpace(GetProvider(configs, LlmProviderKeys.Anthropic)?.ApiVersion))
        {
            yield return ProviderWarning(LlmProviderKeys.Anthropic, "api_version");
        }

        if (IsCustomized(options.OpenAi.BaseUrl, defaults.OpenAi.BaseUrl)
            && string.IsNullOrWhiteSpace(GetProvider(configs, LlmProviderKeys.OpenAi)?.EndpointUrl))
        {
            yield return ProviderWarning(LlmProviderKeys.OpenAi, "endpoint_url");
        }

        if (IsCustomized(options.LmStudio.BaseUrl, defaults.LmStudio.BaseUrl)
            && string.IsNullOrWhiteSpace(GetProvider(configs, LlmProviderKeys.LmStudio)?.EndpointUrl))
        {
            yield return ProviderWarning(LlmProviderKeys.LmStudio, "endpoint_url");
        }
    }

    private static IEnumerable<LlmConfigDeprecationWarning> ProviderTokenWarning(
        string provider,
        string apiKey,
        LlmConfigSnapshot configs)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield break;
        }

        if (GetProvider(configs, provider)?.HasToken != true)
        {
            yield return ProviderWarning(provider, "token_encrypted");
        }
    }

    private static IEnumerable<LlmConfigDeprecationWarning> AnalysisWarnings(
        AnalysisOptions options,
        AnalysisOptions defaults,
        LlmAnalysisConfig? config)
    {
        if (IsCustomized(options.DefaultProvider, defaults.DefaultProvider) && string.IsNullOrWhiteSpace(config?.DefaultProvider))
        {
            yield return SettingWarning("analysis", "analysis.default_provider");
        }

        if (IsCustomized(options.Model, defaults.Model) && string.IsNullOrWhiteSpace(config?.DefaultModel))
        {
            yield return SettingWarning("analysis", "analysis.default_model");
        }

        if (IsCustomized(options.MaxTokensPerAnalysis, defaults.MaxTokensPerAnalysis) && config?.MaxTokensPerAnalysis is null)
        {
            yield return SettingWarning("analysis", "analysis.max_tokens_per_analysis");
        }

        if (IsCustomized(options.MaxTokensPerSynthesis, defaults.MaxTokensPerSynthesis) && config?.MaxTokensPerSynthesis is null)
        {
            yield return SettingWarning("analysis", "analysis.max_tokens_per_synthesis");
        }

        if (IsCustomized(options.MaxFileSizeKb, defaults.MaxFileSizeKb) && config?.MaxFileSizeKb is null)
        {
            yield return SettingWarning("analysis", "analysis.max_file_size_kb");
        }

        if (IsCustomized(options.MaxParallelAnalyses, defaults.MaxParallelAnalyses) && config?.MaxParallelAnalyses is null)
        {
            yield return SettingWarning("analysis", "analysis.max_parallel_analyses");
        }

        if (IsCustomized(options.MaxSourceChars, defaults.MaxSourceChars) && config?.MaxSourceChars is null)
        {
            yield return SettingWarning("analysis", "analysis.max_source_chars");
        }
    }

    private static IEnumerable<LlmConfigDeprecationWarning> ReviewWarnings(
        AnalysisOptions options,
        AnalysisOptions defaults,
        LlmReviewConfig? config)
    {
        if (IsCustomized(options.DefaultProvider, defaults.DefaultProvider) && string.IsNullOrWhiteSpace(config?.DefaultProvider))
        {
            yield return SettingWarning("review", "review.default_provider");
        }

        if (IsCustomized(options.Review.Model, defaults.Review.Model) && string.IsNullOrWhiteSpace(config?.DefaultModel))
        {
            yield return SettingWarning("review", "review.default_model");
        }

        if (IsCustomized(options.Review.MaxFilesToInspect, defaults.Review.MaxFilesToInspect) && config?.MaxFilesToInspect is null)
        {
            yield return SettingWarning("review", "review.max_files_to_inspect");
        }

        if (IsCustomized(options.Review.MaxSourceCharsPerFile, defaults.Review.MaxSourceCharsPerFile) && config?.MaxSourceCharsPerFile is null)
        {
            yield return SettingWarning("review", "review.max_source_chars_per_file");
        }

        if (IsCustomized(options.Review.MaxInspectionPasses, defaults.Review.MaxInspectionPasses) && config?.MaxInspectionPasses is null)
        {
            yield return SettingWarning("review", "review.max_inspection_passes");
        }

        if (IsCustomized(options.Review.MaxFindings, defaults.Review.MaxFindings) && config?.MaxFindings is null)
        {
            yield return SettingWarning("review", "review.max_findings");
        }
    }

    private static IEnumerable<LlmConfigDeprecationWarning> AssistantWarnings(
        AnalysisOptions options,
        AnalysisOptions defaults,
        LlmAssistantConfig? config)
    {
        if (IsCustomized(options.Assistant.Provider, defaults.Assistant.Provider) && string.IsNullOrWhiteSpace(config?.DefaultProvider))
        {
            yield return SettingWarning("assistant", "assistant.default_provider");
        }

        if (IsCustomized(options.Assistant.Model, defaults.Assistant.Model) && string.IsNullOrWhiteSpace(config?.DefaultModel))
        {
            yield return SettingWarning("assistant", "assistant.default_model");
        }

        if (IsCustomized(options.Assistant.MaxTokens, defaults.Assistant.MaxTokens) && config?.MaxTokens is null)
        {
            yield return SettingWarning("assistant", "assistant.max_tokens");
        }

        if (IsCustomized(options.Assistant.MaxTurns, defaults.Assistant.MaxTurns) && config?.MaxTurns is null)
        {
            yield return SettingWarning("assistant", "assistant.max_turns");
        }
    }

    private static LlmProviderConfig? GetProvider(LlmConfigSnapshot configs, string provider) =>
        configs.Providers.GetValueOrDefault(provider);

    private static LlmConfigDeprecationWarning ProviderWarning(string provider, string setting) =>
        SettingWarning($"provider.{provider}", $"provider.{provider}.{setting}");

    private static LlmConfigDeprecationWarning SettingWarning(string section, string settingKey) =>
        new(section, settingKey);

    private static bool IsCustomized(string? current, string? defaultValue) =>
        !string.Equals(Normalize(current), Normalize(defaultValue), StringComparison.Ordinal);

    private static bool IsCustomized(int current, int defaultValue) => current != defaultValue;

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

    private sealed record LlmConfigSnapshot(
        IReadOnlyDictionary<string, LlmProviderConfig?> Providers,
        LlmAnalysisConfig? Analysis,
        LlmReviewConfig? Review,
        LlmAssistantConfig? Assistant);

    private sealed record LlmConfigDeprecationWarning(string Section, string SettingKey);
}
