using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Models.Messages;
using TC.CodeGraphApi.Services.Configuration;
using TC.CodeGraphApi.Services.Extensions;
using TC.CodeGraphApi.Services.Models;
using TC.Common.TcServiceStack.Queue.Abstractions;

namespace TC.CodeGraphApi.Services.Analyzers;

public partial class BatchAnalysisService(
    IGraphStore store,
    IHttpClientFactory httpClientFactory,
    AnthropicCircuitBreaker circuitBreaker,
    ITcServiceBus serviceBus,
    IExclusionService exclusionService,
    AnalysisOptions options,
    IFileSystem fileSystem,
    ILogger<BatchAnalysisService> logger)
    : IBatchAnalysisService
{
    private string BaseUrl => options.BatchApiBaseUrl;
    private string AnthropicVersion => options.AnthropicVersion;

    private static readonly HashSet<string> StructuralEdgeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEFINES", "CONTAINS_FILE", "CONTAINS_FOLDER", "CONTAINS_NAMESPACE"
    };

    private static readonly JsonSerializerOptions CamelOpts = CodeGraphJsonDefaults.CamelCase;
    private static readonly JsonSerializerOptions SnakeOpts = CodeGraphJsonDefaults.SnakeCase;

    public async Task SubmitAnalysisBatchAsync(string repoName, string? repoPath = null,
        bool includeAllSource = false, CancellationToken ct = default)
    {
        var allNodes = await store.GetAllNodesByProjectAsync(repoName);
        if (allNodes.Count == 0)
            throw new InvalidOperationException(
                $"No nodes found for repo '{repoName}'. Index the repo first.");

        // Fetch edges by node IDs (not project) so cross-project edges within the repo are included
        var nodeIds = allNodes.Select(n => n.Id).ToList();
        var allEdges = await store.GetEdgesForNodesAsync(nodeIds);

        // Group nodes by DotnetProject — one batch request per .csproj
        var nodesByProject = allNodes
            .GroupBy(n => GetDotnetProject(n))
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Nodes with no project go into a catch-all group
        var orphanNodes = allNodes.Where(n => string.IsNullOrEmpty(GetDotnetProject(n))).ToList();
        if (orphanNodes.Count > 0 && nodesByProject.Count == 0)
        {
            // No project structure at all — fall back to a single "unknown" group
            nodesByProject["unknown"] = orphanNodes;
        }

        logger.LogInformation(
            "Building per-project analysis batch for {Repo}: {NodeCount} node(s), {EdgeCount} edge(s), {ProjectCount} project(s){Mode}",
            repoName, allNodes.Count, allEdges.Count, nodesByProject.Count,
            includeAllSource ? " (all source)" : "");

        var nodeById = allNodes.ToDictionary(n => n.Id);
        var batchRequests = new List<BatchRequest>();
        var batchRequestEntities = new List<AnalysisBatchRequestEntity>();

        foreach (var (projectName, projectNodes) in nodesByProject)
        {
            var prompt = await BuildProjectPromptAsync(repoName, projectName, projectNodes, allEdges, nodeById, repoPath, includeAllSource);
            // custom_id: alphanumeric/hyphens/underscores only, max 64 chars
            var customId = SanitizeCustomId($"proj_{repoName}_{projectName}");

            batchRequests.Add(new BatchRequest
            {
                CustomId = customId,
                Params = new BatchRequestParams
                {
                    Model = options.Model,
                    MaxTokens = options.MaxTokensPerAnalysis,
                    Messages = [new BatchMessage { Role = "user", Content = prompt }]
                }
            });

            batchRequestEntities.Add(new AnalysisBatchRequestEntity
            {
                CustomId = customId,
                NodeId = null,
                NodeLabel = projectName, // Store full project name for result processing
                Status = "pending"
            });
        }

        logger.LogInformation("Submitting {Count} per-project request(s) to Anthropic for {Repo}",
            batchRequests.Count, repoName);

        var http = httpClientFactory.CreateClient();
        using var response = await circuitBreaker.ExecuteAsync(http,
            () => CreateRequest(HttpMethod.Post, BaseUrl, new { requests = batchRequests }), ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Anthropic batch submission failed {Status}: {Body}",
                (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var created = await response.Content.ReadFromJsonAsync<BatchCreatedResponse>(SnakeOpts, ct)
            ?? throw new InvalidOperationException("Anthropic returned null batch response");

        var batchEntity = new AnalysisBatchEntity
        {
            Repo = repoName,
            AnthropicBatchId = created.Id,
            Status = "submitted",
            RequestCount = batchRequests.Count,
            CompletedCount = 0,
            SubmittedAt = DateTime.UtcNow
        };

        var batchId = await store.CreateAnalysisBatchAsync(batchEntity);

        foreach (var entity in batchRequestEntities)
            entity.BatchId = batchId;
        await store.CreateBatchRequestsAsync(batchRequestEntities);

        logger.LogInformation("Batch {AnthropicBatchId} submitted for {Repo} ({Count} projects)",
            created.Id, repoName, batchRequests.Count);

        // Schedule a delayed check for batch completion
        await serviceBus.Publish(new AnalysisBatchSubmitted
        {
            RepoName = repoName,
            AnthropicBatchId = created.Id,
            RequestCount = batchRequests.Count
        });
    }

    public async Task ProcessCompletedBatchesAsync(string? repo = null, CancellationToken ct = default)
    {
        var pendingBatches = await store.GetPendingBatchesAsync(repo);
        if (pendingBatches.Count == 0)
        {
            logger.LogInformation("No pending analysis batches found{Scope}", repo is null ? "" : $" for {repo}");
            return;
        }

        logger.LogInformation("Checking {Count} pending batch(es)", pendingBatches.Count);

        var http = httpClientFactory.CreateClient();

        foreach (var pending in pendingBatches)
        {
            using var statusResponse = await circuitBreaker.ExecuteAsync(http,
                () => CreateRequest(HttpMethod.Get, $"{BaseUrl}/{pending.AnthropicBatchId}"), ct);
            if (!statusResponse.IsSuccessStatusCode)
            {
                logger.LogError("Failed to retrieve batch results for {Id} on repo {Repo}: {Status}", pending.AnthropicBatchId, pending.Repo, statusResponse.StatusCode);
                await store.UpdateBatchStatusAsync(pending.Id, "failed", 0, DateTime.UtcNow);
                continue;
            }

            var status = await statusResponse.Content.ReadFromJsonAsync<BatchStatusResponse>(SnakeOpts, ct);
            if (status?.ProcessingStatus != "ended")
            {
                logger.LogDebug("Batch {Id} status: {Status} — skipping",
                    pending.AnthropicBatchId, status?.ProcessingStatus);
                continue;
            }

            logger.LogInformation("Batch {Id} completed, processing results for {Repo}",
                pending.AnthropicBatchId, pending.Repo);

            // Load batch requests to map customId → project name
            var batchRequests = await store.GetBatchRequestsAsync(pending.Id);
            var projectNameByCustomId = batchRequests.ToDictionary(r => r.CustomId, r => r.NodeLabel);

            int completed = 0;

            using var resultsResponse = await circuitBreaker.ExecuteAsync(http,
                () => CreateRequest(HttpMethod.Get, $"{BaseUrl}/{pending.AnthropicBatchId}/results"), ct);
            resultsResponse.EnsureSuccessStatusCode();

            await using var stream = await resultsResponse.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;

                var result = JsonSerializer.Deserialize<BatchResultLine>(line, SnakeOpts);
                if (result is null) continue;

                var projectName = projectNameByCustomId.GetValueOrDefault(result.CustomId, result.CustomId);
                await ProcessBatchResultAsync(result, pending.Repo, pending.AnthropicBatchId, projectName);
                completed++;
            }

            await store.UpdateBatchStatusAsync(pending.Id, "completed", completed, DateTime.UtcNow);
            logger.LogInformation("Batch {Id}: {Count} result(s) stored", pending.AnthropicBatchId, completed);

            // Publish event to trigger synthesis and doc generation asynchronously
            if (completed > 0)
            {
                await serviceBus.Publish(new ProjectAnalysisResultsProcessed
                {
                    RepoName = pending.Repo,
                    AnthropicBatchId = pending.AnthropicBatchId,
                    CompletedCount = completed
                });
                logger.LogInformation("Published ProjectAnalysisResultsProcessed for {Repo}", pending.Repo);
            }
        }
    }

    private async Task ProcessBatchResultAsync(BatchResultLine result, string repoName, string batchId, string projectName)
    {
        if (result.Result?.Type != "succeeded" || result.Result.Message is null)
        {
            logger.LogWarning("Batch {BatchId} request {CustomId} did not succeed: {Type}",
                batchId, result.CustomId, result.Result?.Type);
            await store.UpdateBatchRequestStatusAsync(result.CustomId, "errored", DateTime.UtcNow);
            return;
        }

        var text = result.Result.Message.Content
            .Where(c => c.Type == "text")
            .Select(c => c.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

        if (string.IsNullOrWhiteSpace(text))
        {
            logger.LogWarning("Batch {BatchId} request {CustomId}: empty response", batchId, result.CustomId);
            await store.UpdateBatchRequestStatusAsync(result.CustomId, "errored", DateTime.UtcNow);
            return;
        }

        ProjectAnalysisResult? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ProjectAnalysisResult>(text.StripCodeFences(), CamelOpts);
        }
        catch (JsonException)
        {
            parsed = TryRepairTruncatedProjectJson(text);
            if (parsed is null)
            {
                logger.LogWarning("Batch {BatchId} request {CustomId}: truncated JSON could not be repaired",
                    batchId, result.CustomId);
                await store.UpdateBatchRequestStatusAsync(result.CustomId, "errored", DateTime.UtcNow);
                return;
            }

            logger.LogWarning("Batch {BatchId} request {CustomId}: salvaged partial result (nodes: {NodeCount})",
                batchId, result.CustomId, parsed.Nodes?.Count ?? 0);
        }

        if (parsed is null) return;

        var model = result.Result.Message.Model;

        // Store per-project summary
        if (!string.IsNullOrWhiteSpace(parsed.ProjectSummary))
        {
            var confidence = ((string)parsed.Confidence).TryParseEnum<ConfidenceLevel>()
                ?? ConfidenceLevel.Medium;
            await store.UpsertProjectAnalysisAsync(repoName, new StoredProjectAnalysis(
                Repo: repoName,
                ProjectName: projectName,
                Summary: parsed.ProjectSummary,
                Confidence: confidence,
                Endpoints: Array.Empty<StoredEndpoint>(),
                Services: Array.Empty<StoredService>(),
                ExternalDependencies: Array.Empty<string>(),
                DatabaseTables: Array.Empty<string>(),
                ModelUsed: model,
                UpdatedAt: DateTime.UtcNow));
        }

        // Store per-node descriptions
        int nodeCount = 0;
        foreach (var node in parsed.Nodes ?? [])
        {
            if (string.IsNullOrWhiteSpace(node.Description)) continue;
            try
            {
                await store.UpsertNodeAnalysisAsync(new NodeAnalysisEntity
                {
                    NodeId = node.NodeId,
                    Description = node.Description,
                    Confidence = node.Confidence ?? "medium",
                    ModelUsed = model
                });
                nodeCount++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to upsert node analysis for NodeId {NodeId} in {Repo}", node.NodeId, repoName);
            }
        }

        logger.LogInformation("Stored project summary for {Project}, {NodeCount} node descriptions for {Repo}",
            projectName, nodeCount, repoName);

        await store.UpdateBatchRequestStatusAsync(result.CustomId, "succeeded", DateTime.UtcNow);
    }

    /// <summary>
    /// Sanitize a custom_id for the Anthropic Batch API: alphanumeric, hyphens,
    /// underscores only, max 64 characters. Truncates with a hash suffix if too long.
    /// </summary>
    private static string SanitizeCustomId(string raw)
    {
        // Only alphanumeric, hyphens, underscores allowed — strip everything else
        var sanitized = new string(raw.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        if (sanitized.Length <= 64)
            return sanitized;

        var hash = Math.Abs(raw.GetHashCode()).ToString("x8");
        return sanitized[..(64 - 9)] + "_" + hash;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, object? body = null)
    {
        var apiKey = options.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";

        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        if (body is not null)
            request.Content = JsonContent.Create(body, options: SnakeOpts);

        return request;
    }

    /// <summary>
    /// Attempt to repair truncated JSON from a max_tokens cutoff for per-project results.
    /// Strategy: find the last complete object in the nodes array, discard the
    /// truncated tail, and close the JSON structure.
    /// </summary>
    private static ProjectAnalysisResult? TryRepairTruncatedProjectJson(string text)
    {
        try
        {
            var json = text.StripCodeFences();

            var lastCompleteObject = json.LastIndexOf("},", StringComparison.Ordinal);
            if (lastCompleteObject < 0)
                lastCompleteObject = json.LastIndexOf('}');

            if (lastCompleteObject < 0)
                return null;

            var repaired = json[..(lastCompleteObject + 1)];

            int openBraces = 0, openBrackets = 0;
            bool inString = false;
            char prev = '\0';
            foreach (var c in repaired)
            {
                if (c == '"' && prev != '\\') inString = !inString;
                if (!inString)
                {
                    if (c == '{') openBraces++;
                    else if (c == '}') openBraces--;
                    else if (c == '[') openBrackets++;
                    else if (c == ']') openBrackets--;
                }
                prev = c;
            }

            for (int i = 0; i < openBrackets; i++) repaired += "]";
            for (int i = 0; i < openBraces; i++) repaired += "}";

            return JsonSerializer.Deserialize<ProjectAnalysisResult>(repaired, CamelOpts);
        }
        catch
        {
            return null;
        }
    }
}
