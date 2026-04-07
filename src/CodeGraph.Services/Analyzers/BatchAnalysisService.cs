using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Messaging;
using CodeGraph.Services.Models;
using CodeGraph.Services.Extensions;

namespace CodeGraph.Services.Analyzers;

public partial class BatchAnalysisService(
    IGraphStore store,
    IAnalysisProviderRegistry providerRegistry,
    IMessageBus messageBus,
    IExclusionService exclusionService,
    IOptions<AnalysisOptions> optionsAccessor,
    IFileSystem fileSystem,
    ILogger<BatchAnalysisService> logger)
    : IBatchAnalysisService
{
    private readonly AnalysisOptions options = optionsAccessor.Value;
    private static readonly HashSet<string> StructuralEdgeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEFINES", "DEFINES_METHOD", "CONTAINS_FILE", "CONTAINS_FOLDER", "CONTAINS_NAMESPACE", "CONTAINS_PROJECT"
    };

    private static readonly JsonSerializerOptions CamelOpts = CodeGraphJsonDefaults.CamelCase;
    private const string NativeBatchExecutionMode = "native_batch";
    private const string DirectFallbackExecutionMode = "direct_fallback";

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
        var batchRequests = new List<AnalysisBatchRequestItem>();
        var batchRequestEntities = new List<AnalysisBatchRequestEntity>();
        var provider = providerRegistry.GetProvider();

        foreach (var (projectName, projectNodes) in nodesByProject)
        {
            var prompt = await BuildProjectPromptAsync(repoName, projectName, projectNodes, allEdges, nodeById, repoPath, includeAllSource);
            // custom_id: alphanumeric/hyphens/underscores only, max 64 chars
            var customId = SanitizeCustomId($"proj_{repoName}_{projectName}");

            batchRequests.Add(new AnalysisBatchRequestItem(
                customId,
                new AnalysisPrompt(AnalysisPromptBuilder.SystemPrompt, prompt)));

            batchRequestEntities.Add(new AnalysisBatchRequestEntity
            {
                Sequence = batchRequestEntities.Count,
                CustomId = customId,
                NodeId = null,
                NodeLabel = projectName, // Store full project name for result processing
                Status = "pending"
            });
        }

        var executionMode = provider.Capabilities.SupportsBatch
            ? NativeBatchExecutionMode
            : DirectFallbackExecutionMode;

        var providerBatchId = provider.Capabilities.SupportsBatch
            ? (await provider.SubmitBatchAsync(
                batchRequests,
                new AnalysisRequestOptions(MaxTokens: options.MaxTokensPerAnalysis),
                ct)).BatchId
            : CreateDirectFallbackBatchId(provider.ProviderName, repoName);

        logger.LogInformation(
            "Queued {Count} per-project analysis request(s) for {Repo} via {Provider} ({Mode})",
            batchRequests.Count, repoName, provider.ProviderName, executionMode);

        var batchEntity = new AnalysisBatchEntity
        {
            Repo = repoName,
            ProviderBatchId = providerBatchId,
            ProviderName = provider.ProviderName,
            ExecutionMode = executionMode,
            IncludeAllSource = includeAllSource,
            Status = "submitted",
            RequestCount = batchRequests.Count,
            CompletedCount = 0,
            SubmittedAt = DateTime.UtcNow
        };

        var batchId = await store.CreateAnalysisBatchAsync(batchEntity);

        foreach (var entity in batchRequestEntities)
            entity.BatchId = batchId;
        await store.CreateBatchRequestsAsync(batchRequestEntities);

        logger.LogInformation("Batch {ProviderBatchId} submitted via {Provider} for {Repo} ({Count} projects, {Mode})",
            providerBatchId, provider.ProviderName, repoName, batchRequests.Count, executionMode);

        // Schedule a delayed check for batch completion
        await messageBus.PublishAsync(new AnalysisBatchSubmitted
        {
            RepoName = repoName,
            ProviderBatchId = providerBatchId,
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

        foreach (var pending in pendingBatches)
        {
            var provider = providerRegistry.GetProvider(pending.ProviderName);
            if (string.Equals(pending.ExecutionMode, DirectFallbackExecutionMode, StringComparison.OrdinalIgnoreCase))
            {
                await ProcessDirectFallbackBatchAsync(pending, provider, ct);
                continue;
            }

            AnalysisBatchStatusResult status;
            try
            {
                status = await provider.GetBatchStatusAsync(pending.ProviderBatchId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to retrieve batch results for {Id} on repo {Repo}",
                    pending.ProviderBatchId, pending.Repo);
                await store.UpdateBatchStatusAsync(pending.Id, "failed", 0, DateTime.UtcNow);
                continue;
            }

            if (!status.IsCompleted)
            {
                logger.LogDebug("Batch {Id} status: {Status} — skipping",
                    pending.ProviderBatchId, status.ProcessingStatus);
                continue;
            }

            logger.LogInformation("Batch {Id} completed, processing results for {Repo}",
                pending.ProviderBatchId, pending.Repo);

            // Load batch requests to map customId → project name
            var batchRequests = await store.GetBatchRequestsAsync(pending.Id);
            var projectNameByCustomId = batchRequests.ToDictionary(r => r.CustomId, r => r.NodeLabel);

            int completed = 0;
            IReadOnlyList<AnalysisBatchItemResult> results;
            try
            {
                results = await provider.GetBatchResultsAsync(
                    pending.ProviderBatchId,
                    batchRequests.Select(r => r.CustomId).ToList(),
                    ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to download batch result lines for {Id} on repo {Repo}",
                    pending.ProviderBatchId, pending.Repo);
                await store.UpdateBatchStatusAsync(pending.Id, "failed", 0, DateTime.UtcNow);
                continue;
            }

            foreach (var result in results)
            {
                var projectName = projectNameByCustomId.GetValueOrDefault(result.CustomId, result.CustomId);
                await ProcessBatchResultAsync(result, pending.Repo, pending.ProviderBatchId, projectName);
                completed++;
            }

            await store.UpdateBatchStatusAsync(pending.Id, "completed", completed, DateTime.UtcNow);
            logger.LogInformation("Batch {Id}: {Count} result(s) stored", pending.ProviderBatchId, completed);

            // Publish event to trigger synthesis and doc generation asynchronously
            if (completed > 0)
            {
                await messageBus.PublishAsync(new ProjectAnalysisResultsProcessed
                {
                    RepoName = pending.Repo,
                    ProviderBatchId = pending.ProviderBatchId,
                    CompletedCount = completed
                });
                logger.LogInformation("Published ProjectAnalysisResultsProcessed for {Repo}", pending.Repo);
            }
        }
    }

    private async Task ProcessDirectFallbackBatchAsync(
        StoredAnalysisBatch pending,
        IAnalysisModelProvider provider,
        CancellationToken ct)
    {
        logger.LogInformation(
            "Running direct-fallback analysis for batch {Id} on {Repo} via {Provider}",
            pending.ProviderBatchId, pending.Repo, provider.ProviderName);

        var batchRequests = await store.GetBatchRequestsAsync(pending.Id);
        if (batchRequests.Count == 0)
        {
            logger.LogWarning("Batch {Id} for {Repo} has no stored requests", pending.ProviderBatchId, pending.Repo);
            await store.UpdateBatchStatusAsync(pending.Id, "failed", 0, DateTime.UtcNow);
            return;
        }

        var remainingRequests = batchRequests
            .Where(r => string.Equals(r.Status, "pending", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var completed = batchRequests.Count - remainingRequests.Count;

        if (remainingRequests.Count == 0)
        {
            await store.UpdateBatchStatusAsync(pending.Id, "completed", completed, DateTime.UtcNow);
            if (completed > 0)
            {
                await messageBus.PublishAsync(new ProjectAnalysisResultsProcessed
                {
                    RepoName = pending.Repo,
                    ProviderBatchId = pending.ProviderBatchId,
                    CompletedCount = completed
                });
                logger.LogInformation("Published ProjectAnalysisResultsProcessed for {Repo}", pending.Repo);
            }
            return;
        }

        var allNodes = await store.GetAllNodesByProjectAsync(pending.Repo);
        if (allNodes.Count == 0)
        {
            logger.LogWarning("Batch {Id} for {Repo} cannot be replayed because no nodes were found",
                pending.ProviderBatchId, pending.Repo);
            await store.UpdateBatchStatusAsync(pending.Id, "failed", completed, DateTime.UtcNow);
            return;
        }

        var nodeIds = allNodes.Select(n => n.Id).ToList();
        var allEdges = await store.GetEdgesForNodesAsync(nodeIds);
        var nodeById = allNodes.ToDictionary(n => n.Id);
        var nodesByProject = allNodes
            .GroupBy(n => GetDotnetProject(n))
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .ToDictionary(g => g.Key, g => g.ToList());

        var orphanNodes = allNodes.Where(n => string.IsNullOrEmpty(GetDotnetProject(n))).ToList();
        if (orphanNodes.Count > 0 && nodesByProject.Count == 0)
            nodesByProject["unknown"] = orphanNodes;

        var repoPath = (await store.GetRepositoryByName(pending.Repo))?.LocalPath;

        foreach (var request in remainingRequests)
        {
            AnalysisBatchItemResult result;
            if (!nodesByProject.TryGetValue(request.NodeLabel, out var projectNodes))
            {
                logger.LogWarning("Batch {Id} request {CustomId} could not find project group {Project}",
                    pending.ProviderBatchId, request.CustomId, request.NodeLabel);
                result = new AnalysisBatchItemResult(request.CustomId, "errored", null, null);
            }
            else
            {
                try
                {
                    var prompt = await BuildProjectPromptAsync(
                        pending.Repo,
                        request.NodeLabel,
                        projectNodes,
                        allEdges,
                        nodeById,
                        repoPath,
                        pending.IncludeAllSource);

                    var response = await provider.ExecuteAsync(
                        new AnalysisPrompt(AnalysisPromptBuilder.SystemPrompt, prompt),
                        new AnalysisRequestOptions(MaxTokens: options.MaxTokensPerAnalysis),
                        ct);

                    result = new AnalysisBatchItemResult(
                        request.CustomId,
                        "succeeded",
                        response.Text,
                        response.ModelUsed);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Direct fallback analysis failed for batch {Id} request {CustomId} on {Repo}",
                        pending.ProviderBatchId, request.CustomId, pending.Repo);
                    result = new AnalysisBatchItemResult(request.CustomId, "errored", null, null);
                }
            }

            await ProcessBatchResultAsync(result, pending.Repo, pending.ProviderBatchId, request.NodeLabel);
            completed++;
        }

        await store.UpdateBatchStatusAsync(pending.Id, "completed", completed, DateTime.UtcNow);
        logger.LogInformation("Batch {Id}: {Count} direct-fallback result(s) stored",
            pending.ProviderBatchId, completed);

        if (completed > 0)
        {
            await messageBus.PublishAsync(new ProjectAnalysisResultsProcessed
            {
                RepoName = pending.Repo,
                ProviderBatchId = pending.ProviderBatchId,
                CompletedCount = completed
            });
            logger.LogInformation("Published ProjectAnalysisResultsProcessed for {Repo}", pending.Repo);
        }
    }

    private async Task ProcessBatchResultAsync(AnalysisBatchItemResult result, string repoName, string batchId, string projectName)
    {
        if (!string.Equals(result.Status, "succeeded", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(result.Text))
        {
            logger.LogWarning("Batch {BatchId} request {CustomId} did not succeed: {Type}",
                batchId, result.CustomId, result.Status);
            await store.UpdateBatchRequestStatusAsync(result.CustomId, "errored", DateTime.UtcNow);
            return;
        }

        ProjectAnalysisResult? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ProjectAnalysisResult>(result.Text.NormalizeJsonResponse(), CamelOpts);
        }
        catch (JsonException)
        {
            parsed = TryRepairTruncatedProjectJson(result.Text);
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

        var model = result.ModelUsed ?? options.Model;

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
    /// Sanitize a provider request id: alphanumeric, hyphens, underscores only, max 64 characters.
    /// Truncates with a hash suffix if too long.
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

    private static string CreateDirectFallbackBatchId(string providerName, string repoName)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var seed = $"{providerName}_{repoName}_{timestamp}";
        return SanitizeCustomId($"direct_{seed}_{Guid.NewGuid():N}");
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
            var json = text.NormalizeJsonResponse();

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
