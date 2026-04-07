using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extensions;

namespace CodeGraph.Services.Analyzers;

public class GeminiAnalysisProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<AnalysisOptions> optionsAccessor,
    ILogger<GeminiAnalysisProvider> logger) : IAnalysisModelProvider
{
    private readonly AnalysisOptions options = optionsAccessor.Value;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> CompletedStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "JOB_STATE_SUCCEEDED",
        "JOB_STATE_FAILED",
        "JOB_STATE_CANCELLED",
        "JOB_STATE_EXPIRED"
    };

    public string ProviderName => "gemini";

    public AnalysisProviderCapabilities Capabilities { get; } =
        new(SupportsBatch: true, SupportsStructuredJson: true, SupportsStreaming: false, SupportsLargeContext: true);

    public async Task<AnalysisTextResponse> ExecuteAsync(
        AnalysisPrompt prompt,
        AnalysisRequestOptions request,
        CancellationToken ct = default)
    {
        var model = ResolveModel(request);
        var body = new GeminiGenerateContentRequest
        {
            SystemInstruction = new GeminiContent
            {
                Parts =
                [
                    new GeminiPart { Text = prompt.SystemPrompt }
                ]
            },
            Contents =
            [
                new GeminiContent
                {
                    Role = "user",
                    Parts =
                    [
                        new GeminiPart { Text = prompt.UserPrompt }
                    ]
                }
            ],
            GenerationConfig = new GeminiGenerationConfig
            {
                ResponseMimeType = "application/json",
                MaxOutputTokens = request.MaxTokens ?? options.MaxTokensPerSynthesis
            }
        };

        var http = httpClientFactory.CreateClient();
        using var response = await http.SendAsync(CreateJsonRequest(
            HttpMethod.Post,
            BuildModelActionUrl(model, "generateContent"),
            body), ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Gemini content generation failed {Status}: {Body}",
                (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var generated = await response.Content.ReadFromJsonAsync<GeminiGenerateContentResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Gemini returned null generateContent response");
        var text = ExtractText(generated);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Gemini returned an empty text response");

        return new AnalysisTextResponse(text, generated.ModelVersion ?? model, ProviderName);
    }

    public async Task<AnalysisBatchSubmissionResult> SubmitBatchAsync(
        IReadOnlyList<AnalysisBatchRequestItem> items,
        AnalysisRequestOptions request,
        CancellationToken ct = default)
    {
        var model = ResolveModel(request);
        var body = new GeminiBatchGenerateContentRequest
        {
            Batch = new GeminiBatch
            {
                DisplayName = $"codegraph-{DateTime.UtcNow:yyyyMMddHHmmss}",
                InlinedRequests = new GeminiInlinedRequests
                {
                    Requests = items.Select(item => new GeminiInlinedRequest
                    {
                        Request = new GeminiGenerateContentRequest
                        {
                            SystemInstruction = new GeminiContent
                            {
                                Parts =
                                [
                                    new GeminiPart { Text = item.Prompt.SystemPrompt }
                                ]
                            },
                            Contents =
                            [
                                new GeminiContent
                                {
                                    Role = "user",
                                    Parts =
                                    [
                                        new GeminiPart { Text = item.Prompt.UserPrompt }
                                    ]
                                }
                            ],
                            GenerationConfig = new GeminiGenerationConfig
                            {
                                ResponseMimeType = "application/json",
                                MaxOutputTokens = request.MaxTokens ?? options.MaxTokensPerAnalysis
                            }
                        },
                        Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["customId"] = item.CustomId
                        }
                    }).ToList()
                }
            }
        };

        var http = httpClientFactory.CreateClient();
        using var response = await http.SendAsync(CreateJsonRequest(
            HttpMethod.Post,
            BuildModelActionUrl(model, "batchGenerateContent"),
            body), ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Gemini batch submission failed {Status}: {Body}",
                (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var created = await response.Content.ReadFromJsonAsync<GeminiBatchResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Gemini returned null batch create response");
        if (string.IsNullOrWhiteSpace(created.Name))
            throw new InvalidOperationException("Gemini returned a batch response without a resource name");

        return new AnalysisBatchSubmissionResult(created.Name, created.State ?? "submitted");
    }

    public async Task<AnalysisBatchStatusResult> GetBatchStatusAsync(string batchId, CancellationToken ct = default)
    {
        var http = httpClientFactory.CreateClient();
        using var response = await http.SendAsync(CreateRequest(
            HttpMethod.Get,
            BuildBatchUrl(batchId)), ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Gemini batch status lookup failed for {BatchId}: {Status} {Body}",
                batchId, (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var status = await response.Content.ReadFromJsonAsync<GeminiBatchResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Gemini returned null batch status response");
        var processingStatus = status.State ?? "unknown";

        return new AnalysisBatchStatusResult(
            status.Name,
            processingStatus,
            CompletedStates.Contains(processingStatus));
    }

    public async Task<IReadOnlyList<AnalysisBatchItemResult>> GetBatchResultsAsync(
        string batchId,
        IReadOnlyList<string>? requestIds = null,
        CancellationToken ct = default)
    {
        var http = httpClientFactory.CreateClient();
        using var response = await http.SendAsync(CreateRequest(
            HttpMethod.Get,
            BuildBatchUrl(batchId)), ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Gemini batch results lookup failed for {BatchId}: {Status} {Body}",
                batchId, (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var batch = await response.Content.ReadFromJsonAsync<GeminiBatchResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Gemini returned null batch results response");
        var inlinedResponses = batch.Output?.InlinedResponses?.InlinedResponses;
        if (inlinedResponses is null || inlinedResponses.Count == 0)
            return [];

        var results = new List<AnalysisBatchItemResult>(inlinedResponses.Count);
        for (var i = 0; i < inlinedResponses.Count; i++)
        {
            var inline = inlinedResponses[i];
            var customId = GetCustomId(inline, requestIds, i);
            var text = inline.Response is null ? null : ExtractText(inline.Response);
            var model = inline.Response?.ModelVersion;
            var status = inline.Error is null && inline.Response is not null
                ? "succeeded"
                : "errored";

            results.Add(new AnalysisBatchItemResult(customId, status, text, model));
        }

        return results;
    }

    private HttpRequestMessage CreateJsonRequest(HttpMethod method, string url, object body)
    {
        var request = CreateRequest(method, url);
        request.Content = JsonContent.Create(body, options: JsonOpts);
        return request;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-goog-api-key", options.Gemini.ApiKey);
        return request;
    }

    private string ResolveModel(AnalysisRequestOptions request)
    {
        var configured = !string.IsNullOrWhiteSpace(options.Gemini.Model)
            ? options.Gemini.Model
            : options.Model;
        return NormalizeModel(request.Model ?? configured);
    }

    private string BuildModelActionUrl(string model, string action)
    {
        var baseUrl = options.Gemini.BaseUrl.TrimEnd('/');
        return $"{baseUrl}/{NormalizeModel(model)}:{action}";
    }

    private string BuildBatchUrl(string batchId)
    {
        var baseUrl = options.Gemini.BaseUrl.TrimEnd('/');
        var relative = batchId.TrimStart('/');
        return $"{baseUrl}/{relative}";
    }

    private static string NormalizeModel(string model)
    {
        return model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? model
            : $"models/{model}";
    }

    private static string GetCustomId(GeminiInlinedResponse response, IReadOnlyList<string>? requestIds, int index)
    {
        if (response.Metadata?.TryGetValue("customId", out var customId) == true &&
            !string.IsNullOrWhiteSpace(customId))
            return customId;

        if (requestIds is not null && index < requestIds.Count && !string.IsNullOrWhiteSpace(requestIds[index]))
            return requestIds[index];

        return $"gemini_{index}";
    }

    private static string? ExtractText(GeminiGenerateContentResponse response)
    {
        return response.Candidates
            .SelectMany(candidate => candidate.Content?.Parts ?? [])
            .Select(part => part.Text?.StripCodeFences())
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    private sealed class GeminiBatchGenerateContentRequest
    {
        [JsonPropertyName("batch")]
        public GeminiBatch Batch { get; set; } = new();
    }

    private sealed class GeminiBatch
    {
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("inlinedRequests")]
        public GeminiInlinedRequests InlinedRequests { get; set; } = new();
    }

    private sealed class GeminiInlinedRequests
    {
        [JsonPropertyName("requests")]
        public List<GeminiInlinedRequest> Requests { get; set; } = [];
    }

    private sealed class GeminiInlinedRequest
    {
        [JsonPropertyName("request")]
        public GeminiGenerateContentRequest Request { get; set; } = new();

        [JsonPropertyName("metadata")]
        public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class GeminiGenerateContentRequest
    {
        [JsonPropertyName("systemInstruction")]
        public GeminiContent? SystemInstruction { get; set; }

        [JsonPropertyName("contents")]
        public List<GeminiContent> Contents { get; set; } = [];

        [JsonPropertyName("generationConfig")]
        public GeminiGenerationConfig? GenerationConfig { get; set; }
    }

    private sealed class GeminiGenerationConfig
    {
        [JsonPropertyName("responseMimeType")]
        public string? ResponseMimeType { get; set; }

        [JsonPropertyName("maxOutputTokens")]
        public int? MaxOutputTokens { get; set; }
    }

    private sealed class GeminiGenerateContentResponse
    {
        [JsonPropertyName("candidates")]
        public List<GeminiCandidate> Candidates { get; set; } = [];

        [JsonPropertyName("modelVersion")]
        public string? ModelVersion { get; set; }
    }

    private sealed class GeminiCandidate
    {
        [JsonPropertyName("content")]
        public GeminiContent? Content { get; set; }
    }

    private sealed class GeminiContent
    {
        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("parts")]
        public List<GeminiPart> Parts { get; set; } = [];
    }

    private sealed class GeminiPart
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private sealed class GeminiBatchResponse
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("output")]
        public GeminiBatchOutput? Output { get; set; }

        [JsonPropertyName("error")]
        public GeminiError? Error { get; set; }
    }

    private sealed class GeminiBatchOutput
    {
        [JsonPropertyName("responsesFile")]
        public string? ResponsesFile { get; set; }

        [JsonPropertyName("inlinedResponses")]
        public GeminiInlinedResponses? InlinedResponses { get; set; }
    }

    private sealed class GeminiInlinedResponses
    {
        [JsonPropertyName("inlinedResponses")]
        public List<GeminiInlinedResponse> InlinedResponses { get; set; } = [];
    }

    private sealed class GeminiInlinedResponse
    {
        [JsonPropertyName("metadata")]
        public Dictionary<string, string>? Metadata { get; set; }

        [JsonPropertyName("response")]
        public GeminiGenerateContentResponse? Response { get; set; }

        [JsonPropertyName("error")]
        public GeminiError? Error { get; set; }
    }

    private sealed class GeminiError
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
