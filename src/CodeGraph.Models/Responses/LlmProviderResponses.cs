namespace CodeGraph.Models.Responses;

public sealed record LlmProviderResponse(
    string Provider,
    bool HasToken,
    string? EndpointUrl,
    string? ApiVersion,
    IReadOnlyList<string> Models,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record LlmProviderModelResponse(
    string Provider,
    string Model);

public sealed record LlmAnalysisResponse(
    string DefaultProvider,
    string DefaultModel,
    int MaxTokensPerAnalysis,
    int MaxTokensPerSynthesis,
    int MaxFileSizeKb,
    int MaxParallelAnalyses,
    int MaxSourceChars,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record LlmReviewResponse(
    string DefaultProvider,
    string DefaultModel,
    int MaxFilesToInspect,
    int MaxSourceCharsPerFile,
    int MaxInspectionPasses,
    int MaxFindings,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record LlmAssistantResponse(
    string DefaultProvider,
    string DefaultModel,
    int MaxTokens,
    int MaxTurns,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);
