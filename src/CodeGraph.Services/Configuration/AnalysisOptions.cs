namespace CodeGraph.Services.Configuration;

public class AnalysisOptions
{
    public string DefaultProvider { get; set; } = "anthropic";
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

    /// <summary>
    /// When true, stage and commit generated CODEGRAPH.md files after synthesis.
    /// </summary>
    public bool AutoCommitDocs { get; set; }

    /// <summary>
    /// When true and AutoCommitDocs is enabled, push the resulting commit to origin.
    /// </summary>
    public bool AutoPushDocs { get; set; }

    /// <summary>
    /// Commit message used when AutoCommitDocs is enabled.
    /// </summary>
    public string AutoCommitMessage { get; set; } = "docs(codegraph): update CODEGRAPH.md";

    public AnthropicAnalysisProviderOptions Anthropic { get; set; } = new();
    public OpenAiAnalysisProviderOptions OpenAi { get; set; } = new();
    public GeminiAnalysisProviderOptions Gemini { get; set; } = new();
    public LocalAnalysisProviderOptions Local { get; set; } = new();

    // Legacy compatibility shims for existing config bindings.
    public string ApiKey
    {
        get => Anthropic.ApiKey;
        set => Anthropic.ApiKey = value;
    }

    public string BatchApiBaseUrl
    {
        get => Anthropic.BatchApiBaseUrl;
        set => Anthropic.BatchApiBaseUrl = value;
    }

    public string MessagesApiUrl
    {
        get => Anthropic.MessagesApiUrl;
        set => Anthropic.MessagesApiUrl = value;
    }

    public string AnthropicVersion
    {
        get => Anthropic.Version;
        set => Anthropic.Version = value;
    }
}

public class AnthropicAnalysisProviderOptions
{
    public string ApiKey { get; set; } = "";
    public string BatchApiBaseUrl { get; set; } = "https://api.anthropic.com/v1/messages/batches";
    public string MessagesApiUrl { get; set; } = "https://api.anthropic.com/v1/messages";
    public string Version { get; set; } = "2023-06-01";
}

public class OpenAiAnalysisProviderOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ChatCompletionsPath { get; set; } = "/chat/completions";
    public string BatchesPath { get; set; } = "/batches";
    public string FilesPath { get; set; } = "/files";
    public string Model { get; set; } = "gpt-5";
    public string? Organization { get; set; }
    public string? Project { get; set; }
}

public class GeminiAnalysisProviderOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    public string Model { get; set; } = "gemini-2.5-flash";
}

public class LocalAnalysisProviderOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "http://localhost:1234/v1";
    public string ChatCompletionsPath { get; set; } = "/chat/completions";
    public string Model { get; set; } = "qwen3";
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxConcurrentRequests { get; set; } = 1;
    public int DirectFallbackMaxAttempts { get; set; } = 3;
    public bool UseJsonObjectResponseFormat { get; set; }
}
