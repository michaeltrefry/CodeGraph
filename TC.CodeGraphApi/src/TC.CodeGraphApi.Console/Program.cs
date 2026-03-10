using System.IO.Hashing;
using System.Text;
using Anthropic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Services;
using TC.CodeGraphApi.Services.Models;

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var logger = loggerFactory.CreateLogger("TC.CodeGraphApi.Console");

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0].ToLowerInvariant();

switch (command)
{
    case "migrate":
        return await RunMigrate(args);
    case "index":
        return await RunIndex(args);
    case "index-all":
        return await RunIndexAll(args);
    case "stats":
        return await RunStats();
    case "analyze":
        return await RunAnalyze(args);
    case "mcp":
        return await RunMcp();
    default:
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 1;
}

async Task<int> RunMigrate(string[] args)
{
    var connectionString = GetConnectionString();

    var migrationsPath = args.Length > 1
        ? args[1]
        : Path.Combine(FindRepoRoot(), "TC.CodeGraphApi", "sql", "migrations");

    logger.LogInformation("Applying migrations from {Path}", migrationsPath);
    logger.LogInformation("Using connection: {Server}", RedactConnectionString(connectionString));

    var store = BuildStore(connectionString);

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

async Task<int> RunIndex(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: index <path> [--name <name>] [--foundational]");
        return 1;
    }

    var path = Path.GetFullPath(args[1]);
    if (!Directory.Exists(path))
    {
        Console.Error.WriteLine($"Directory not found: {path}");
        return 1;
    }

    string? name = null;
    var foundational = false;

    for (var i = 2; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--name" when i + 1 < args.Length:
                name = args[++i];
                break;
            case "--foundational":
                foundational = true;
                break;
        }
    }

    var projectName = name ?? Path.GetFileName(path);
    var connectionString = GetConnectionString();
    var store = BuildStore(connectionString);
    var pipeline = BuildPipeline(store);

    try
    {
        if (foundational)
            await store.UpsertProjectAsync(projectName, localPath: path, isFoundational: true);

        await pipeline.IndexProjectAsync(projectName, path);
        Console.WriteLine($"Indexed {projectName}: check database for results.");
        return 0;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Index failed for {Project}", projectName);
        return 1;
    }
}

async Task<int> RunIndexAll(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: index-all <base-path>");
        return 1;
    }

    var basePath = Path.GetFullPath(args[1]);
    if (!Directory.Exists(basePath))
    {
        Console.Error.WriteLine($"Directory not found: {basePath}");
        return 1;
    }

    var connectionString = GetConnectionString();
    var store = BuildStore(connectionString);
    var pipeline = BuildPipeline(store);

    var foundationalRepos = GetFoundationalRepos();

    try
    {
        // Step 1: Index foundational repos first
        foreach (var foundational in foundationalRepos)
        {
            var path = Path.Combine(basePath, foundational);
            if (!Directory.Exists(path))
            {
                Console.WriteLine($"SKIP (not found): {foundational}");
                continue;
            }
            Console.WriteLine($"Indexing foundational: {foundational}");
            await store.UpsertProjectAsync(foundational, localPath: path, isFoundational: true);
            await pipeline.IndexProjectAsync(foundational, path);
        }

        // Step 2: Build foundational knowledge from indexed framework repos
        var knowledge = await BuildFoundationalKnowledge(store);

        // Step 3: Index remaining repos
        var repoDirs = Directory.GetDirectories(basePath)
            .Where(d => !foundationalRepos.Contains(Path.GetFileName(d)))
            .OrderBy(d => d);
        foreach (var repoDir in repoDirs)
        {
            var projectName = Path.GetFileName(repoDir);
            Console.WriteLine($"Indexing: {projectName}");
            await pipeline.IndexProjectAsync(projectName, repoDir, knowledge: knowledge);
        }

        Console.WriteLine("Index-all complete.");
        return 0;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Index-all failed");
        return 1;
    }
}

