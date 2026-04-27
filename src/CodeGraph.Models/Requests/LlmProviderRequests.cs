namespace CodeGraph.Models.Requests;

public sealed record LlmProviderWriteRequest(
    string? EndpointUrl,
    string? ApiVersion,
    IReadOnlyList<string>? Models,
    LlmProviderTokenActionRequest? Token);

public sealed record LlmProviderTokenActionRequest(
    LlmProviderTokenActionKindRequest Action,
    string? Value = null);

public enum LlmProviderTokenActionKindRequest
{
    Preserve,
    Replace,
    Clear
}

public sealed record LlmAnalysisWriteRequest(
    string DefaultProvider,
    string DefaultModel,
    int MaxTokensPerAnalysis,
    int MaxTokensPerSynthesis,
    int MaxFileSizeKb,
    int MaxParallelAnalyses,
    int MaxSourceChars);

public sealed record LlmReviewWriteRequest(
    string DefaultProvider,
    string DefaultModel,
    int MaxFilesToInspect,
    int MaxSourceCharsPerFile,
    int MaxInspectionPasses,
    int MaxFindings);

public sealed record LlmAssistantWriteRequest(
    string DefaultProvider,
    string DefaultModel,
    int MaxTokens,
    int MaxTurns);
