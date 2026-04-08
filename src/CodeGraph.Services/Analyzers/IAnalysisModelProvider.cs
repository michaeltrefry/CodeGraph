namespace CodeGraph.Services.Analyzers;

public interface IAnalysisModelProvider
{
    string ProviderName { get; }
    AnalysisProviderCapabilities Capabilities { get; }

    Task<AnalysisTextResponse> ExecuteAsync(
        AnalysisPrompt prompt,
        AnalysisRequestOptions request,
        CancellationToken ct = default);

    Task<AnalysisBatchSubmissionResult> SubmitBatchAsync(
        IReadOnlyList<AnalysisBatchRequestItem> items,
        AnalysisRequestOptions request,
        CancellationToken ct = default);

    Task<AnalysisBatchStatusResult> GetBatchStatusAsync(
        string batchId,
        CancellationToken ct = default);

    Task<IReadOnlyList<AnalysisBatchItemResult>> GetBatchResultsAsync(
        string batchId,
        IReadOnlyList<string>? requestIds = null,
        CancellationToken ct = default);
}

public interface IAnalysisProviderRegistry
{
    IAnalysisModelProvider GetProvider(string? providerName = null);
}

public sealed record AnalysisPrompt(string SystemPrompt, string UserPrompt);

public sealed record AnalysisRequestOptions(
    string? Model = null,
    int? MaxTokens = null);

public sealed record AnalysisTextResponse(
    string Text,
    string ModelUsed,
    string ProviderName);

public sealed record AnalysisProviderCapabilities(
    bool SupportsBatch,
    bool SupportsStructuredJson,
    bool SupportsStreaming,
    bool SupportsLargeContext,
    int? MaxContextTokens = null);

public sealed record AnalysisBatchRequestItem(
    string CustomId,
    AnalysisPrompt Prompt);

public sealed record AnalysisBatchRequestPayload(
    AnalysisPrompt Prompt,
    AnalysisRequestOptions Request);

public sealed record AnalysisBatchSubmissionResult(
    string BatchId,
    string ProcessingStatus);

public sealed record AnalysisBatchStatusResult(
    string BatchId,
    string ProcessingStatus,
    bool IsCompleted);

public sealed record AnalysisBatchItemResult(
    string CustomId,
    string Status,
    string? Text,
    string? ModelUsed);