async Task<int> RunStats()
{
    var connectionString = GetConnectionString();
    var store = BuildStore(connectionString);

    try
    {
        var projects = await store.ListProjectsAsync();
        Console.WriteLine($"Projects: {projects.Count}");
        Console.WriteLine();

        foreach (var project in projects.OrderBy(p => p.Name))
        {
            Console.WriteLine($"  {project.Name}{(project.IsFoundational ? " [foundational]" : "")}");
        }
        Console.WriteLine();

        // Node counts by label
        Console.WriteLine("Nodes by label:");
        foreach (var label in Enum.GetValues<NodeLabel>())
        {
            var nodes = await store.FindAllNodesByLabelAsync(label);
            if (nodes.Count > 0)
                Console.WriteLine($"  {label,-20} {nodes.Count,8:N0}");
        }
        Console.WriteLine();

        // Edge counts by type
        Console.WriteLine("Edges by type:");
        foreach (var edgeType in Enum.GetValues<EdgeType>())
        {
            var edges = await store.FindAllEdgesByTypeAsync(edgeType);
            if (edges.Count > 0)
                Console.WriteLine($"  {edgeType,-20} {edges.Count,8:N0}");
        }

        return 0;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Stats failed");
        return 1;
    }
}

async Task<int> RunAnalyze(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: analyze <path> [--name <name>] [--model <model>] [--write-docs]");
        return 1;
    }

    var path = Path.GetFullPath(args[1]);
    if (!Directory.Exists(path))
    {
        Console.Error.WriteLine($"Directory not found: {path}");
        return 1;
    }

    string? name = null;
    string? model = null;
    var writeDocs = false;

    for (var i = 2; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--name" when i + 1 < args.Length:
                name = args[++i];
                break;
            case "--model" when i + 1 < args.Length:
                model = args[++i];
                break;
            case "--write-docs":
                writeDocs = true;
                break;
        }
    }

    var projectName = name ?? Path.GetFileName(path);
    var connectionString = GetConnectionString();
    var store = BuildStore(connectionString);
    var analyzer = BuildAnalyzer(store);
    var docGenerator = new CodeGraphDocGenerator();

    try
    {
        Console.WriteLine($"Analyzing {projectName} with {model ?? "claude-sonnet-4-6"}...");
        var analysis = await analyzer.AnalyzeRepositoryAsync(projectName, path, model);

        Console.WriteLine($"Confidence: {analysis.Confidence}");
        Console.WriteLine($"Model: {analysis.ModelUsed}");
        Console.WriteLine();
        Console.WriteLine(analysis.Summary);

        if (writeDocs)
        {
            // Write repo-level CODEGRAPH.md
            var allEdges = await store.FindCrossRepoEdgesAsync(projectName);
            var inbound = allEdges.Where(e => e.TargetProject == projectName).ToList();
            var outbound = allEdges.Where(e => e.SourceProject == projectName).ToList();
            var repoDoc = docGenerator.GenerateRepoDoc(projectName, analysis,
                inbound, outbound);
            await File.WriteAllTextAsync(Path.Combine(path, "CODEGRAPH.md"), repoDoc);

            // Write project-level CODEGRAPH.md files
            foreach (var project in analysis.Projects)
            {
                var projectPath = FindProjectDirectory(path, project.ProjectName);
                if (projectPath is not null)
                {
                    var projectDoc = docGenerator.GenerateProjectDoc(project);
                    await File.WriteAllTextAsync(
                        Path.Combine(projectPath, "CODEGRAPH.md"), projectDoc);
                }
            }

            Console.WriteLine();
            Console.WriteLine("CODEGRAPH.md files written.");
        }

        // Store summary in database
        var sourceHash = ComputeRepoHash(path);
        await store.UpsertProjectSummaryAsync(projectName, analysis.Summary,
            analysis.Confidence, sourceHash, analysis.ModelUsed);

        return 0;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Analysis failed for {Project}", projectName);
        return 1;
    }
}

async Task<int> RunMcp()
{
    var connectionString = GetConnectionString();
    var store = BuildStore(connectionString);
    var pipeline = BuildPipeline(store);
    var queryEngine = new GraphQueryEngine(store, loggerFactory.CreateLogger<GraphQueryEngine>());

    var builder = Host.CreateApplicationBuilder();

    // Suppress all console logging so only MCP JSON goes to stdout
    builder.Logging.ClearProviders();

    builder.Services.AddSingleton<IGraphStore>(store);
    builder.Services.AddSingleton(queryEngine);
    builder.Services.AddSingleton(pipeline);

    builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "codegraph",
            Version = "1.0.0"
        };
    })
    .WithStdioServerTransport()
    .WithTools<CodeGraphMcpServer>();

    var host = builder.Build();

    try
    {
        await host.RunAsync();
        return 0;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "MCP server failed");
        return 1;
    }
}

