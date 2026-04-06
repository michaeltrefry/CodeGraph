using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Data.Neo4j;
using CodeGraph.Models;
using CodeGraph.Services.Embeddings;

LoadDotEnv();

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var logger = loggerFactory.CreateLogger("CodeGraph.Api.Console");

if (args.Length == 0)
{
    return 1;
}

var command = args[0].ToLowerInvariant();

switch (command)
{
    case "migrate":
        return await RunMigrate(args);
    case "migrate-to-neo4j":
        return await RunMigrateToNeo4j();
    default:
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Available commands: migrate, migrate-to-neo4j");
        return 1;
}

async Task<int> RunMigrate(string[] args)
{
    var provider = Environment.GetEnvironmentVariable("CODEGRAPH_PROVIDER") ?? "mysql";

    if (provider.Equals("neo4j", StringComparison.OrdinalIgnoreCase))
    {
        var migrationsPath = args.Length > 1
            ? args[1]
            : Path.Combine(FindRepoRoot(), "cypher", "migrations");

        logger.LogInformation("Applying Neo4j migrations from {Path}", migrationsPath);

        var store = BuildNeo4jStore();
        try
        {
            await store.ApplyMigrationsAsync(migrationsPath);
            logger.LogInformation("Neo4j migrations complete");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Neo4j migration failed");
            return 1;
        }
    }
    else
    {
        var connectionString = GetConnectionString();

        var migrationsPath = args.Length > 1
            ? args[1]
            : Path.Combine(FindRepoRoot(), "sql", "migrations");

        logger.LogInformation("Applying migrations from {Path}", migrationsPath);
        logger.LogInformation("Using connection: {Server}", RedactConnectionString(connectionString));

        var store = BuildMySqlStore(connectionString);

        try
        {
            await store.ApplyMigrationsAsync(migrationsPath);
            logger.LogInformation("Migrations complete");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Migration failed");
            return 1;
        }
    }
}

