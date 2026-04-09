namespace CodeGraph.Services.Configuration;

public class AnalysisOptions
{
    public string DefaultProvider { get; set; } = "anthropic";
    public string Model { get; set; } = "claude-sonnet-4-6";
    public int MaxTokensPerAnalysis { get; set; } = 128_000;
    public int MaxTokensPerSynthesis { get; set; } = 128_000;
    public int MaxFileSizeKb { get; set; } = 1024;
    public int MaxParallelAnalyses { get; set; } = 5;

    public AssistantOptions Assistant { get; set; } = new();

    /// <summary>Legacy compatibility shim for existing assistant token bindings.</summary>
    public int AssistantMaxTokens
    {
        get => Assistant.MaxTokens;
        set => Assistant.MaxTokens = value;
    }

    /// <summary>Legacy compatibility shim for existing assistant turn bindings.</summary>
    public int AssistantMaxTurns
    {
        get => Assistant.MaxTurns;
        set => Assistant.MaxTurns = value;
    }

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

    /// <summary>
    /// Git author/committer name used for automatic CODEGRAPH.md commits.
    /// </summary>
    public string AutoCommitAuthorName { get; set; } = "CodeGraph";

    /// <summary>
    /// Git author/committer email used for automatic CODEGRAPH.md commits.
    /// </summary>
    public string AutoCommitAuthorEmail { get; set; } = "codegraph@localhost";

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

public class AssistantOptions
{
    public string Provider { get; set; } = "anthropic";
    public string Model { get; set; } = "";
    public int MaxTokens { get; set; } = 10000;
    public int MaxTurns { get; set; } = 10;
    public AssistantAnthropicOptions Anthropic { get; set; } = new();
    public AssistantOpenAiCompatibleOptions OpenAi { get; set; } = new();
    public AssistantOpenAiCompatibleOptions Local { get; set; } = new();
}

public class AssistantAnthropicOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}

public class AssistantOpenAiCompatibleOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ChatCompletionsPath { get; set; } = "";
    public string? Organization { get; set; }
    public string? Project { get; set; }
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
    public int MaxSourceChars { get; set; } = 32_000;
    public int MaxPromptNodes { get; set; } = 80;
    public int MaxRelationshipTargetsPerType { get; set; } = 8;
}
