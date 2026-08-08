using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Services.DatabaseSchema;

namespace CodeGraph.Services.Indexer;

public sealed class IndexerRunExecutor(
    IDatabaseSourceStore databaseSourceStore,
    IDatabaseSchemaExtractor databaseSchemaExtractor,
    IAdminService adminService,
    IProjectService projectService)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> ExecuteAsync(IndexerRunLease lease, CancellationToken ct = default)
    {
        var run = lease.Run;
        ct.ThrowIfCancellationRequested();

        switch (run.Operation)
        {
            case IndexerRunOperations.ReAnalyze:
            {
                var args = DeserializeArgs<ReAnalyzeIndexerRunArgs>(run.ArgsJson);
                if (string.IsNullOrWhiteSpace(args.Repo))
                    throw new InvalidOperationException($"Indexer run '{run.Id}' has no repository to re-analyze.");

                var batch = await projectService.ReAnalyzeRepository(args.Repo.Trim(), ct)
                    ?? throw new KeyNotFoundException($"Repository '{args.Repo.Trim()}' was not found.");
                return $"Completed re-analysis for {args.Repo.Trim()}; analysis batch {batch.Id} is {batch.Status}.";
            }

            case IndexerRunOperations.ProcessRepositories:
            {
                var request = DeserializeArgs<ProcessRequest>(run.ArgsJson);
                var response = await adminService.ProcessRepositoriesAsync(request, ct);
                return $"Published {response.Count} repositor{(response.Count == 1 ? "y" : "ies")} for processing.";
            }

            case IndexerRunOperations.ReIndexAll:
            {
                var response = await adminService.ReIndexAllAsync(ct);
                return $"Published {response.Count} repositor{(response.Count == 1 ? "y" : "ies")} for re-indexing.";
            }

            case IndexerRunOperations.Discover:
            {
                var request = DeserializeArgs<DiscoverRequest>(run.ArgsJson);
                var response = await adminService.DiscoverAsync(request, ct);
                return $"Discovered {response.Discovered}, matched {response.Matched}, published {response.Published}.";
            }

            case IndexerRunOperations.SyncAllSchemas:
                await databaseSchemaExtractor.SyncAllAsync(ct);
                return "Completed database schema sync for all enabled sources.";

            case IndexerRunOperations.SyncSchema:
                if (!long.TryParse(run.Target, out var sourceId) || sourceId <= 0)
                    throw new InvalidOperationException($"Indexer run '{run.Id}' has invalid schema source target '{run.Target}'.");

                var source = await databaseSourceStore.GetAsync(sourceId)
                    ?? throw new InvalidOperationException($"Database source '{sourceId}' was not found.");

                await databaseSchemaExtractor.SyncAsync(source, ct);
                return $"Completed database schema sync for source {source.ServerName}/{(string.IsNullOrWhiteSpace(source.DatabaseName) ? "all databases" : source.DatabaseName)}.";

            case IndexerRunOperations.Link:
                await adminService.LinkAsync(ct);
                return "Completed cross-repository linking.";

            case IndexerRunOperations.DetectCommunities:
                await adminService.DetectCommunitiesAsync(ct);
                return "Completed community detection.";

            case IndexerRunOperations.LinkAndDetect:
                await adminService.LinkAndDetectAsync(ct);
                return "Completed cross-repository linking and community detection.";

            case IndexerRunOperations.ProcessBatchAnalysis:
            {
                var args = DeserializeArgs<BatchAnalysisIndexerRunArgs>(run.ArgsJson);
                await adminService.ProcessBatchAnalysisAsync(args.Repo, ct);
                return args.Repo is null
                    ? "Completed batch analysis result processing."
                    : $"Completed batch analysis result processing for {args.Repo}.";
            }

            default:
                throw new InvalidOperationException($"Unsupported indexer run operation '{run.Operation}'.");
        }
    }

    private static T DeserializeArgs<T>(string? argsJson)
        where T : new()
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return new T();

        return JsonSerializer.Deserialize<T>(argsJson, JsonOptions) ?? new T();
    }
}
