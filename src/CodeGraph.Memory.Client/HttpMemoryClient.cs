using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeGraph.Host.Shared.Auth;
using CodeGraph.Models.Memory;
using Microsoft.Extensions.Options;

namespace CodeGraph.Memory.Client;

public sealed class HttpMemoryClient(
    IHttpClientFactory httpClientFactory,
    IOptions<MemoryClientOptions> optionsAccessor,
    IInternalServiceTokenFactory tokenFactory) : IMemoryClient
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private MemoryClientOptions Options => optionsAccessor.Value;

    public Task<MemoryStoreAcceptedResult> QueueClaimsAsync(
        string username,
        MemoryClaimExtractionResult extraction,
        string source = "api",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        var path = "api/memory/claims/store" + BuildQueryString([("source", source)]);
        return SendJsonAsync<MemoryStoreAcceptedResult>(username, HttpMethod.Post, path, extraction, ct);
    }

    public Task<MemoryWriteReceipt?> GetWriteStatusAsync(string username, string receiptId, CancellationToken ct = default)
        => SendOptionalJsonAsync<MemoryWriteReceipt>(
            username,
            HttpMethod.Get,
            $"api/memory/writes/{Uri.EscapeDataString(NormalizeRequired(receiptId, nameof(receiptId)))}",
            ct: ct,
            allowRetry: true);

    public Task<MemoryWriteDiagnosticsResult> GetWriteDiagnosticsAsync(
        string username,
        int staleAfterMinutes = 15,
        int sampleLimit = 10,
        CancellationToken ct = default)
    {
        var path = "api/memory/writes/diagnostics" + BuildQueryString(
        [
            ("staleAfterMinutes", Math.Clamp(staleAfterMinutes, 1, 1440).ToString()),
            ("sampleLimit", Math.Clamp(sampleLimit, 1, 100).ToString())
        ]);

        return SendJsonAsync<MemoryWriteDiagnosticsResult>(username, HttpMethod.Get, path, ct: ct, allowRetry: true);
    }

    public Task<MemoryDiagnosticsResult> GetDiagnosticsAsync(
        string username,
        int staleAfterMinutes = 15,
        int sampleLimit = 10,
        CancellationToken ct = default)
    {
        var path = "api/memory/diagnostics" + BuildQueryString(
        [
            ("staleAfterMinutes", Math.Clamp(staleAfterMinutes, 1, 1440).ToString()),
            ("sampleLimit", Math.Clamp(sampleLimit, 1, 100).ToString())
        ]);

        return SendJsonAsync<MemoryDiagnosticsResult>(username, HttpMethod.Get, path, ct: ct, allowRetry: true);
    }

    public Task<MemoryCleanupResult> DeleteBySourceAsync(
        string username,
        string source,
        bool dryRun,
        CancellationToken ct = default)
        => SendJsonAsync<MemoryCleanupResult>(
            username,
            HttpMethod.Post,
            "api/memory/cleanup/by-source",
            new MemoryCleanupBySourceRequest
            {
                Source = NormalizeRequired(source, nameof(source)),
                DryRun = dryRun,
            },
            ct);

    public Task<MemoryCleanupResult> DeleteTestDataAsync(
        string username,
        bool dryRun,
        CancellationToken ct = default)
        => SendJsonAsync<MemoryCleanupResult>(
            username,
            HttpMethod.Post,
            "api/memory/cleanup/test-data",
            new MemoryCleanupTestDataRequest
            {
                DryRun = dryRun,
            },
            ct);

    public Task<MemoryCleanupResult> DeleteByIdsAsync(
        string username,
        IReadOnlyList<string> claimIds,
        IReadOnlyList<string> entityIds,
        bool dryRun,
        CancellationToken ct = default)
        => SendJsonAsync<MemoryCleanupResult>(
            username,
            HttpMethod.Post,
            "api/memory/cleanup/by-ids",
            new MemoryCleanupByIdsRequest
            {
                ClaimIds = NormalizeIds(claimIds),
                EntityIds = NormalizeIds(entityIds),
                DryRun = dryRun,
            },
            ct);

    public Task<MemorySearchResult> SearchAsync(
        string username,
        string query,
        int entityLimit = 5,
        int claimLimit = 5,
        CancellationToken ct = default)
    {
        var path = "api/memory/search" + BuildQueryString(
        [
            ("query", NormalizeRequired(query, nameof(query))),
            ("entityLimit", Math.Clamp(entityLimit, 1, 25).ToString()),
            ("claimLimit", Math.Clamp(claimLimit, 1, 25).ToString())
        ]);

        return SendJsonAsync<MemorySearchResult>(username, HttpMethod.Get, path, ct: ct, allowRetry: true);
    }

    public Task<MemorySubgraphResult> GetSubgraphAsync(
        string username,
        MemorySubgraphRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<MemorySubgraphResult>(
            username,
            HttpMethod.Post,
            "api/memory/subgraph",
            request,
            ct,
            allowRetry: true);
    }

    public Task<MemoryFrontierExpansionResult> ExpandFrontierAsync(
        string username,
        MemoryFrontierExpansionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<MemoryFrontierExpansionResult>(
            username,
            HttpMethod.Post,
            "api/memory/frontier/expand",
            request,
            ct,
            allowRetry: true);
    }

    public Task<MemorySummaryRenderResult> RenderSummaryAsync(
        string username,
        MemorySummaryRenderRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<MemorySummaryRenderResult>(
            username,
            HttpMethod.Post,
            "api/memory/render-summary",
            request,
            ct,
            allowRetry: true);
    }

    public Task<MemoryEntityBundle?> GetEntityBundleAsync(
        string username,
        string entityId,
        bool includeSuperseded = false,
        bool includeConflicts = true,
        int neighborLimit = 20,
        CancellationToken ct = default)
    {
        var path = $"api/memory/entities/{Uri.EscapeDataString(NormalizeRequired(entityId, nameof(entityId)))}/bundle" +
                   BuildQueryString(
                   [
                       ("includeSuperseded", includeSuperseded.ToString().ToLowerInvariant()),
                       ("includeConflicts", includeConflicts.ToString().ToLowerInvariant()),
                       ("neighborLimit", Math.Clamp(neighborLimit, 1, 500).ToString())
                   ]);

        return SendOptionalJsonAsync<MemoryEntityBundle>(username, HttpMethod.Get, path, ct: ct, allowRetry: true);
    }

    public Task<MemoryClaimBundle?> GetClaimBundleAsync(
        string username,
        string claimId,
        bool includeSupersessionChain = true,
        bool includeConflicts = true,
        bool includeEvidence = true,
        CancellationToken ct = default)
    {
        var path = $"api/memory/claims/{Uri.EscapeDataString(NormalizeRequired(claimId, nameof(claimId)))}/bundle" +
                   BuildQueryString(
                   [
                       ("includeSupersessionChain", includeSupersessionChain.ToString().ToLowerInvariant()),
                       ("includeConflicts", includeConflicts.ToString().ToLowerInvariant()),
                       ("includeEvidence", includeEvidence.ToString().ToLowerInvariant())
                   ]);

        return SendOptionalJsonAsync<MemoryClaimBundle>(username, HttpMethod.Get, path, ct: ct, allowRetry: true);
    }

    public Task<MemoryQueryResult> QueryAsync(
        string username,
        string topic,
        int hops = 2,
        int maxNodes = 20,
        CancellationToken ct = default)
    {
        var path = "api/memory/query" + BuildQueryString(
        [
            ("topic", NormalizeRequired(topic, nameof(topic))),
            ("hops", Math.Clamp(hops, 1, 5).ToString()),
            ("maxNodes", Math.Clamp(maxNodes, 1, 50).ToString())
        ]);

        return SendJsonAsync<MemoryQueryResult>(username, HttpMethod.Get, path, ct: ct, allowRetry: true);
    }

    public Task<MemoryGraphSnapshot> GetGraphAsync(
        string username,
        int limit = 200,
        int skip = 0,
        CancellationToken ct = default)
    {
        var path = "api/memory/graph" + BuildQueryString(
        [
            ("limit", Math.Clamp(limit, 1, 500).ToString()),
            ("skip", Math.Max(skip, 0).ToString())
        ]);

        return SendJsonAsync<MemoryGraphSnapshot>(username, HttpMethod.Get, path, ct: ct, allowRetry: true);
    }

    public Task<MemoryGraphSnapshot?> GetEntityGraphAsync(
        string username,
        string entityId,
        int neighborLimit = 200,
        CancellationToken ct = default)
    {
        var path = $"api/memory/entities/{Uri.EscapeDataString(NormalizeRequired(entityId, nameof(entityId)))}/graph" +
                   BuildQueryString(
                   [
                       ("neighborLimit", Math.Clamp(neighborLimit, 1, 500).ToString())
                   ]);

        return SendOptionalJsonAsync<MemoryGraphSnapshot>(username, HttpMethod.Get, path, ct: ct, allowRetry: true);
    }

    public async Task<MemoryEntityWithRelationshipsResponse?> GetEntityWithRelationshipsAsync(
        string username,
        string entityId,
        CancellationToken ct = default)
    {
        using var response = await SendAsync(
            username,
            HttpMethod.Get,
            $"api/memory/entities/{Uri.EscapeDataString(NormalizeRequired(entityId, nameof(entityId)))}",
            body: null,
            ct,
            allowRetry: true);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<MemoryEntityWithRelationshipsResponse>(JsonOptions, ct);
    }

    private async Task<T> SendJsonAsync<T>(
        string username,
        HttpMethod method,
        string path,
        object? body = null,
        CancellationToken ct = default,
        bool allowRetry = false)
    {
        using var response = await SendAsync(username, method, path, body, ct, allowRetry);
        await EnsureSuccessAsync(response, ct);
        var payload = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return payload ?? throw new InvalidOperationException($"Memory response body for '{path}' was empty.");
    }

    private async Task<T?> SendOptionalJsonAsync<T>(
        string username,
        HttpMethod method,
        string path,
        object? body = null,
        CancellationToken ct = default,
        bool allowRetry = false)
    {
        using var response = await SendAsync(username, method, path, body, ct, allowRetry);
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
        CancellationToken ct,
        bool allowRetry = false)
    {
        var maxAttempts = allowRetry ? Math.Clamp(Options.MaxTransientAttempts, 1, 5) : 1;
        for (var attempt = 1; ; attempt++)
        {
            var client = httpClientFactory.CreateClient(NormalizeHttpClientName(Options.HttpClientName));
            using var request = new HttpRequestMessage(method, path);
            AttachIdentityHeader(request, username);

            if (body is not null)
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(body, JsonOptions),
                    Encoding.UTF8,
                    "application/json");
            }

            var response = await client.SendAsync(request, ct);
            if (attempt >= maxAttempts || !IsTransient(response.StatusCode))
                return response;

            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), ct);
        }
    }

    private void AttachIdentityHeader(HttpRequestMessage request, string username)
    {
        var token = tokenFactory.CreateToken(
            NormalizeRequired(username, nameof(username)),
            NormalizeRequired(Options.Audience, nameof(Options.Audience)));
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.TryAddWithoutValidation(CodeGraphInternalServiceAuthenticationDefaults.HeaderName, token);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = response.Content is null ? null : await response.Content.ReadAsStringAsync(ct);
        var (errorCode, message) = ParseErrorBody(body);

        throw new MemoryClientException(
            response.StatusCode,
            errorCode,
            string.IsNullOrWhiteSpace(message)
                ? $"Memory request failed with status code {(int)response.StatusCode}."
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
        => string.IsNullOrWhiteSpace(value) ? MemoryClientOptions.DefaultHttpClientName : value.Trim();

    private static string NormalizeRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{parameterName} is required.", parameterName);

        return value.Trim();
    }

    private static IReadOnlyList<string> NormalizeIds(IEnumerable<string>? ids)
        => ids?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList()
           ?? [];

    private static bool IsTransient(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
