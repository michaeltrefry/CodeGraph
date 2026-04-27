using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Models;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extensions;

namespace CodeGraph.Services.Analyzers;

public class AnthropicAnalysisProvider(
    IHttpClientFactory httpClientFactory,
    AnthropicCircuitBreaker circuitBreaker,
    IOptions<AnalysisOptions> optionsAccessor,
    ILogger<AnthropicAnalysisProvider> logger,
    IDbBackedLlmProviderConfigResolver? providerConfigResolver = null) : IAnalysisModelProvider
{
    private readonly AnalysisOptions options = optionsAccessor.Value;
    private static readonly JsonSerializerOptions SnakeOpts = CodeGraphJsonDefaults.SnakeCase;

    public string ProviderName => "anthropic";

    public AnalysisProviderCapabilities Capabilities { get; } =
        new(SupportsBatch: true, SupportsStructuredJson: true, SupportsStreaming: false, SupportsLargeContext: true);

    public async Task<AnalysisTextResponse> ExecuteAsync(
        AnalysisPrompt prompt,
        AnalysisRequestOptions request,
        CancellationToken ct = default)
    {
        var providerConfig = await ResolveProviderConfigAsync(ct);
        var body = new AnthropicMessageRequest
        {
            Model = request.Model ?? providerConfig.Model,
            MaxTokens = request.MaxTokens ?? options.MaxTokensPerSynthesis,
            System = prompt.SystemPrompt,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = prompt.UserPrompt
                }
            ]
        };

        var http = httpClientFactory.CreateClient();
        using var response = await circuitBreaker.ExecuteAsync(http,
            () => CreateRequest(HttpMethod.Post, BuildMessagesUrl(providerConfig), providerConfig, body), ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Anthropic message request failed {Status}: {Body}",
                (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var message = await response.Content.ReadFromJsonAsync<AnthropicMessageResponse>(SnakeOpts, ct)
            ?? throw new InvalidOperationException("Anthropic returned null message response");
        var text = ExtractText(message.Content);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Anthropic returned an empty text response");

        return new AnalysisTextResponse(text, message.Model, ProviderName);
    }

    public async Task<AnalysisBatchSubmissionResult> SubmitBatchAsync(
        IReadOnlyList<AnalysisBatchRequestItem> items,
        AnalysisRequestOptions request,
        CancellationToken ct = default)
    {
        var providerConfig = await ResolveProviderConfigAsync(ct);
        var batchRequest = new AnthropicBatchCreateRequest
        {
            Requests = items.Select(item => new AnthropicBatchRequest
            {
                CustomId = item.CustomId,
                Params = new AnthropicMessageRequest
                {
                    Model = request.Model ?? providerConfig.Model,
                    MaxTokens = request.MaxTokens ?? options.MaxTokensPerAnalysis,
                    System = item.Prompt.SystemPrompt,
                    Messages =
                    [
                        new AnthropicMessage
                        {
                            Role = "user",
                            Content = item.Prompt.UserPrompt
                        }
                    ]
                }
            }).ToList()
        };

        var http = httpClientFactory.CreateClient();
        using var response = await circuitBreaker.ExecuteAsync(http,
            () => CreateRequest(HttpMethod.Post, BuildBatchUrl(providerConfig), providerConfig, batchRequest), ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Anthropic batch submission failed {Status}: {Body}",
                (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var created = await response.Content.ReadFromJsonAsync<AnthropicBatchCreatedResponse>(SnakeOpts, ct)
            ?? throw new InvalidOperationException("Anthropic returned null batch response");

        return new AnalysisBatchSubmissionResult(created.Id, created.ProcessingStatus);
    }

    public async Task<AnalysisBatchStatusResult> GetBatchStatusAsync(
        string batchId,
        CancellationToken ct = default)
    {
        var providerConfig = await ResolveProviderConfigAsync(ct);
        var http = httpClientFactory.CreateClient();
        using var response = await circuitBreaker.ExecuteAsync(http,
            () => CreateRequest(HttpMethod.Get, $"{BuildBatchUrl(providerConfig)}/{batchId}", providerConfig), ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Anthropic batch status lookup failed for {BatchId}: {Status} {Body}",
                batchId, (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var status = await response.Content.ReadFromJsonAsync<AnthropicBatchStatusResponse>(SnakeOpts, ct)
            ?? throw new InvalidOperationException("Anthropic returned null batch status");

        return new AnalysisBatchStatusResult(
            status.Id,
            status.ProcessingStatus,
            string.Equals(status.ProcessingStatus, "ended", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<AnalysisBatchItemResult>> GetBatchResultsAsync(
        string batchId,
        IReadOnlyList<string>? requestIds = null,
        CancellationToken ct = default)
    {
        var providerConfig = await ResolveProviderConfigAsync(ct);
        var http = httpClientFactory.CreateClient();
        using var response = await circuitBreaker.ExecuteAsync(http,
            () => CreateRequest(HttpMethod.Get, $"{BuildBatchUrl(providerConfig)}/{batchId}/results", providerConfig), ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Anthropic batch results lookup failed for {BatchId}: {Status} {Body}",
                batchId, (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var results = new List<AnalysisBatchItemResult>();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var result = JsonSerializer.Deserialize<AnthropicBatchResultLine>(line, SnakeOpts);
            if (result is null)
                continue;

            results.Add(new AnalysisBatchItemResult(
                result.CustomId,
                result.Result?.Type ?? "unknown",
                ExtractText(result.Result?.Message?.Content),
                result.Result?.Message?.Model));
        }

        return results;
    }

    private Task<LlmProviderRuntimeConfig> ResolveProviderConfigAsync(CancellationToken ct) =>
        providerConfigResolver?.GetProviderAsync(ProviderName, ct)
        ?? Task.FromResult(LlmProviderRuntimeConfig.FromOptions(ProviderName, options));

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        LlmProviderRuntimeConfig providerConfig,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(providerConfig.ApiKey))
            request.Headers.Add("x-api-key", providerConfig.ApiKey);

        if (!string.IsNullOrWhiteSpace(providerConfig.ApiVersion))
            request.Headers.Add("anthropic-version", providerConfig.ApiVersion);

        if (body is not null)
            request.Content = JsonContent.Create(body, options: SnakeOpts);

        return request;
    }

    private string BuildMessagesUrl(LlmProviderRuntimeConfig providerConfig)
    {
        if (string.IsNullOrWhiteSpace(providerConfig.EndpointUrl))
            return options.Anthropic.MessagesApiUrl;

        var endpoint = providerConfig.EndpointUrl.TrimEnd('/');
        if (endpoint.EndsWith("/messages/batches", StringComparison.OrdinalIgnoreCase))
            return endpoint[..^"/batches".Length];

        if (endpoint.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            return endpoint;

        return $"{endpoint}/messages";
    }

    private string BuildBatchUrl(LlmProviderRuntimeConfig providerConfig)
    {
        if (string.IsNullOrWhiteSpace(providerConfig.EndpointUrl))
            return options.Anthropic.BatchApiBaseUrl.TrimEnd('/');

        var endpoint = providerConfig.EndpointUrl.TrimEnd('/');
        if (endpoint.EndsWith("/messages/batches", StringComparison.OrdinalIgnoreCase))
            return endpoint;

        if (endpoint.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            return $"{endpoint}/batches";

        return $"{endpoint}/messages/batches";
    }

    private static string? ExtractText(IReadOnlyList<AnthropicContentBlock>? content)
    {
        return content?
            .Where(block => block.Type == "text")
            .Select(block => block.Text?.StripCodeFences())
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    private sealed class AnthropicBatchCreateRequest
    {
        public List<AnthropicBatchRequest> Requests { get; set; } = [];
    }

    private sealed class AnthropicBatchRequest
    {
        public string CustomId { get; set; } = "";
        public AnthropicMessageRequest Params { get; set; } = new();
    }

    private sealed class AnthropicMessageRequest
    {
        public string Model { get; set; } = "";
        public int MaxTokens { get; set; }
        public string? System { get; set; }
        public List<AnthropicMessage> Messages { get; set; } = [];
    }

    private sealed class AnthropicMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    private sealed class AnthropicBatchCreatedResponse
    {
        public string Id { get; set; } = "";
        public string ProcessingStatus { get; set; } = "";
    }

    private sealed class AnthropicBatchStatusResponse
    {
        public string Id { get; set; } = "";
        public string ProcessingStatus { get; set; } = "";
    }

    private sealed class AnthropicBatchResultLine
    {
        public string CustomId { get; set; } = "";
        public AnthropicBatchResult? Result { get; set; }
    }

    private sealed class AnthropicBatchResult
    {
        public string Type { get; set; } = "";
        public AnthropicMessageResponse? Message { get; set; }
    }

    private sealed class AnthropicMessageResponse
    {
        public string Model { get; set; } = "";
        public List<AnthropicContentBlock> Content { get; set; } = [];
    }

    private sealed class AnthropicContentBlock
    {
        public string Type { get; set; } = "";
        public string? Text { get; set; }
    }
}
