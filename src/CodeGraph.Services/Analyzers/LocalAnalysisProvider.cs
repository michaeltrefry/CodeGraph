using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Exceptions;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Services.Analyzers;

public class LocalAnalysisProvider(
    IHttpClientFactory httpClientFactory,
    IGraphStore store,
    IOptions<AnalysisOptions> optionsAccessor,
    ILogger<LocalAnalysisProvider> logger) : IAnalysisModelProvider
{
    private readonly AnalysisOptions options = optionsAccessor.Value;
    private static readonly JsonSerializerOptions SnakeOpts = CodeGraphJsonDefaults.SnakeCase;
    private static readonly JsonSerializerOptions RequestOpts = new(SnakeOpts)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly SemaphoreSlim concurrencyGate = CreateConcurrencyGate(optionsAccessor.Value.Local.MaxConcurrentRequests);

    public string ProviderName => "local";

    public AnalysisProviderCapabilities Capabilities { get; } =
        new(SupportsBatch: true, SupportsStructuredJson: true, SupportsStreaming: false, SupportsLargeContext: false);

    public async Task<AnalysisTextResponse> ExecuteAsync(
        AnalysisPrompt prompt,
        AnalysisRequestOptions request,
        CancellationToken ct = default)
    {
        if (concurrencyGate.CurrentCount == 0)
            logger.LogInformation("Local provider is saturated; queueing request until one of {MaxConcurrentRequests} slot(s) is free",
                Math.Max(1, options.Local.MaxConcurrentRequests));

        await concurrencyGate.WaitAsync(ct);
        try
        {
            var body = BuildRequestBody(
                ResolveModel(request),
                request.MaxTokens,
                options.Local.UseJsonObjectResponseFormat,
                prompt);

            var http = httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.Local.TimeoutSeconds));

            HttpResponseMessage response;
            try
            {
                response = await SendWithStructuredOutputFallbackAsync(http, body, prompt, request.MaxTokens, ct);
            }
            catch (Exception ex) when (IsRetryableTransportException(ex, ct))
            {
                throw new RetryableAnalysisException(
                    $"Local analysis request failed transiently after waiting up to {http.Timeout.TotalSeconds:F0}s.",
                    ex);
            }

            using (response)
            {
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
        }
        finally
        {
            concurrencyGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendWithStructuredOutputFallbackAsync(
        HttpClient http,
        LocalChatCompletionRequest body,
        AnalysisPrompt prompt,
        int? maxTokens,
        CancellationToken ct)
    {
        var url = BuildUrl(options.Local.ChatCompletionsPath);
        var response = await http.SendAsync(CreateJsonRequest(HttpMethod.Post, url, body), ct);

        if (response.IsSuccessStatusCode ||
            !body.UsesJsonObjectResponseFormat ||
            response.StatusCode != HttpStatusCode.BadRequest)
        {
            return response;
        }

        var errorBody = await response.Content.ReadAsStringAsync(ct);
        response.Dispose();

        logger.LogWarning(
            "Local provider rejected structured json_object output; retrying without response_format. Response: {Body}",
            errorBody);

        var fallbackBody = BuildRequestBody(body.Model, maxTokens, useJsonObjectResponseFormat: false, prompt);
        return await http.SendAsync(CreateJsonRequest(HttpMethod.Post, url, fallbackBody), ct);
    }

    public Task<AnalysisBatchSubmissionResult> SubmitBatchAsync(
        IReadOnlyList<AnalysisBatchRequestItem> items,
        AnalysisRequestOptions request,
        CancellationToken ct = default)
    {
        return Task.FromResult(new AnalysisBatchSubmissionResult(CreateBatchId(), "submitted"));
    }

    public async Task<AnalysisBatchStatusResult> GetBatchStatusAsync(string batchId, CancellationToken ct = default)
    {
        var batch = await store.GetBatchByProviderBatchIdAsync(batchId)
            ?? throw new InvalidOperationException($"Local batch '{batchId}' was not found.");

        var requests = await store.GetBatchRequestsAsync(batch.Id);
        if (requests.Count == 0)
            return new AnalysisBatchStatusResult(batchId, "submitted", IsCompleted: false);

        var pendingRequest = requests
            .Where(r => string.Equals(r.Status, "pending", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Sequence)
            .ThenBy(r => r.CustomId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (pendingRequest is not null)
        {
            await ProcessPendingBatchRequestAsync(batch, pendingRequest, ct);
            requests = await store.GetBatchRequestsAsync(batch.Id);
        }

        var completedCount = requests.Count(r => IsTerminalStatus(r.Status));
        await store.UpdateBatchStatusAsync(batch.Id, batch.Status, completedCount, completedAt: null);

        var isCompleted = requests.Count > 0 && requests.All(r => IsTerminalStatus(r.Status));
        return new AnalysisBatchStatusResult(
            batchId,
            isCompleted ? "completed" : "processing",
            IsCompleted: isCompleted);
    }

    public async Task<IReadOnlyList<AnalysisBatchItemResult>> GetBatchResultsAsync(
        string batchId,
        IReadOnlyList<string>? requestIds = null,
        CancellationToken ct = default)
    {
        var batch = await store.GetBatchByProviderBatchIdAsync(batchId)
            ?? throw new InvalidOperationException($"Local batch '{batchId}' was not found.");
        var requests = await store.GetBatchRequestsAsync(batch.Id);

        if (requestIds is not null && requestIds.Count > 0)
        {
            var allowed = requestIds.ToHashSet(StringComparer.Ordinal);
            requests = requests.Where(r => allowed.Contains(r.CustomId)).ToList();
        }

        return requests
            .OrderBy(r => r.Sequence)
            .ThenBy(r => r.CustomId, StringComparer.Ordinal)
            .Select(r => new AnalysisBatchItemResult(
                r.CustomId,
                r.Status,
                r.ResponseText,
                r.ModelUsed))
            .ToList();
    }

    private async Task ProcessPendingBatchRequestAsync(
        StoredAnalysisBatch batch,
        AnalysisBatchRequestEntity request,
        CancellationToken ct)
    {
        var nextAttempt = request.AttemptCount + 1;
        var maxAttempts = Math.Max(1, options.Local.DirectFallbackMaxAttempts);

        AnalysisBatchRequestPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AnalysisBatchRequestPayload>(request.RequestPayloadJson ?? "", CodeGraphJsonDefaults.CamelCase);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Local batch {BatchId} request {CustomId} has invalid request payload JSON",
                batch.ProviderBatchId, request.CustomId);
            await store.UpdateBatchRequestStateAsync(batch.Id, request.CustomId, "errored", nextAttempt,
                responseText: null, modelUsed: null, completedAt: DateTime.UtcNow);
            return;
        }

        if (payload is null)
        {
            logger.LogError("Local batch {BatchId} request {CustomId} is missing request payload JSON",
                batch.ProviderBatchId, request.CustomId);
            await store.UpdateBatchRequestStateAsync(batch.Id, request.CustomId, "errored", nextAttempt,
                responseText: null, modelUsed: null, completedAt: DateTime.UtcNow);
            return;
        }

        try
        {
            var response = await ExecuteAsync(payload.Prompt, payload.Request, ct);
            await store.UpdateBatchRequestStateAsync(batch.Id, request.CustomId, "succeeded", nextAttempt,
                responseText: response.Text, modelUsed: response.ModelUsed, completedAt: DateTime.UtcNow);
        }
        catch (RetryableAnalysisException ex) when (nextAttempt < maxAttempts)
        {
            logger.LogWarning(ex,
                "Local batch {BatchId} request {CustomId} hit a transient failure; keeping it pending (attempt {Attempt}/{MaxAttempts})",
                batch.ProviderBatchId, request.CustomId, nextAttempt, maxAttempts);
            await store.UpdateBatchRequestStateAsync(batch.Id, request.CustomId, "pending", nextAttempt,
                responseText: null, modelUsed: null, completedAt: null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Local batch {BatchId} request {CustomId} failed",
                batch.ProviderBatchId, request.CustomId);
            await store.UpdateBatchRequestStateAsync(batch.Id, request.CustomId, "errored", nextAttempt,
                responseText: null, modelUsed: null, completedAt: DateTime.UtcNow);
        }
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
        request.Content = JsonContent.Create(body, options: RequestOpts);
        return request;
    }

    internal static string SerializeRequestBodyForTests(
        string model,
        int? maxTokens,
        bool useJsonObjectResponseFormat,
        AnalysisPrompt prompt)
    {
        var body = BuildRequestBody(model, maxTokens, useJsonObjectResponseFormat, prompt);
        return JsonSerializer.Serialize(body, RequestOpts);
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

        throw new InvalidOperationException(
            "CodeGraph:AnalysisOptions:Local:Model must be configured when using the local analysis provider.");
    }

    private static bool IsTerminalStatus(string status) =>
        string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "errored", StringComparison.OrdinalIgnoreCase);

    private string BuildUrl(string path)
    {
        var baseUrl = NormalizeBaseUrl(options.Local.BaseUrl).TrimEnd('/');
        var relative = path.StartsWith("/") ? path : $"/{path}";
        return $"{baseUrl}{relative}";
    }

    internal static string NormalizeBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return baseUrl;

        var runningInContainer = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (!runningInContainer)
            return baseUrl;

        return Regex.Replace(
            baseUrl,
            @"^http://(localhost|127\.0\.0\.1)(?=[:/]|$)",
            "http://host.docker.internal",
            RegexOptions.IgnoreCase);
    }

    private static LocalChatCompletionRequest BuildRequestBody(
        string model,
        int? maxTokens,
        bool useJsonObjectResponseFormat,
        AnalysisPrompt prompt)
    {
        return new LocalChatCompletionRequest
        {
            Model = model,
            MaxTokens = maxTokens,
            ResponseFormat = useJsonObjectResponseFormat
                ? new LocalResponseFormat { Type = "json_object" }
                : null,
            Messages =
            [
                new LocalMessageRequest { Role = "system", Content = prompt.SystemPrompt },
                new LocalMessageRequest { Role = "user", Content = prompt.UserPrompt }
            ]
        };
    }

    private static string CreateBatchId() =>
        $"local_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";

    private static SemaphoreSlim CreateConcurrencyGate(int maxConcurrentRequests)
    {
        var max = Math.Max(1, maxConcurrentRequests);
        return new SemaphoreSlim(max, max);
    }

    private static bool IsRetryableTransportException(Exception ex, CancellationToken ct)
    {
        if (ex is RetryableAnalysisException)
            return true;

        if (ex is TaskCanceledException && !ct.IsCancellationRequested)
            return true;

        if (ex is TimeoutException or HttpRequestException)
            return true;

        return ex.InnerException is not null && IsRetryableTransportException(ex.InnerException, ct);
    }

    private sealed class LocalChatCompletionRequest
    {
        public string Model { get; set; } = "";
        public int? MaxTokens { get; set; }
        public LocalResponseFormat? ResponseFormat { get; set; }
        public List<LocalMessageRequest> Messages { get; set; } = [];

        [JsonIgnore]
        public bool UsesJsonObjectResponseFormat =>
            string.Equals(ResponseFormat?.Type, "json_object", StringComparison.OrdinalIgnoreCase);
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
