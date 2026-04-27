namespace CodeGraph.Data;

public interface ILlmConfigRepository
{
    Task<LlmProviderConfig?> GetProviderAsync(string providerKey, CancellationToken ct = default);
    Task<string?> GetProviderTokenAsync(string providerKey, CancellationToken ct = default);
    Task SetProviderAsync(LlmProviderWrite write, CancellationToken ct = default);

    Task<LlmAnalysisConfig?> GetAnalysisAsync(CancellationToken ct = default);
    Task SetAnalysisAsync(LlmAnalysisWrite write, CancellationToken ct = default);

    Task<LlmReviewConfig?> GetReviewAsync(CancellationToken ct = default);
    Task SetReviewAsync(LlmReviewWrite write, CancellationToken ct = default);

    Task<LlmAssistantConfig?> GetAssistantAsync(CancellationToken ct = default);
    Task SetAssistantAsync(LlmAssistantWrite write, CancellationToken ct = default);
}

public sealed record LlmProviderConfig(
    string ProviderKey,
    bool HasToken,
    string? EndpointUrl,
    string? ApiVersion,
    IReadOnlyList<string> Models,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record LlmProviderWrite(
    string ProviderKey,
    string? EndpointUrl,
    string? ApiVersion,
    IReadOnlyList<string>? Models,
    LlmProviderTokenWrite? Token,
    string? UpdatedBy = null);

public sealed record LlmProviderTokenWrite(
    LlmProviderTokenActionKind Action,
    string? Value = null);

public enum LlmProviderTokenActionKind
{
    Preserve,
    Replace,
    Clear
}

public sealed record LlmAnalysisConfig(
    string? DefaultProvider,
    string? DefaultModel,
    int? MaxTokensPerAnalysis,
    int? MaxTokensPerSynthesis,
    int? MaxFileSizeKb,
    int? MaxParallelAnalyses,
    int? MaxSourceChars,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record LlmAnalysisWrite(
    string DefaultProvider,
    string DefaultModel,
    int MaxTokensPerAnalysis,
    int MaxTokensPerSynthesis,
    int MaxFileSizeKb,
    int MaxParallelAnalyses,
    int MaxSourceChars,
    string? UpdatedBy = null);

public sealed record LlmReviewConfig(
    string? DefaultProvider,
    string? DefaultModel,
    int? MaxFilesToInspect,
    int? MaxSourceCharsPerFile,
    int? MaxInspectionPasses,
    int? MaxFindings,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record LlmReviewWrite(
    string DefaultProvider,
    string DefaultModel,
    int MaxFilesToInspect,
    int MaxSourceCharsPerFile,
    int MaxInspectionPasses,
    int MaxFindings,
    string? UpdatedBy = null);

public sealed record LlmAssistantConfig(
    string? DefaultProvider,
    string? DefaultModel,
    int? MaxTokens,
    int? MaxTurns,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record LlmAssistantWrite(
    string DefaultProvider,
    string DefaultModel,
    int MaxTokens,
    int MaxTurns,
    string? UpdatedBy = null);
