using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeGraph.Host.Shared.Auth;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using Microsoft.Extensions.Options;

namespace CodeGraph.Indexer.Client;

public sealed class HttpIndexerClient(
    IHttpClientFactory httpClientFactory,
    IOptions<IndexerClientOptions> optionsAccessor,
    IInternalServiceTokenFactory tokenFactory) : IIndexerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private IndexerClientOptions Options => optionsAccessor.Value;

    public Task<IndexerAcceptedResponse> StartProcessRepositoriesAsync(
        string username,
        ProcessRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<IndexerAcceptedResponse>(
            username,
            HttpMethod.Post,
            "api/indexer/repositories/process",
            request,
            ct);
    }

    public Task<IndexerAcceptedResponse> StartReIndexAllAsync(string username, CancellationToken ct = default)
        => SendJsonAsync<IndexerAcceptedResponse>(
            username,
            HttpMethod.Post,
            "api/indexer/repositories/reindex-all",
            ct: ct);

    public Task<IndexerAcceptedResponse> StartDiscoverAsync(
        string username,
        DiscoverRequest? request = null,
        CancellationToken ct = default)
        => SendJsonAsync<IndexerAcceptedResponse>(
            username,
            HttpMethod.Post,
            "api/indexer/repositories/discover",
            request ?? new DiscoverRequest(),
            ct);

    public Task<IndexerAcceptedResponse> StartSyncSchemaAsync(
        string username,
        long sourceId,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceId);
        return SendJsonAsync<IndexerAcceptedResponse>(
            username,
            HttpMethod.Post,
            $"api/indexer/schemas/{sourceId}/sync",
            ct: ct);
    }

    public Task<IndexerAcceptedResponse> StartSyncAllSchemasAsync(string username, CancellationToken ct = default)
        => SendJsonAsync<IndexerAcceptedResponse>(
            username,
            HttpMethod.Post,
            "api/indexer/schemas/sync-all",
            ct: ct);

    public Task<IndexerAcceptedResponse> StartLinkAsync(string username, CancellationToken ct = default)
        => SendJsonAsync<IndexerAcceptedResponse>(username, HttpMethod.Post, "api/indexer/link", ct: ct);

    public Task<IndexerAcceptedResponse> StartDetectCommunitiesAsync(string username, CancellationToken ct = default)
        => SendJsonAsync<IndexerAcceptedResponse>(
            username,
            HttpMethod.Post,
            "api/indexer/communities/detect",
            ct: ct);

    public Task<IndexerAcceptedResponse> StartLinkAndDetectAsync(string username, CancellationToken ct = default)
        => SendJsonAsync<IndexerAcceptedResponse>(
            username,
            HttpMethod.Post,
            "api/indexer/link-and-detect",
            ct: ct);

    public Task<IndexerAcceptedResponse> StartProcessBatchAnalysisAsync(
        string username,
        string? repo = null,
        CancellationToken ct = default)
    {
        var path = "api/indexer/batch-analysis/process";
        if (!string.IsNullOrWhiteSpace(repo))
            path += $"?repo={Uri.EscapeDataString(repo.Trim())}";

        return SendJsonAsync<IndexerAcceptedResponse>(username, HttpMethod.Post, path, ct: ct);
    }

    public Task<IndexerRunResponse?> GetRunAsync(string username, long runId, CancellationToken ct = default)
        => SendOptionalJsonAsync<IndexerRunResponse>(
            username,
            HttpMethod.Get,
            $"api/indexer/runs/{runId}",
            ct: ct);

    public Task<IReadOnlyList<IndexerRunResponse>> ListRunsAsync(
        string username,
        string? status = null,
        string? operation = null,
        int take = 50,
        CancellationToken ct = default)
    {
        var query = BuildQueryString(
        [
            ("status", status),
            ("operation", operation),
            ("take", Math.Clamp(take, 1, 200).ToString())
        ]);

        return SendJsonAsync<IReadOnlyList<IndexerRunResponse>>(
            username,
            HttpMethod.Get,
            "api/indexer/runs" + query,
            ct: ct);
    }

    private async Task<T> SendJsonAsync<T>(
        string username,
        HttpMethod method,
        string path,
        object? body = null,
        CancellationToken ct = default)
    {
        using var response = await SendAsync(username, method, path, body, ct);
        await EnsureSuccessAsync(response, ct);
        var payload = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return payload ?? throw new InvalidOperationException($"Indexer response body for '{path}' was empty.");
    }

    private async Task<T?> SendOptionalJsonAsync<T>(
        string username,
        HttpMethod method,
        string path,
        object? body = null,
        CancellationToken ct = default)
    {
        using var response = await SendAsync(username, method, path, body, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string username,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct)
    {
        var options = Options;
        var client = httpClientFactory.CreateClient(NormalizeHttpClientName(options.HttpClientName));
        using var request = new HttpRequestMessage(method, path);
        AttachIdentityHeader(request, username);

        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
        }

        return await client.SendAsync(request, ct);
    }

    private void AttachIdentityHeader(HttpRequestMessage request, string username)
    {
        var options = Options;
        var token = tokenFactory.CreateToken(NormalizeRequired(username, nameof(username)), NormalizeRequired(options.Audience, nameof(options.Audience)));
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.TryAddWithoutValidation(CodeGraphInternalServiceAuthenticationDefaults.HeaderName, token);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = response.Content is null ? null : await response.Content.ReadAsStringAsync(ct);
        var (errorCode, message) = ParseErrorBody(body);

        throw new IndexerClientException(
            response.StatusCode,
            errorCode,
            string.IsNullOrWhiteSpace(message)
                ? $"Indexer request failed with status code {(int)response.StatusCode}."
                : message);
    }

    private static (string? ErrorCode, string? Message) ParseErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(body);
            string? errorCode = null;
            string? message = null;
            if (document.RootElement.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String)
            {
                errorCode = errorElement.GetString();
            }

            if (document.RootElement.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
            {
                message = messageElement.GetString();
            }

            return (errorCode, message);
        }
        catch (JsonException)
        {
            return (null, body);
        }
    }

    private static string BuildQueryString(IEnumerable<(string Key, string? Value)> values)
    {
        var pairs = values
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!.Trim())}")
            .ToArray();

        return pairs.Length == 0 ? string.Empty : "?" + string.Join("&", pairs);
    }

    private static string NormalizeHttpClientName(string? value)
        => string.IsNullOrWhiteSpace(value) ? IndexerClientOptions.DefaultHttpClientName : value.Trim();

    private static string NormalizeRequired(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{parameterName} is required.", parameterName);

        return value.Trim();
    }
}
