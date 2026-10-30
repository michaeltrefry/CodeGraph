using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Models;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Services.Analyzers;

public class OpenAiAnalysisProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<AnalysisOptions> optionsAccessor,
    ILogger<OpenAiAnalysisProvider> logger,
    IDbBackedLlmProviderConfigResolver? providerConfigResolver = null) : IAnalysisModelProvider
{
    private readonly AnalysisOptions options = optionsAccessor.Value;
    private static readonly JsonSerializerOptions SnakeOpts = CodeGraphJsonDefaults.SnakeCase;

    public string ProviderName => "openai";

    public AnalysisProviderCapabilities Capabilities { get; } =
        new(SupportsBatch: true, SupportsStructuredJson: true, SupportsStreaming: false, SupportsLargeContext: true);

    /// <summary>The hosted OpenAI API needs a key; a custom OpenAI-compatible endpoint may not.</summary>
    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        var providerConfig = await ResolveProviderConfigAsync(ct);
        if (!string.IsNullOrWhiteSpace(providerConfig.ApiKey))
            return true;
        return !Uri.TryCreate(BuildUrl(providerConfig, "/"), UriKind.Absolute, out var endpoint) ||
               !endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AnalysisTextResponse> ExecuteAsync(
        AnalysisPrompt prompt,
        AnalysisRequestOptions request,
        CancellationToken ct = default)
    {
        var providerConfig = await ResolveProviderConfigAsync(ct);
        var body = new OpenAiChatCompletionRequest
        {
            Model = ResolveModel(request, providerConfig),
            MaxCompletionTokens = request.MaxTokens,
            ResponseFormat = new OpenAiResponseFormat { Type = "json_object" },
            Messages =
            [
                new OpenAiMessage { Role = "system", Content = prompt.SystemPrompt },
                new OpenAiMessage { Role = "user", Content = prompt.UserPrompt }
            ]
        };

        var http = httpClientFactory.CreateClient();
        using var response = await http.SendAsync(CreateJsonRequest(
            HttpMethod.Post,
            BuildUrl(providerConfig, options.OpenAi.ChatCompletionsPath),
            providerConfig,
            body), ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("OpenAI chat completion failed {Status}: {Body}",
                (int)response.StatusCode, errorBody);
            throw new HttpRequestException($"Provider returned HTTP {(int)response.StatusCode}: {errorBody}",
                null, response.StatusCode);
        }

        var completion = await response.Content.ReadFromJsonAsync<OpenAiChatCompletionResponse>(SnakeOpts, ct)
            ?? throw new InvalidOperationException("OpenAI returned null chat completion response");
        var text = completion.Choices
            .Select(choice => choice?.Message?.Content)
            .FirstOrDefault(content => !string.IsNullOrWhiteSpace(content));
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("OpenAI returned an empty chat completion response");

        return new AnalysisTextResponse(text, completion.Model, ProviderName);
    }

    public async Task<AnalysisBatchSubmissionResult> SubmitBatchAsync(
        IReadOnlyList<AnalysisBatchRequestItem> items,
        AnalysisRequestOptions request,
        CancellationToken ct = default)
    {
        var providerConfig = await ResolveProviderConfigAsync(ct);
        var inputJsonl = BuildBatchInputFile(items, request, providerConfig);
        var inputFileId = await UploadBatchFileAsync(inputJsonl, providerConfig, ct);

        var body = new OpenAiBatchCreateRequest
        {
            InputFileId = inputFileId,
            Endpoint = options.OpenAi.ChatCompletionsPath.StartsWith("/")
                ? options.OpenAi.ChatCompletionsPath
                : $"/{options.OpenAi.ChatCompletionsPath}",
            CompletionWindow = "24h"
        };

        var http = httpClientFactory.CreateClient();
        using var response = await http.SendAsync(CreateJsonRequest(
            HttpMethod.Post,
            BuildUrl(providerConfig, options.OpenAi.BatchesPath),
            providerConfig,
            body), ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("OpenAI batch creation failed {Status}: {Body}",
                (int)response.StatusCode, errorBody);
            throw new HttpRequestException($"Provider returned HTTP {(int)response.StatusCode}: {errorBody}",
                null, response.StatusCode);
        }

        var created = await response.Content.ReadFromJsonAsync<OpenAiBatchResponse>(SnakeOpts, ct)
            ?? throw new InvalidOperationException("OpenAI returned null batch create response");

        return new AnalysisBatchSubmissionResult(created.Id, created.Status);
    }

    public async Task<AnalysisBatchStatusResult> GetBatchStatusAsync(string batchId, CancellationToken ct = default)
    {
        var providerConfig = await ResolveProviderConfigAsync(ct);
        var http = httpClientFactory.CreateClient();
        using var response = await http.SendAsync(CreateRequest(
            HttpMethod.Get,
            BuildUrl(providerConfig, $"{options.OpenAi.BatchesPath.TrimEnd('/')}/{batchId}"),
            providerConfig), ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("OpenAI batch status lookup failed for {BatchId}: {Status} {Body}",
                batchId, (int)response.StatusCode, errorBody);
            throw new HttpRequestException($"Provider returned HTTP {(int)response.StatusCode}: {errorBody}",
                null, response.StatusCode);
        }

        var status = await response.Content.ReadFromJsonAsync<OpenAiBatchResponse>(SnakeOpts, ct)
            ?? throw new InvalidOperationException("OpenAI returned null batch status response");

        return new AnalysisBatchStatusResult(
            status.Id,
            status.Status,
            string.Equals(status.Status, "completed", StringComparison.OrdinalIgnoreCase),
            status.Errors?.ToString());
    }

    public async Task<IReadOnlyList<AnalysisBatchItemResult>> GetBatchResultsAsync(
        string batchId,
        IReadOnlyList<string>? requestIds = null,
        CancellationToken ct = default)
    {
        var providerConfig = await ResolveProviderConfigAsync(ct);
        var http = httpClientFactory.CreateClient();
        using var batchResponse = await http.SendAsync(CreateRequest(
            HttpMethod.Get,
            BuildUrl(providerConfig, $"{options.OpenAi.BatchesPath.TrimEnd('/')}/{batchId}"),
            providerConfig), ct);

        if (!batchResponse.IsSuccessStatusCode)
        {
            var errorBody = await batchResponse.Content.ReadAsStringAsync(ct);
            logger.LogError("OpenAI batch lookup failed for {BatchId}: {Status} {Body}",
                batchId, (int)batchResponse.StatusCode, errorBody);
            throw new HttpRequestException($"Provider returned HTTP {(int)batchResponse.StatusCode}: {errorBody}",
                null, batchResponse.StatusCode);
        }

        var batch = await batchResponse.Content.ReadFromJsonAsync<OpenAiBatchResponse>(SnakeOpts, ct)
            ?? throw new InvalidOperationException("OpenAI returned null batch lookup response");
        var results = new List<AnalysisBatchItemResult>();
        // Error lines are stored separately from successful responses by the hosted batch API.
        foreach (var fileId in new[] { batch.OutputFileId, batch.ErrorFileId }
                     .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            using var fileResponse = await http.SendAsync(CreateRequest(
                HttpMethod.Get,
                BuildUrl(providerConfig, $"{options.OpenAi.FilesPath.TrimEnd('/')}/{fileId}/content"),
                providerConfig), ct);
            if (!fileResponse.IsSuccessStatusCode)
            {
                results.Add(new AnalysisBatchItemResult("", "errored", null, null,
                    $"Provider result download returned HTTP {(int)fileResponse.StatusCode}: {await fileResponse.Content.ReadAsStringAsync(ct)}"));
                continue;
            }
            await using var stream = await fileResponse.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                OpenAiBatchOutputLine? item;
                try
                {
                    item = JsonSerializer.Deserialize<OpenAiBatchOutputLine>(line, SnakeOpts);
                }
                catch (JsonException ex)
                {
                    results.Add(new AnalysisBatchItemResult("", "errored", null, null, $"Malformed provider result: {ex.Message}"));
                    continue;
                }
                if (item is null)
                {
                    results.Add(new AnalysisBatchItemResult("", "errored", null, null, "Null provider result."));
                    continue;
                }
                var error = item.Error?.ToString() ?? item.Response?.Body?.Error?.ToString();
                var choices = item.Response?.Body?.Choices;
                var text = choices?.Select(choice => choice?.Message?.Content)
                    .FirstOrDefault(content => !string.IsNullOrWhiteSpace(content));
                if (error is null && item.Response?.StatusCode is >= 200 and < 300)
                {
                    if (choices is null || choices.Count == 0 || choices.Any(choice => choice is null || choice.Message is null))
                        error = "Malformed provider result: response choices are missing or contain a null choice/message.";
                    else if (string.IsNullOrWhiteSpace(text))
                        error = "Malformed provider result: response choices contain no non-empty text.";
                }
                var succeeded = error is null && item.Response?.StatusCode is >= 200 and < 300;
                results.Add(new AnalysisBatchItemResult(item.CustomId, succeeded ? "succeeded" : "errored",
                    text, item.Response?.Body?.Model,
                    succeeded ? null : error ?? $"Provider returned HTTP {item.Response?.StatusCode}."));
            }
        }

        return results;
    }

    private async Task<string> UploadBatchFileAsync(
        string jsonl,
        LlmProviderRuntimeConfig providerConfig,
        CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient();
        using var request = CreateRequest(HttpMethod.Post, BuildUrl(providerConfig, options.OpenAi.FilesPath), providerConfig);
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(jsonl));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/jsonl");
        form.Add(fileContent, "file", "analysis-batch.jsonl");
        form.Add(new StringContent("batch"), "purpose");
        request.Content = form;

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("OpenAI batch file upload failed {Status}: {Body}",
                (int)response.StatusCode, errorBody);
            throw new HttpRequestException($"Provider returned HTTP {(int)response.StatusCode}: {errorBody}",
                null, response.StatusCode);
        }

        var uploaded = await response.Content.ReadFromJsonAsync<OpenAiFileResponse>(SnakeOpts, ct)
            ?? throw new InvalidOperationException("OpenAI returned null file upload response");
        return uploaded.Id;
    }

    private string BuildBatchInputFile(
        IReadOnlyList<AnalysisBatchRequestItem> items,
        AnalysisRequestOptions request,
        LlmProviderRuntimeConfig providerConfig)
    {
        var lines = items.Select(item => JsonSerializer.Serialize(new OpenAiBatchInputLine
        {
            CustomId = item.CustomId,
            Method = "POST",
            Url = options.OpenAi.ChatCompletionsPath.StartsWith("/")
                ? options.OpenAi.ChatCompletionsPath
                : $"/{options.OpenAi.ChatCompletionsPath}",
            Body = new OpenAiChatCompletionRequest
            {
                Model = ResolveModel(request, providerConfig),
                MaxCompletionTokens = request.MaxTokens,
                ResponseFormat = new OpenAiResponseFormat { Type = "json_object" },
                Messages =
                [
                    new OpenAiMessage { Role = "system", Content = item.Prompt.SystemPrompt },
                    new OpenAiMessage { Role = "user", Content = item.Prompt.UserPrompt }
                ]
            }
        }, SnakeOpts));

        return string.Join("\n", lines);
    }

    private string ResolveModel(AnalysisRequestOptions request, LlmProviderRuntimeConfig providerConfig)
    {
        if (!string.IsNullOrWhiteSpace(request.Model))
            return request.Model;

        if (!string.IsNullOrWhiteSpace(providerConfig.Model))
            return providerConfig.Model;

        return options.Model;
    }

    private Task<LlmProviderRuntimeConfig> ResolveProviderConfigAsync(CancellationToken ct) =>
        providerConfigResolver?.GetProviderAsync(ProviderName, ct)
        ?? Task.FromResult(LlmProviderRuntimeConfig.FromOptions(ProviderName, options));

    private HttpRequestMessage CreateJsonRequest(
        HttpMethod method,
        string url,
        LlmProviderRuntimeConfig providerConfig,
        object body)
    {
        var request = CreateRequest(method, url, providerConfig);
        request.Content = JsonContent.Create(body, options: SnakeOpts);
        return request;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, LlmProviderRuntimeConfig providerConfig)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(providerConfig.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", providerConfig.ApiKey);

        var organization = options.OpenAi.Organization;
        if (!string.IsNullOrWhiteSpace(organization))
            request.Headers.Add("OpenAI-Organization", organization);

        var project = options.OpenAi.Project;
        if (!string.IsNullOrWhiteSpace(project))
            request.Headers.Add("OpenAI-Project", project);

        return request;
    }

    private string BuildUrl(LlmProviderRuntimeConfig providerConfig, string path)
    {
        var baseUrl = (providerConfig.EndpointUrl ?? options.OpenAi.BaseUrl).TrimEnd('/');
        var relative = path.StartsWith("/") ? path : $"/{path}";
        return $"{baseUrl}{relative}";
    }

    private sealed class OpenAiChatCompletionRequest
    {
        public string Model { get; set; } = "";
        public int? MaxCompletionTokens { get; set; }
        public OpenAiResponseFormat? ResponseFormat { get; set; }
        public List<OpenAiMessage> Messages { get; set; } = [];
    }

    private sealed class OpenAiResponseFormat
    {
        public string Type { get; set; } = "json_object";
    }

    private sealed class OpenAiMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    private sealed class OpenAiChatCompletionResponse
    {
        public JsonElement? Error { get; set; }
        public string Model { get; set; } = "";
        public List<OpenAiChoice> Choices { get; set; } = [];
    }

    private sealed class OpenAiChoice
    {
        public OpenAiChoiceMessage? Message { get; set; }
    }

    private sealed class OpenAiChoiceMessage
    {
        public string? Content { get; set; }
    }

    private sealed class OpenAiBatchCreateRequest
    {
        public string InputFileId { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string CompletionWindow { get; set; } = "24h";
    }

    private sealed class OpenAiBatchResponse
    {
        public string Id { get; set; } = "";
        public string Status { get; set; } = "";
        public string? OutputFileId { get; set; }
        public string? ErrorFileId { get; set; }
        public JsonElement? Errors { get; set; }
    }

    private sealed class OpenAiFileResponse
    {
        public string Id { get; set; } = "";
    }

    private sealed class OpenAiBatchInputLine
    {
        public string CustomId { get; set; } = "";
        public string Method { get; set; } = "POST";
        public string Url { get; set; } = "";
        public OpenAiChatCompletionRequest Body { get; set; } = new();
    }

    private sealed class OpenAiBatchOutputLine
    {
        public string CustomId { get; set; } = "";
        public JsonElement? Error { get; set; }
        public OpenAiBatchOutputResponse? Response { get; set; }
    }

    private sealed class OpenAiBatchOutputResponse
    {
        public int StatusCode { get; set; }
        public OpenAiChatCompletionResponse? Body { get; set; }
    }
}
