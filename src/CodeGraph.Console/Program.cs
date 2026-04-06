using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Data.Neo4j;
using CodeGraph.Services.Configuration;

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var appSettings = new CodeGraphServiceSettings();
config.GetSection("CodeGraph").Bind(appSettings);

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var logger = loggerFactory.CreateLogger("CodeGraph.Console");

if (args.Length == 0)
{
    return 1;
}

var command = args[0].ToLowerInvariant();

switch (command)
{
    case "migrate":
        return await RunMigrate(args);
    default:
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Available commands: migrate");
        return 1;
}

async Task<int> RunMigrate(string[] args)
{
    var migrationsPath = args.Length > 1
        ? args[1]
        : Path.Combine(FindRepoRoot(), "cypher", "migrations");

    logger.LogInformation("Applying Neo4j migrations from {Path}", migrationsPath);

    var storageOptions = appSettings.StorageOptions;
    storageOptions.Neo4jMigrationsPath = migrationsPath;

    var factory = new Neo4jSessionFactory(storageOptions);
    var store = new Neo4jGraphStore(factory, storageOptions, loggerFactory.CreateLogger<Neo4jGraphStore>());
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

string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
        dir = Directory.GetParent(dir)?.FullName;
    return dir ?? Directory.GetCurrentDirectory();
}
