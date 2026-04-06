namespace CodeGraph.Services.Configuration;

public class AnalysisOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-4-6";
    public int MaxTokensPerAnalysis { get; set; } = 128_000;
    public int MaxTokensPerSynthesis { get; set; } = 128_000;
    public int MaxFileSizeKb { get; set; } = 1024;
    public int MaxParallelAnalyses { get; set; } = 5;

    /// <summary>Max tokens for streaming assistant responses.</summary>
    public int AssistantMaxTokens { get; set; } = 10000;

    /// <summary>Max agent loop turns for the streaming assistant.</summary>
    public int AssistantMaxTurns { get; set; } = 10;

    /// <summary>
    /// Maximum characters of source code to include in a batch analysis prompt.
    /// Applies to both convention-based and include-all-source modes.
    /// Default ~128K chars ≈ ~32K tokens, leaving room for the graph + output
    /// within the model's context window.
    /// </summary>
    public int MaxSourceChars { get; set; } = 128_000;

    // Anthropic API constants
    public string BatchApiBaseUrl { get; set; } = "https://api.anthropic.com/v1/messages/batches";
    public string MessagesApiUrl { get; set; } = "https://api.anthropic.com/v1/messages";
    public string AnthropicVersion { get; set; } = "2023-06-01";
}