async Task<int> RunMigrateToNeo4j()
{
    logger.LogInformation("Migrating data from MySQL to Neo4j...");

    var connectionString = GetConnectionString();
    var mysqlStore = BuildMySqlStore(connectionString);
    var neo4jStore = BuildNeo4jStore();

    try
    {
        // Step 1: Apply Neo4j schema migrations
        logger.LogInformation("Step 1: Applying Neo4j schema...");
        var cypherPath = Path.Combine(FindRepoRoot(), "cypher", "migrations");
        await neo4jStore.ApplyMigrationsAsync(cypherPath);

        // Step 2: Migrate repositories
        logger.LogInformation("Step 2: Migrating repositories...");
        var repos = await mysqlStore.ListRepositoriesAsync();
        foreach (var repo in repos)
        {
            await neo4jStore.UpsertRepositoryAsync(new RepositoryEntity
            {
                Name = repo.Name,
                RepoUrl = repo.RepoUrl,
                GitLabGroup = repo.GitLabGroup,
                LocalPath = repo.LocalPath,
                LastCommitSha = repo.LastCommitSha,
                IndexedAt = repo.IndexedAt,
                Language = repo.Language,
                Framework = repo.Framework,
                IsFoundational = repo.IsFoundational
            });
        }
        logger.LogInformation("Migrated {Count} repositories", repos.Count);

        // Step 3: Migrate nodes (batch by project)
        logger.LogInformation("Step 3: Migrating nodes...");
        var totalNodes = 0;
        foreach (var repo in repos)
        {
            var nodes = await mysqlStore.SearchNodesAsync(repo.Name, "", limit: 100000);
            if (nodes.Count == 0) continue;
            await neo4jStore.UpsertNodeBatchAsync(nodes);
            totalNodes += nodes.Count;
            logger.LogInformation("  {Project}: {Count} nodes", repo.Name, nodes.Count);
        }
        logger.LogInformation("Migrated {Count} total nodes", totalNodes);

        // Step 4: Migrate edges (batch by project, single query per project)
        logger.LogInformation("Step 4: Migrating edges...");
        var totalEdges = 0;
        foreach (var repo in repos)
        {
            var edgeEntities = await mysqlStore.GetAllEdgesByProjectAsync(repo.Name);
            if (edgeEntities.Count == 0) continue;

            var edges = edgeEntities.Select(e => new GraphEdge
            {
                Id = e.Id,
                Project = e.Project,
                SourceId = e.SourceId,
                TargetId = e.TargetId,
                Type = Enum.Parse<EdgeType>(e.Type),
                Properties = new Dictionary<string, object>()
            }).ToList();

            await neo4jStore.InsertEdgeBatchAsync(edges);
            totalEdges += edges.Count;
            logger.LogInformation("  {Project}: {Count} edges", repo.Name, edges.Count);
        }
        logger.LogInformation("Migrated {Count} total edges", totalEdges);

        // Step 5: Migrate cross-repo edges
        logger.LogInformation("Step 5: Migrating cross-repo edges...");
        var crossRepoEdges = await mysqlStore.GetAllCrossRepoEdgesAsync();
        if (crossRepoEdges.Count > 0)
        {
            await neo4jStore.InsertCrossRepoEdgeBatchAsync(crossRepoEdges);
        }
        logger.LogInformation("Migrated {Count} cross-repo edges", crossRepoEdges.Count);

        // Step 6: Migrate sync state
        logger.LogInformation("Step 6: Migrating sync state & file hashes...");
        foreach (var repo in repos)
        {
            var syncState = await mysqlStore.GetSyncStateAsync(repo.Name);
            if (syncState is not null)
                await neo4jStore.UpsertSyncStateAsync(syncState);

            var fileHashes = await mysqlStore.GetFileHashesAsync(repo.Name);
            if (fileHashes.Count > 0)
                await neo4jStore.UpsertFileHashBatchAsync(repo.Name, fileHashes);
        }

        // Step 7: Migrate analysis data
        logger.LogInformation("Step 7: Migrating analysis data...");
        foreach (var repo in repos)
        {
            var summary = await mysqlStore.GetRepositorySummaryAsync(repo.Name);
            if (summary is not null)
                await neo4jStore.UpsertRepositorySummaryAsync(
                    summary.Project, summary.Summary, summary.Confidence, summary.SourceHash, summary.ModelUsed);

            var analyses = await mysqlStore.GetProjectAnalysesAsync(repo.Name);
            foreach (var analysis in analyses)
                await neo4jStore.UpsertProjectAnalysisAsync(repo.Name, analysis);
        }

        // Step 8: Migrate health data
        logger.LogInformation("Step 8: Migrating health & security data...");
        foreach (var repo in repos)
        {
            var healthSummaries = await mysqlStore.GetProjectHealthSummariesAsync(repo.Name);
            foreach (var hs in healthSummaries)
                await neo4jStore.UpsertProjectHealthSummaryAsync(hs);

            var healthAnalyses = await mysqlStore.GetProjectHealthAnalysesAsync(repo.Name);
            foreach (var ha in healthAnalyses)
                await neo4jStore.UpsertProjectHealthAnalysisAsync(ha);

            var secFindings = await mysqlStore.GetSecurityFindingsAsync(repo.Name);
            if (secFindings.Count > 0)
                await neo4jStore.UpsertSecurityFindingsBatchAsync(repo.Name, secFindings.ToList());

            var secSummary = await mysqlStore.GetProjectSecuritySummaryAsync(repo.Name);
            if (secSummary is not null)
                await neo4jStore.UpsertProjectSecuritySummaryAsync(secSummary);

            var fileMetrics = await mysqlStore.GetFileMetricsAsync(repo.Name);
            if (fileMetrics.Count > 0)
                await neo4jStore.UpsertFileMetricsBatchAsync(repo.Name, fileMetrics.ToList());
        }

        // Step 9: Migrate clusters
        logger.LogInformation("Step 9: Migrating clusters...");
        var clusters = await mysqlStore.GetRepoClustersAsync();
        if (clusters.Count > 0)
            await neo4jStore.ReplaceRepoClustersAsync(clusters.ToList());

        // Step 10: Migrate exclusion rules
        logger.LogInformation("Step 10: Migrating exclusion rules...");
        var exclusions = await mysqlStore.ListExclusionRulesAsync();
        foreach (var rule in exclusions)
            await neo4jStore.CreateExclusionRuleAsync(rule);

        // Step 11: Generate embeddings for existing nodes
        logger.LogInformation("Step 11: Generating embeddings...");
        var storageOpts = BuildNeo4jStorageOptions();
        var embeddingService = new OnnxEmbeddingService(storageOpts, loggerFactory.CreateLogger<OnnxEmbeddingService>());
        if (embeddingService.IsAvailable)
        {
            var sessionFactory = new Neo4jSessionFactory(storageOpts);
            var vectorStore = new Neo4jVectorStore(sessionFactory);
            var semanticSearch = new SemanticSearchService(vectorStore, neo4jStore, embeddingService,
                loggerFactory.CreateLogger<SemanticSearchService>());

            var totalEmbeddings = 0;
            foreach (var repo in repos)
            {
                var nodes = await neo4jStore.SearchNodesAsync(repo.Name, "", limit: 100000);
                if (nodes.Count == 0) continue;

                // Single batch query for all node analyses instead of N+1
                var nodeIds = nodes.Select(n => n.Id).ToList();
                var analyses = await neo4jStore.GetNodeAnalysesBatchAsync(nodeIds);

                var items = nodes.Select(n =>
                {
                    analyses.TryGetValue(n.Id, out var analysis);
                    return (node: n, description: analysis?.Description);
                }).ToList();

                await semanticSearch.IndexNodeBatchAsync(items);
                totalEmbeddings += items.Count;
                logger.LogInformation("  {Project}: {Count} embeddings", repo.Name, items.Count);
            }
            logger.LogInformation("Generated {Count} total embeddings", totalEmbeddings);
        }
        else
        {
            logger.LogWarning("No ONNX embedding model configured — skipping embedding generation. " +
                "Set CODEGRAPH_EMBEDDING_MODEL to an .onnx file path to enable.");
        }

        logger.LogInformation("Migration complete! All data has been transferred from MySQL to Neo4j.");
        return 0;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Migration to Neo4j failed");
        return 1;
    }
}

