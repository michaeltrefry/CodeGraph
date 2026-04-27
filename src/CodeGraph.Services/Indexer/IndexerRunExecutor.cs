using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Services.DatabaseSchema;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Indexer;

public sealed class IndexerRunExecutor(
    IIndexerRunStore runStore,
    IDatabaseSourceStore databaseSourceStore,
    IDatabaseSchemaExtractor databaseSchemaExtractor,
    IAdminService adminService,
    ILogger<IndexerRunExecutor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ExecuteAsync(long runId, CancellationToken ct = default)
    {
        var run = await runStore.GetIndexerRunAsync(runId, ct)
            ?? throw new InvalidOperationException($"Indexer run '{runId}' was not found.");
        if (IsTerminalStatus(run.Status))
            return;

        try
        {
            await runStore.UpdateIndexerRunStatusAsync(runId, "running", ct: ct);

            switch (run.Operation)
            {
                case IndexerRunOperations.ProcessRepositories:
                {
                    var request = DeserializeArgs<ProcessRequest>(run.ArgsJson);
                    var response = await adminService.ProcessRepositoriesAsync(request);
                    await CompleteAsync(runId, $"Published {response.Count} repositor{(response.Count == 1 ? "y" : "ies")} for processing.", ct);
                    break;
                }

                case IndexerRunOperations.ReIndexAll:
                {
                    var response = await adminService.ReIndexAllAsync();
                    await CompleteAsync(runId, $"Published {response.Count} repositor{(response.Count == 1 ? "y" : "ies")} for re-indexing.", ct);
                    break;
                }

                case IndexerRunOperations.Discover:
                {
                    var request = DeserializeArgs<DiscoverRequest>(run.ArgsJson);
                    var response = await adminService.DiscoverAsync(request);
                    await CompleteAsync(runId, $"Discovered {response.Discovered}, matched {response.Matched}, published {response.Published}.", ct);
                    break;
                }

                case IndexerRunOperations.SyncAllSchemas:
                    await databaseSchemaExtractor.SyncAllAsync(ct);
                    await runStore.UpdateIndexerRunStatusAsync(
                        runId,
                        "completed",
                        message: "Completed database schema sync for all enabled sources.",
                        completedAt: DateTime.UtcNow,
                        ct: ct);
                    break;

                case IndexerRunOperations.SyncSchema:
                    if (!long.TryParse(run.Target, out var sourceId) || sourceId <= 0)
                        throw new InvalidOperationException($"Indexer run '{runId}' has invalid schema source target '{run.Target}'.");

                    var source = await databaseSourceStore.GetAsync(sourceId)
                        ?? throw new InvalidOperationException($"Database source '{sourceId}' was not found.");

                    await databaseSchemaExtractor.SyncAsync(source, ct);
                    await runStore.UpdateIndexerRunStatusAsync(
                        runId,
                        "completed",
                        message: $"Completed database schema sync for source {source.ServerName}/{(string.IsNullOrWhiteSpace(source.DatabaseName) ? "all databases" : source.DatabaseName)}.",
                        completedAt: DateTime.UtcNow,
                        ct: ct);
                    break;

                case IndexerRunOperations.Link:
                    await adminService.LinkAsync(ct);
                    await CompleteAsync(runId, "Completed cross-repository linking.", ct);
                    break;

                case IndexerRunOperations.DetectCommunities:
                    await adminService.DetectCommunitiesAsync(ct);
                    await CompleteAsync(runId, "Completed community detection.", ct);
                    break;

                case IndexerRunOperations.LinkAndDetect:
                    await adminService.LinkAndDetectAsync(ct);
                    await CompleteAsync(runId, "Completed cross-repository linking and community detection.", ct);
                    break;

                case IndexerRunOperations.ProcessBatchAnalysis:
                {
                    var args = DeserializeArgs<BatchAnalysisIndexerRunArgs>(run.ArgsJson);
                    await adminService.ProcessBatchAnalysisAsync(args.Repo);
                    await CompleteAsync(
                        runId,
                        args.Repo is null
                            ? "Completed batch analysis result processing."
                            : $"Completed batch analysis result processing for {args.Repo}.",
                        ct);
                    break;
                }

                default:
                    throw new InvalidOperationException($"Unsupported indexer run operation '{run.Operation}'.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Indexer run {RunId} failed for operation {Operation}", runId, run.Operation);
            await runStore.UpdateIndexerRunStatusAsync(
                runId,
                "failed",
                error: ex.Message,
                completedAt: DateTime.UtcNow,
                ct: ct);
            throw;
        }
    }

    private static bool IsTerminalStatus(string status)
        => status is "completed" or "failed";

    private async Task CompleteAsync(long runId, string message, CancellationToken ct)
        => await runStore.UpdateIndexerRunStatusAsync(
            runId,
            "completed",
            message: message,
            completedAt: DateTime.UtcNow,
            ct: ct);

    private static T DeserializeArgs<T>(string? argsJson)
        where T : new()
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return new T();

        return JsonSerializer.Deserialize<T>(argsJson, JsonOptions)
            ?? new T();
    }
}
