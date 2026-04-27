using System.Collections.ObjectModel;
using CodeGraph.Data;

namespace CodeGraph.Data.MariaDb;

public static class LlmConfigKeys
{
    public const string AnthropicProvider = LlmProviderKeys.Anthropic;
    public const string OpenAiProvider = LlmProviderKeys.OpenAi;
    public const string LmStudioProvider = LlmProviderKeys.LmStudio;

    public static readonly IReadOnlyList<string> Providers = LlmProviderKeys.All;

    public static readonly IReadOnlyList<string> AnalysisKeys =
    [
        AnalysisDefaultProvider,
        AnalysisDefaultModel,
        AnalysisMaxTokensPerAnalysis,
        AnalysisMaxTokensPerSynthesis,
        AnalysisMaxFileSizeKb,
        AnalysisMaxParallelAnalyses,
        AnalysisMaxSourceChars
    ];

    public static readonly IReadOnlyList<string> ReviewKeys =
    [
        ReviewDefaultProvider,
        ReviewDefaultModel,
        ReviewMaxFilesToInspect,
        ReviewMaxSourceCharsPerFile,
        ReviewMaxInspectionPasses,
        ReviewMaxFindings
    ];

    public static readonly IReadOnlyList<string> AssistantKeys =
    [
        AssistantDefaultProvider,
        AssistantDefaultModel,
        AssistantMaxTokens,
        AssistantMaxTurns
    ];

    public static readonly IReadOnlySet<string> KnownKeys = new ReadOnlySet<string>(
        Providers
            .SelectMany(provider => new[]
            {
                ProviderEndpointUrl(provider),
                ProviderApiVersion(provider),
                ProviderTokenEncrypted(provider)
            })
            .Concat(AnalysisKeys)
            .Concat(ReviewKeys)
            .Concat(AssistantKeys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    public const string AnalysisDefaultProvider = "analysis.default_provider";
    public const string AnalysisDefaultModel = "analysis.default_model";
    public const string AnalysisMaxTokensPerAnalysis = "analysis.max_tokens_per_analysis";
    public const string AnalysisMaxTokensPerSynthesis = "analysis.max_tokens_per_synthesis";
    public const string AnalysisMaxFileSizeKb = "analysis.max_file_size_kb";
    public const string AnalysisMaxParallelAnalyses = "analysis.max_parallel_analyses";
    public const string AnalysisMaxSourceChars = "analysis.max_source_chars";

    public const string ReviewDefaultProvider = "review.default_provider";
    public const string ReviewDefaultModel = "review.default_model";
    public const string ReviewMaxFilesToInspect = "review.max_files_to_inspect";
    public const string ReviewMaxSourceCharsPerFile = "review.max_source_chars_per_file";
    public const string ReviewMaxInspectionPasses = "review.max_inspection_passes";
    public const string ReviewMaxFindings = "review.max_findings";

    public const string AssistantDefaultProvider = "assistant.default_provider";
    public const string AssistantDefaultModel = "assistant.default_model";
    public const string AssistantMaxTokens = "assistant.max_tokens";
    public const string AssistantMaxTurns = "assistant.max_turns";

    public static string ProviderEndpointUrl(string providerKey) => $"provider.{NormalizeProvider(providerKey)}.endpoint_url";
    public static string ProviderApiVersion(string providerKey) => $"provider.{NormalizeProvider(providerKey)}.api_version";
    public static string ProviderTokenEncrypted(string providerKey) => $"provider.{NormalizeProvider(providerKey)}.token_encrypted";

    public static bool IsKnownProvider(string providerKey) => LlmProviderKeys.IsKnown(providerKey);

    public static string RequireKnownProvider(string providerKey)
    {
        var normalized = NormalizeProvider(providerKey);
        if (!IsKnownProvider(normalized))
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerKey),
                providerKey,
                $"Unknown LLM provider '{providerKey}'. Expected one of: {string.Join(", ", Providers)}.");
        }

        return normalized;
    }

    public static void RequireKnownKey(string key)
    {
        if (!KnownKeys.Contains(key))
        {
            throw new ArgumentOutOfRangeException(nameof(key), key, $"Unknown LLM config key '{key}'.");
        }
    }

    private static string NormalizeProvider(string providerKey) => LlmProviderKeys.Normalize(providerKey);
}