// --- Helpers ---

string GetConnectionString()
    => Environment.GetEnvironmentVariable("CODEGRAPH_MYSQL")
       ?? "Server=localhost;Database=codegraph;User=root;Password=;SslMode=None";

MySqlGraphStore BuildMySqlStore(string connectionString)
{
    var storageOptions = new CodeGraphStorageOptions
    {
        ConnectionString = connectionString,
        MigrationsPath = Path.Combine(FindRepoRoot(), "sql", "migrations")
    };

    var serverVersion = ServerVersion.AutoDetect(connectionString);
    var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
        .UseMySql(connectionString, serverVersion)
        .Options;

    var context = new CodeGraphDbContext(dbOptions);
    return new MySqlGraphStore(context, storageOptions, loggerFactory.CreateLogger<MySqlGraphStore>());
}

CodeGraphStorageOptions BuildNeo4jStorageOptions() => new()
{
    Provider = "neo4j",
    Neo4jUri = Environment.GetEnvironmentVariable("CODEGRAPH_NEO4J_URI") ?? "bolt://localhost:7687",
    Neo4jUsername = Environment.GetEnvironmentVariable("CODEGRAPH_NEO4J_USER") ?? "neo4j",
    Neo4jPassword = Environment.GetEnvironmentVariable("CODEGRAPH_NEO4J_PASSWORD") ?? "codegraph",
    Neo4jDatabase = Environment.GetEnvironmentVariable("CODEGRAPH_NEO4J_DATABASE"),
    Neo4jMigrationsPath = Path.Combine(FindRepoRoot(), "cypher", "migrations"),
    EmbeddingModelPath = Environment.GetEnvironmentVariable("CODEGRAPH_EMBEDDING_MODEL"),
    EmbeddingDimensions = int.TryParse(Environment.GetEnvironmentVariable("CODEGRAPH_EMBEDDING_DIMENSIONS"), out var d) ? d : 384
};

Neo4jGraphStore BuildNeo4jStore()
{
    var storageOptions = BuildNeo4jStorageOptions();
    var factory = new Neo4jSessionFactory(storageOptions);
    return new Neo4jGraphStore(factory, storageOptions, loggerFactory.CreateLogger<Neo4jGraphStore>());
}

void LoadDotEnv()
{
    var path = GetEnvFile();
    if (path == null) return;

    foreach (var line in File.ReadAllLines(path))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

        var eq = trimmed.IndexOf('=');
        if (eq <= 0) continue;

        var key = trimmed[..eq].Trim();
        var value = trimmed[(eq + 1)..].Trim();

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1];

        Environment.SetEnvironmentVariable(key, value);
    }
}

string? GetEnvFile()
{
    var dir = Directory.GetCurrentDirectory();
    var path = Path.Combine(dir, ".env");
    while (!File.Exists(path))
    {
        var parent = Directory.GetParent(dir);
        if (parent == null || !parent.Exists) break;
        dir = parent.FullName;
        path = Path.Combine(dir, ".env");
    }
    return !File.Exists(path) ? null : path;
}

string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
        dir = Directory.GetParent(dir)?.FullName;
    return dir ?? Directory.GetCurrentDirectory();
}

string RedactConnectionString(string cs)
{
    var parts = cs.Split(';').Select(p =>
        p.TrimStart().StartsWith("Password", StringComparison.OrdinalIgnoreCase)
            ? "Password=***"
            : p);
    return string.Join(";", parts);
}