// --- Helpers ---

string GetConnectionString()
    => Environment.GetEnvironmentVariable("CODEGRAPH_MYSQL")
       ?? "Server=localhost;Database=codegraph;User=root;Password=;SslMode=None";

MySqlGraphStore BuildStore(string connectionString)
{
    var storageOptions = Options.Create(new CodeGraphStorageOptions
    {
        ConnectionString = connectionString,
        MigrationsPath = Path.Combine(FindRepoRoot(), "TC.CodeGraphApi", "sql", "migrations")
    });

    var serverVersion = ServerVersion.AutoDetect(connectionString);
    var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
        .UseMySql(connectionString, serverVersion)
        .Options;

    var context = new CodeGraphDbContext(dbOptions);
    return new MySqlGraphStore(context, storageOptions, loggerFactory.CreateLogger<MySqlGraphStore>());
}

IndexingPipeline BuildPipeline(IGraphStore store)
{
    var options = Options.Create(new IndexingOptions());
    // No extractors registered yet — Phase 3 adds the Roslyn extractor
    var extractors = Enumerable.Empty<ICodeExtractor>();
    return new IndexingPipeline(store, extractors, options, loggerFactory.CreateLogger<IndexingPipeline>());
}

ClaudeCodeAnalyzer BuildAnalyzer(IGraphStore store)
{
    // AnthropicClient reads ANTHROPIC_API_KEY from environment by default
    var client = new AnthropicClient();
    var options = Options.Create(new AnalysisOptions());
    return new ClaudeCodeAnalyzer(client, options, store,
        loggerFactory.CreateLogger<ClaudeCodeAnalyzer>());
}

string? FindProjectDirectory(string repoRoot, string projectName)
{
    // Look for a directory containing a .csproj matching the project name
    var csprojFiles = Directory.GetFiles(repoRoot, $"{projectName}.csproj",
        SearchOption.AllDirectories);
    return csprojFiles.Length > 0 ? Path.GetDirectoryName(csprojFiles[0]) : null;
}

string ComputeRepoHash(string rootPath)
{
    // Hash all source files to detect changes since last analysis
    var hash = new XxHash64();
    var files = Directory.GetFiles(rootPath, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"))
        .OrderBy(f => f);

    foreach (var file in files)
    {
        var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(file));
        hash.Append(bytes);
    }

    return Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
}

/// <summary>
/// Build foundational knowledge from already-indexed framework repos.
/// Phase 2 stub — returns empty knowledge. Phase 3 will populate from Roslyn analysis.
/// </summary>
Task<FoundationalKnowledge> BuildFoundationalKnowledge(IGraphStore store)
{
    // TODO: Phase 3 — query the graph for foundational patterns
    return Task.FromResult(new FoundationalKnowledge());
}

/// <summary>
/// Foundational repos that must be indexed first (framework/shared libraries).
/// </summary>
HashSet<string> GetFoundationalRepos()
{
    // TODO: Load from configuration
    return
    [
        "TC.Common.ServiceStack",
        "TC.Common.ServiceBus",
        "TC.Common.Models"
    ];
}

void PrintUsage()
{
    Console.WriteLine("""
        Usage: TC.CodeGraphApi.Console <command> [options]

        Commands:
          migrate [path]                Apply database migrations (default: sql/migrations/)
          index <path> [options]        Index a local repository
            --name <name>                 Project name (defaults to directory name)
            --foundational                Mark as foundational repo
          index-all <base-path>         Index all repos under a directory
          analyze <path> [options]      Run Claude analysis on a repository
            --name <name>                 Project name (defaults to directory name)
            --model <model>               Claude model (default: claude-sonnet-4-6)
            --write-docs                  Write CODEGRAPH.md files to the repository
          mcp                           Start MCP server (stdio transport)
          stats                         Show graph statistics

        Environment Variables:
          CODEGRAPH_MYSQL               MySQL connection string
          ANTHROPIC_API_KEY             Anthropic API key (required for analyze)
        """);
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
