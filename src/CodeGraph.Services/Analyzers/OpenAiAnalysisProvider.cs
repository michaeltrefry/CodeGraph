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
            response.EnsureSuccessStatusCode();
        }

        var completion = await response.Content.ReadFromJsonAsync<OpenAiChatCompletionResponse>(SnakeOpts, ct)
            ?? throw new InvalidOperationException("OpenAI returned null chat completion response");
        var text = completion.Choices
            .Select(choice => choice.Message?.Content)
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
            response.EnsureSuccessStatusCode();
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
            response.EnsureSuccessStatusCode();
        }

        var status = await response.Content.ReadFromJsonAsync<OpenAiBatchResponse>(SnakeOpts, ct)
            ?? throw new InvalidOperationException("OpenAI returned null batch status response");

        return new AnalysisBatchStatusResult(
            status.Id,
            status.Status,
            string.Equals(status.Status, "completed", StringComparison.OrdinalIgnoreCase));
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
            batchResponse.EnsureSuccessStatusCode();
        }

        var batch = await batchResponse.Content.ReadFromJsonAsync<OpenAiBatchResponse>(SnakeOpts, ct)
            ?? throw new InvalidOperationException("OpenAI returned null batch lookup response");
        if (string.IsNullOrWhiteSpace(batch.OutputFileId))
            return [];

        using var fileResponse = await http.SendAsync(CreateRequest(
            HttpMethod.Get,
            BuildUrl(providerConfig, $"{options.OpenAi.FilesPath.TrimEnd('/')}/{batch.OutputFileId}/content"),
            providerConfig), ct);

        if (!fileResponse.IsSuccessStatusCode)
        {
            var errorBody = await fileResponse.Content.ReadAsStringAsync(ct);
            logger.LogError("OpenAI batch output download failed for {BatchId}: {Status} {Body}",
                batchId, (int)fileResponse.StatusCode, errorBody);
            fileResponse.EnsureSuccessStatusCode();
        }

        var results = new List<AnalysisBatchItemResult>();
        await using var stream = await fileResponse.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var item = JsonSerializer.Deserialize<OpenAiBatchOutputLine>(line, SnakeOpts);
            if (item is null)
                continue;

            var status = item.Error is null &&
                item.Response?.StatusCode is >= 200 and < 300
                ? "succeeded"
                : "errored";
            var text = item.Response?.Body?.Choices
                ?.Select(choice => choice.Message?.Content)
                .FirstOrDefault(content => !string.IsNullOrWhiteSpace(content));
            var model = item.Response?.Body?.Model;

            results.Add(new AnalysisBatchItemResult(item.CustomId, status, text, model));
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
            response.EnsureSuccessStatusCode();
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
        public OpenAiBatchOutputError? Error { get; set; }
        public OpenAiBatchOutputResponse? Response { get; set; }
    }

    private sealed class OpenAiBatchOutputError
    {
        public string? Message { get; set; }
    }

    private sealed class OpenAiBatchOutputResponse
    {
        public int StatusCode { get; set; }
        public OpenAiChatCompletionResponse? Body { get; set; }
    }
}
