using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Models;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Services.Analyzers;

public class LocalAnalysisProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<AnalysisOptions> optionsAccessor,
    ILogger<LocalAnalysisProvider> logger) : IAnalysisModelProvider
{
    private readonly AnalysisOptions options = optionsAccessor.Value;
    private static readonly JsonSerializerOptions SnakeOpts = CodeGraphJsonDefaults.SnakeCase;

    public string ProviderName => "local";

    public AnalysisProviderCapabilities Capabilities { get; } =
        new(SupportsBatch: false, SupportsStructuredJson: true, SupportsStreaming: false, SupportsLargeContext: false);

    public async Task<AnalysisTextResponse> ExecuteAsync(
        AnalysisPrompt prompt,
        AnalysisRequestOptions request,
        CancellationToken ct = default)
    {
        var body = new LocalChatCompletionRequest
        {
            Model = ResolveModel(request),
            MaxTokens = request.MaxTokens,
            ResponseFormat = options.Local.UseJsonObjectResponseFormat
                ? new LocalResponseFormat { Type = "json_object" }
                : null,
            Messages =
            [
                new LocalMessageRequest { Role = "system", Content = prompt.SystemPrompt },
                new LocalMessageRequest { Role = "user", Content = prompt.UserPrompt }
            ]
        };

        var http = httpClientFactory.CreateClient();
        using var response = await http.SendAsync(CreateJsonRequest(
            HttpMethod.Post,
            BuildUrl(options.Local.ChatCompletionsPath),
            body), ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Local chat completion failed {Status}: {Body}",
                (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var completion = await response.Content.ReadFromJsonAsync<LocalChatCompletionResponse>(SnakeOpts, ct)
            ?? throw new InvalidOperationException("Local provider returned null chat completion response");
        var text = completion.Choices
            .Select(choice => ExtractMessageText(choice.Message))
            .FirstOrDefault(content => !string.IsNullOrWhiteSpace(content));
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Local provider returned an empty chat completion response");

        return new AnalysisTextResponse(text, completion.Model ?? body.Model, ProviderName);
    }

    public Task<AnalysisBatchSubmissionResult> SubmitBatchAsync(
        IReadOnlyList<AnalysisBatchRequestItem> items,
        AnalysisRequestOptions request,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("The local analysis provider does not support batch submissions.");
    }

    public Task<AnalysisBatchStatusResult> GetBatchStatusAsync(string batchId, CancellationToken ct = default)
    {
        throw new NotSupportedException("The local analysis provider does not support batch status checks.");
    }

    public Task<IReadOnlyList<AnalysisBatchItemResult>> GetBatchResultsAsync(
        string batchId,
        IReadOnlyList<string>? requestIds = null,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("The local analysis provider does not support batch result downloads.");
    }

    private static string? ExtractMessageText(LocalMessageResponse? message)
    {
        if (message is null)
            return null;

        if (message.Content.ValueKind == JsonValueKind.String)
            return message.Content.GetString();

        if (message.Content.ValueKind != JsonValueKind.Array)
            return null;

        var parts = new List<string>();
        foreach (var item in message.Content.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    var str = item.GetString();
                    if (!string.IsNullOrWhiteSpace(str))
                        parts.Add(str);
                    break;
                case JsonValueKind.Object when item.TryGetProperty("text", out var textElement):
                    var text = textElement.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(text);
                    break;
            }
        }

        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private HttpRequestMessage CreateJsonRequest(HttpMethod method, string url, object body)
    {
        var request = CreateRequest(method, url);
        request.Content = JsonContent.Create(body, options: SnakeOpts);
        return request;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);

        if (!string.IsNullOrWhiteSpace(options.Local.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Local.ApiKey);

        return request;
    }

    private string ResolveModel(AnalysisRequestOptions request)
    {
        if (!string.IsNullOrWhiteSpace(request.Model))
            return request.Model;

        if (!string.IsNullOrWhiteSpace(options.Local.Model))
            return options.Local.Model;

        return options.Model;
    }

    private string BuildUrl(string path)
    {
        var baseUrl = options.Local.BaseUrl.TrimEnd('/');
        var relative = path.StartsWith("/") ? path : $"/{path}";
        return $"{baseUrl}{relative}";
    }

    private sealed class LocalChatCompletionRequest
    {
        public string Model { get; set; } = "";
        public int? MaxTokens { get; set; }
        public LocalResponseFormat? ResponseFormat { get; set; }
        public List<LocalMessageRequest> Messages { get; set; } = [];
    }

    private sealed class LocalResponseFormat
    {
        public string Type { get; set; } = "json_object";
    }

    private sealed class LocalMessageRequest
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    private sealed class LocalChatCompletionResponse
    {
        public string? Model { get; set; }
        public List<LocalChoice> Choices { get; set; } = [];
    }

    private sealed class LocalChoice
    {
        public LocalMessageResponse? Message { get; set; }
    }

    private sealed class LocalMessageResponse
    {
        public JsonElement Content { get; set; }
    }
}
