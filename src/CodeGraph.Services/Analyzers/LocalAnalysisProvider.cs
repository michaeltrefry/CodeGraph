using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Exceptions;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extensions;

namespace CodeGraph.Services.Analyzers;

public class LocalAnalysisProvider(
    IHttpClientFactory httpClientFactory,
    IGraphStore store,
    IOptions<AnalysisOptions> optionsAccessor,
    ILogger<LocalAnalysisProvider> logger,
    IServiceScopeFactory? scopeFactory = null) : IAnalysisModelProvider
{
    private readonly AnalysisOptions options = optionsAccessor.Value;
    private static readonly JsonSerializerOptions SnakeOpts = CodeGraphJsonDefaults.SnakeCase;
    private static readonly JsonSerializerOptions RequestOpts = new(SnakeOpts)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly SemaphoreSlim concurrencyGate = CreateConcurrencyGate(optionsAccessor.Value.Local.MaxConcurrentRequests);
    private readonly object batchRunnerLock = new();
    private readonly Dictionary<string, Task> batchRunners = new(StringComparer.Ordinal);

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
                    if (TryRecoverCompletionFromBadRequest(errorBody, body.Model, out var recovered))
                    {
                        logger.LogWarning(
                            "Local provider returned {Status} while parsing model output; recovered JSON from error body and will continue.",
                            (int)response.StatusCode);
                        return recovered;
                    }

                    logger.LogError("Local chat completion failed {Status}: {Body}",
                        (int)response.StatusCode, errorBody);
                    response.EnsureSuccessStatusCode();
                }

                var rawBody = await response.Content.ReadAsStringAsync(ct);
                var completion = JsonSerializer.Deserialize<LocalChatCompletionResponse>(rawBody, SnakeOpts)
                    ?? throw new InvalidOperationException("Local provider returned null chat completion response");
                var text = completion.Choices
                    .Select(ExtractChoiceText)
                    .FirstOrDefault(content => !string.IsNullOrWhiteSpace(content));
                if (string.IsNullOrWhiteSpace(text))
                {
                    logger.LogWarning("Local provider returned an empty chat completion response body: {Body}",
                        TruncateForLog(rawBody, 1200));
                    throw new InvalidOperationException("Local provider returned an empty chat completion response");
                }

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

    private AnalysisTextResponse CreateRecoveredResponse(string text, string model) =>
        new(text, model, ProviderName);

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

        var completedCount = requests.Count(r => IsTerminalStatus(r.Status));
        var isCompleted = requests.Count > 0 && requests.All(r => IsTerminalStatus(r.Status));
        if (isCompleted)
        {
            await store.UpdateBatchStatusAsync(batch.Id, "submitted", completedCount, completedAt: null);
            return new AnalysisBatchStatusResult(batchId, "completed", IsCompleted: true);
        }

        await store.UpdateBatchStatusAsync(batch.Id, "submitted", completedCount, completedAt: null);
        EnsureBatchRunnerStarted(batchId);

        return new AnalysisBatchStatusResult(
            batchId,
            IsBatchRunnerActive(batchId) ? "processing" : "submitted",
            IsCompleted: false);
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
        IGraphStore batchStore,
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
            await batchStore.UpdateBatchRequestStateAsync(batch.Id, request.CustomId, "errored", nextAttempt,
                responseText: null, modelUsed: null, completedAt: DateTime.UtcNow);
            return;
        }

        if (payload is null)
        {
            logger.LogError("Local batch {BatchId} request {CustomId} is missing request payload JSON",
                batch.ProviderBatchId, request.CustomId);
            await batchStore.UpdateBatchRequestStateAsync(batch.Id, request.CustomId, "errored", nextAttempt,
                responseText: null, modelUsed: null, completedAt: DateTime.UtcNow);
            return;
        }

        try
        {
            var response = await ExecuteAsync(payload.Prompt, payload.Request, ct);
            await batchStore.UpdateBatchRequestStateAsync(batch.Id, request.CustomId, "succeeded", nextAttempt,
                responseText: response.Text, modelUsed: response.ModelUsed, completedAt: DateTime.UtcNow);
        }
        catch (RetryableAnalysisException ex) when (nextAttempt < maxAttempts)
        {
            logger.LogWarning(ex,
                "Local batch {BatchId} request {CustomId} hit a transient failure; keeping it pending (attempt {Attempt}/{MaxAttempts})",
                batch.ProviderBatchId, request.CustomId, nextAttempt, maxAttempts);
            await batchStore.UpdateBatchRequestStateAsync(batch.Id, request.CustomId, "pending", nextAttempt,
                responseText: null, modelUsed: null, completedAt: null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Local batch {BatchId} request {CustomId} failed",
                batch.ProviderBatchId, request.CustomId);
            await batchStore.UpdateBatchRequestStateAsync(batch.Id, request.CustomId, "errored", nextAttempt,
                responseText: null, modelUsed: null, completedAt: DateTime.UtcNow);
        }
    }

    private bool TryRecoverCompletionFromBadRequest(string errorBody, string model, out AnalysisTextResponse recovered)
    {
        recovered = default!;

        if (string.IsNullOrWhiteSpace(errorBody))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (!doc.RootElement.TryGetProperty("error", out var errorElement) ||
                errorElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var errorText = errorElement.GetString();
            if (string.IsNullOrWhiteSpace(errorText) ||
                errorText.IndexOf("Failed to parse input", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var recoveredText = errorText.NormalizeJsonResponse()
                .Replace("\uFFFD", "", StringComparison.Ordinal)
                .Trim();

            if (string.IsNullOrWhiteSpace(recoveredText) ||
                recoveredText[0] is not ('{' or '['))
            {
                return false;
            }

            recovered = CreateRecoveredResponse(recoveredText, model);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void EnsureBatchRunnerStarted(string batchId)
    {
        lock (batchRunnerLock)
        {
            if (batchRunners.TryGetValue(batchId, out var existing) && !existing.IsCompleted)
                return;

            var runner = Task.Run(() => RunBatchInBackgroundAsync(batchId));
            batchRunners[batchId] = runner;

            _ = runner.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    logger.LogError(t.Exception,
                        "Local batch runner crashed for {BatchId}",
                        batchId);
                }

                lock (batchRunnerLock)
                {
                    if (batchRunners.TryGetValue(batchId, out var current) && ReferenceEquals(current, runner))
                        batchRunners.Remove(batchId);
                }
            }, TaskScheduler.Default);
        }
    }

    private bool IsBatchRunnerActive(string batchId)
    {
        lock (batchRunnerLock)
        {
            return batchRunners.TryGetValue(batchId, out var runner) && !runner.IsCompleted;
        }
    }

    private async Task RunBatchInBackgroundAsync(string batchId)
    {
        using var scope = scopeFactory?.CreateScope();
        var batchStore = scope?.ServiceProvider.GetRequiredService<IGraphStore>() ?? store;

        while (true)
        {
            var batch = await batchStore.GetBatchByProviderBatchIdAsync(batchId);
            if (batch is null)
                return;

            var requests = await batchStore.GetBatchRequestsAsync(batch.Id);
            if (requests.Count == 0)
            {
                await batchStore.UpdateBatchStatusAsync(batch.Id, "submitted", 0, completedAt: null);
                return;
            }

            var completedCount = requests.Count(r => IsTerminalStatus(r.Status));
            var pendingRequest = requests
                .Where(r => string.Equals(r.Status, "pending", StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Sequence)
                .ThenBy(r => r.CustomId, StringComparer.Ordinal)
                .FirstOrDefault();

            if (pendingRequest is null)
            {
                await batchStore.UpdateBatchStatusAsync(batch.Id, "submitted", completedCount, completedAt: null);
                return;
            }

            var priorAttemptCount = pendingRequest.AttemptCount;
            await ProcessPendingBatchRequestAsync(batchStore, batch, pendingRequest, CancellationToken.None);

            requests = await batchStore.GetBatchRequestsAsync(batch.Id);
            completedCount = requests.Count(r => IsTerminalStatus(r.Status));
            await batchStore.UpdateBatchStatusAsync(batch.Id, "submitted", completedCount, completedAt: null);

            var updatedRequest = requests.FirstOrDefault(r => string.Equals(r.CustomId, pendingRequest.CustomId, StringComparison.Ordinal));
            if (updatedRequest is null)
                return;

            // Stop after a retryable failure so the next poll can re-drive the batch later.
            if (string.Equals(updatedRequest.Status, "pending", StringComparison.OrdinalIgnoreCase) &&
                updatedRequest.AttemptCount > priorAttemptCount)
            {
                return;
            }
        }
    }

    private static string? ExtractChoiceText(LocalChoice? choice)
    {
        if (choice is null)
            return null;

        var messageText = ExtractContentText(choice.Message?.Content);
        if (!string.IsNullOrWhiteSpace(messageText))
            return messageText;

        var directText = ExtractContentText(choice.Text);
        if (!string.IsNullOrWhiteSpace(directText))
            return directText;

        var reasoningText = ExtractContentText(choice.Reasoning);
        if (!string.IsNullOrWhiteSpace(reasoningText))
            return reasoningText;

        return null;
    }

    private static string? ExtractContentText(JsonElement? content)
    {
        if (content is null)
            return null;

        var element = content.Value;

        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
            return null;

        if (element.ValueKind == JsonValueKind.String)
            return element.GetString();

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("text", out var objectText))
            {
                var text = ExtractContentText(objectText);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            if (element.TryGetProperty("content", out var nestedContent))
            {
                var nested = ExtractContentText(nestedContent);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
            return null;

        var parts = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            var text = ExtractContentText(item);
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(text);
        }

        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static string TruncateForLog(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            return text;

        return text[..maxChars] + "...";
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
        public JsonElement? Text { get; set; }
        public JsonElement? Reasoning { get; set; }
    }

    private sealed class LocalMessageResponse
    {
        public JsonElement? Content { get; set; }
    }
}
