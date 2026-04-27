namespace CodeGraph.Data;

public static class LlmProviderKeys
{
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";
    public const string LmStudio = "lmstudio";

    public static readonly IReadOnlyList<string> All =
    [
        Anthropic,
        OpenAi,
        LmStudio
    ];

    public static bool IsKnown(string providerKey) =>
        All.Contains(Normalize(providerKey), StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string providerKey) =>
        providerKey.Trim().ToLowerInvariant();
}
