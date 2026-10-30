using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Exceptions;
using CodeGraph.Models.Messages;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Messaging;
using CodeGraph.Services.Models;
using CodeGraph.Services.Extensions;
using CodeGraph.Services.Prompts;

namespace CodeGraph.Services.Analyzers;

public partial class BatchAnalysisService(
    IGraphStore store,
    IAnalysisProviderRegistry providerRegistry,
    IMessageBus messageBus,
    IExclusionService exclusionService,
    IOptions<AnalysisOptions> optionsAccessor,
    IFileSystem fileSystem,
    ILogger<BatchAnalysisService> logger,
    IAgentPromptService? agentPromptService = null,
    IDbBackedAnalysisSettingsResolver? analysisSettingsResolver = null,
    IOptions<RepositorySourceOptions>? repositorySourceOptionsAccessor = null)
    : IBatchAnalysisService
{
    private readonly AnalysisOptions options = optionsAccessor.Value;
    private readonly RepositorySourceOptions repositorySourceOptions =
        repositorySourceOptionsAccessor?.Value ?? new RepositorySourceOptions();
    private static readonly HashSet<string> StructuralEdgeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEFINES", "DEFINES_METHOD", "CONTAINS_FILE", "CONTAINS_FOLDER", "CONTAINS_NAMESPACE", "CONTAINS_PROJECT"
    };

    private static readonly JsonSerializerOptions CamelOpts = CodeGraphJsonDefaults.CamelCase;
    private const string NativeBatchExecutionMode = "native_batch";

    public async Task SubmitAnalysisBatchAsync(string repoName, string? repoPath = null,
        bool includeAllSource = false, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(repoPath))
        {
            logger.LogDebug(
                "Ignoring caller-supplied RepoPath for {Repo}; source authorization uses current repository metadata",
                repoName);
        }

        var allNodes = await store.GetAllNodesByRepositoryAsync(repoName);
        if (allNodes.Count == 0)
            throw new InvalidOperationException(
                $"No nodes found for repo '{repoName}'. Index the repo first.");

        // Every edge touching one of the repo's nodes (so cross-project edges within the repo are
        // included). Prompts only use endpoints and type, so edge properties are not loaded.
        var allEdges = await store.GetEdgeEndpointsForRepositoryAsync(repoName);

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

        var analysisSettings = await GetAnalysisSettingsAsync(ct);
        var nodeById = allNodes.ToDictionary(n => n.Id);
        var batchRequests = new List<AnalysisBatchRequestItem>();
        var batchRequestEntities = new List<AnalysisBatchRequestEntity>();
        var provider = providerRegistry.GetProvider(analysisSettings.DefaultProvider);

        foreach (var (projectName, repositoryNodes) in nodesByProject)
        {
            var prompt = await BuildProjectPromptAsync(
                repoName,
                projectName,
                repositoryNodes,
                allEdges,
                nodeById,
                provider,
                analysisSettings,
                includeAllSource);
            // custom_id: alphanumeric/hyphens/underscores only, max 64 chars
            var customId = SanitizeCustomId($"proj_{repoName}_{projectName}");

            var requestPayload = new AnalysisBatchRequestPayload(
                new AnalysisPrompt(
                    await GetRepositoryAnalysisSystemPromptAsync("project analysis"),
                    prompt),
                new AnalysisRequestOptions(
                    Model: analysisSettings.DefaultModel,
                    MaxTokens: analysisSettings.MaxTokensPerAnalysis));

            batchRequests.Add(new AnalysisBatchRequestItem(
                customId,
                requestPayload.Prompt));

            batchRequestEntities.Add(new AnalysisBatchRequestEntity
            {
                Sequence = batchRequestEntities.Count,
                CustomId = customId,
                NodeId = null,
                NodeLabel = projectName, // Store full project name for result processing
                RequestPayloadJson = JsonSerializer.Serialize(requestPayload, CamelOpts),
                Status = "pending"
            });
        }

        if (!provider.Capabilities.SupportsBatch)
        {
            throw new InvalidOperationException(
                $"Analysis provider '{provider.ProviderName}' does not support batch execution.");
        }

        var executionMode = NativeBatchExecutionMode;

        var providerBatchId = (await provider.SubmitBatchAsync(
            batchRequests,
            new AnalysisRequestOptions(
                Model: analysisSettings.DefaultModel,
                MaxTokens: analysisSettings.MaxTokensPerAnalysis),
            ct)).BatchId;

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

        // Ask for an immediate completion check. The batch is already recorded as 'submitted', and
        // the periodic ProcessBatchAnalysis job sweeps every submitted batch, so a failed publish
        // only delays processing — it must not report the (successful) submission as failed.
        try
        {
            await messageBus.PublishAsync(new AnalysisBatchSubmitted
            {
                RepoName = repoName,
                ProviderBatchId = providerBatchId,
                RequestCount = batchRequests.Count
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not publish the completion check for batch {ProviderBatchId} ({Repo}); the periodic batch-analysis job will process it",
                providerBatchId, repoName);
        }
    }

    public async Task ProcessCompletedBatchesAsync(string? repo = null, CancellationToken ct = default)
    {
        await ProcessCompletedBatchesCoreAsync(repo, providerBatchId: null, ct);
    }

    public async Task ProcessCompletedBatchAsync(string repoName, string providerBatchId, CancellationToken ct = default)
    {
        await ProcessCompletedBatchesCoreAsync(repoName, providerBatchId, ct);
    }

    private async Task ProcessCompletedBatchesCoreAsync(string? repo, string? providerBatchId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var pendingBatches = await store.GetPendingBatchesAsync(repo);
        if (!string.IsNullOrWhiteSpace(providerBatchId))
        {
            pendingBatches = pendingBatches
                .Where(b => string.Equals(b.ProviderBatchId, providerBatchId, StringComparison.Ordinal))
                .ToList();
        }

        if (pendingBatches.Count == 0)
        {
            logger.LogInformation(
                "No pending analysis batches found{Scope}{BatchScope}",
                repo is null ? "" : $" for {repo}",
                string.IsNullOrWhiteSpace(providerBatchId) ? "" : $" with batch id {providerBatchId}");
            return;
        }

        logger.LogInformation("Checking {Count} pending batch(es)", pendingBatches.Count);

        foreach (var pending in pendingBatches)
        {
            ct.ThrowIfCancellationRequested();
            var completed = 0;
            string? providerFailure = null;
            var shouldPublish = false;
            try
            {
                var batchRequests = await store.GetBatchRequestsAsync(pending.Id);
                // Submission saves the parent before its requests; a concurrent sweep must wait.
                if (pending.RequestCount > 0 && batchRequests.Count < pending.RequestCount)
                    continue;

                var provider = providerRegistry.GetProvider(pending.ProviderName);
                var status = await provider.GetBatchStatusAsync(pending.ProviderBatchId, ct);
                ct.ThrowIfCancellationRequested();
                if (status.IsFailed)
                    providerFailure = $"Provider {pending.ProviderName} batch {pending.ProviderBatchId} {status.ProcessingStatus}: " +
                        (status.Error ?? "No provider error details were supplied.");

                if (!status.IsCompleted && !status.IsFailed)
                {
                    logger.LogDebug("Batch {Id} status: {Status} — skipping", pending.ProviderBatchId, status.ProcessingStatus);
                    continue;
                }

                var results = await provider.GetBatchResultsAsync(pending.ProviderBatchId,
                    batchRequests.Select(r => r.CustomId).ToList(), ct);
                var errors = new List<string>();
                if (providerFailure is not null) errors.Add(providerFailure);
                var requestsById = batchRequests.ToDictionary(r => r.CustomId, StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (batchRequests.Count == 0 || batchRequests.Count != pending.RequestCount)
                    errors.Add($"Expected {pending.RequestCount} stored requests, found {batchRequests.Count}.");

                foreach (var result in results)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(result.CustomId) || !requestsById.TryGetValue(result.CustomId, out var request))
                    {
                        errors.Add($"Unexpected result request '{result.CustomId}': {result.Error ?? "no matching stored request"}.");
                        continue;
                    }
                    if (!seen.Add(result.CustomId))
                    {
                        errors.Add($"Duplicate result for request '{result.CustomId}'. {result.Error}");
                        continue;
                    }

                    string? error;
                    try
                    {
                        error = await ProcessBatchResultAsync(result, pending.Id, pending.Repo,
                            request.NodeLabel, Math.Max(1, request.AttemptCount), ct);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
                    {
                        error = ex.ToString();
                        await store.UpdateBatchRequestStateAsync(pending.Id, result.CustomId, "errored",
                            Math.Max(1, request.AttemptCount), result.Text, result.ModelUsed, DateTime.UtcNow);
                    }
                    if (error is null)
                        completed++;
                    else
                        errors.Add($"Request '{result.CustomId}': {error}");
                }

                foreach (var request in batchRequests.Where(r => !seen.Contains(r.CustomId)))
                {
                    errors.Add($"Request '{request.CustomId}': provider returned no result.");
                    await store.UpdateBatchRequestStateAsync(pending.Id, request.CustomId, "errored",
                        Math.Max(1, request.AttemptCount), null, null, DateTime.UtcNow);
                }

                ct.ThrowIfCancellationRequested();
                var failed = errors.Count > 0;
                await store.UpdateBatchStatusAsync(pending.Id, failed ? "failed" : "completed", completed, DateTime.UtcNow,
                    failed ? $"{completed}/{pending.RequestCount} requests succeeded. " + string.Join(" ", errors) : null);
                logger.LogInformation("Batch {Id}: {Count}/{Total} succeeded; failed: {Failed}",
                    pending.ProviderBatchId, completed, pending.RequestCount, failed);

                shouldPublish = !failed && completed > 0;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Failed to process analysis batch {Id} on repo {Repo}",
                    pending.ProviderBatchId, pending.Repo);
                await store.UpdateBatchStatusAsync(pending.Id, "failed", completed, DateTime.UtcNow,
                    providerFailure is null ? ex.ToString() : providerFailure + " " + ex);
            }

            // A partial failure retains its useful output but must not advertise a successful batch.
            // Keep publication outside the failure handler: a bus outage is not an analysis failure.
            if (shouldPublish)
            {
                var current = await store.GetBatchByProviderBatchIdAsync(pending.ProviderBatchId);
                if (current?.Status == "completed")
                    await messageBus.PublishAsync(new ProjectAnalysisResultsProcessed
                    {
                        RepoName = pending.Repo,
                        ProviderBatchId = pending.ProviderBatchId,
                        CompletedCount = completed
                    }, ct);
            }
        }
    }

    // Null means the complete request result was valid and stored; a reason means it failed.
    private async Task<string?> ProcessBatchResultAsync(AnalysisBatchItemResult result, long batchRecordId, string repoName,
        string projectName, int attemptCount, CancellationToken ct)
    {
        if (!string.Equals(result.Status, "succeeded", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(result.Error) || string.IsNullOrWhiteSpace(result.Text))
        {
            await store.UpdateBatchRequestStateAsync(batchRecordId, result.CustomId, "errored", attemptCount,
                responseText: result.Text, modelUsed: result.ModelUsed, completedAt: DateTime.UtcNow);
            return result.Error ?? (string.IsNullOrWhiteSpace(result.Text)
                ? $"{result.Status}: empty output." : $"Provider returned status '{result.Status}'.");
        }

        ProjectAnalysisResult? parsed;
        string? error = null;
        try
        {
            parsed = JsonSerializer.Deserialize<ProjectAnalysisResult>(result.Text.NormalizeJsonResponse(), CamelOpts);
        }
        catch (JsonException ex)
        {
            // Keep salvageable data, but truncated/malformed output is still a failed request.
            parsed = TryRepairTruncatedProjectJson(result.Text);
            error = $"Invalid JSON output: {ex.Message}";
        }

        var validNodes = parsed?.Nodes?.Where(n => n is not null && n.NodeId > 0 && !string.IsNullOrWhiteSpace(n.Description)).ToList() ?? [];
        if (parsed is null || (string.IsNullOrWhiteSpace(parsed.ProjectSummary) && validNodes.Count == 0))
        {
            await store.UpdateBatchRequestStateAsync(batchRecordId, result.CustomId, "errored", attemptCount,
                result.Text, result.ModelUsed, DateTime.UtcNow);
            return error ?? "Output contains no project summary or valid node descriptions.";
        }
        if (parsed.Nodes?.Any(n => n is null || n.NodeId <= 0 || string.IsNullOrWhiteSpace(n.Description)) == true)
            error = "Output contains invalid node descriptions or node identifiers.";

        var analysisSettings = await GetAnalysisSettingsAsync(ct);
        var model = result.ModelUsed ?? analysisSettings.DefaultModel;
        if (!string.IsNullOrWhiteSpace(parsed.ProjectSummary))
        {
            var confidence = parsed.Confidence.TryParseEnum<ConfidenceLevel>() ?? ConfidenceLevel.Medium;
            await store.UpsertProjectAnalysisAsync(repoName, new StoredProjectAnalysis(
                Repo: repoName, ProjectName: projectName, Summary: parsed.ProjectSummary, Confidence: confidence,
                Endpoints: Array.Empty<StoredEndpoint>(), Services: Array.Empty<StoredService>(),
                ExternalDependencies: Array.Empty<string>(), DatabaseTables: Array.Empty<string>(),
                ModelUsed: model, UpdatedAt: DateTime.UtcNow));
        }

        foreach (var node in validNodes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await store.UpsertNodeAnalysisAsync(new NodeAnalysisEntity
                {
                    NodeId = node.NodeId, Description = node.Description,
                    Confidence = node.Confidence ?? "medium", ModelUsed = model
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Failed to upsert node analysis for NodeId {NodeId} in {Repo}", node.NodeId, repoName);
                error = (error is null ? "" : error + " ") + $"Node {node.NodeId}: {ex}";
            }
        }

        await store.UpdateBatchRequestStateAsync(batchRecordId, result.CustomId, error is null ? "succeeded" : "errored",
            attemptCount, result.Text, result.ModelUsed, DateTime.UtcNow);
        return error;
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

    private Task<LlmAnalysisRuntimeConfig> GetAnalysisSettingsAsync(CancellationToken ct) =>
        analysisSettingsResolver is null
            ? Task.FromResult(LlmAnalysisRuntimeConfig.FromOptions(options))
            : analysisSettingsResolver.GetAnalysisAsync(ct);
}
