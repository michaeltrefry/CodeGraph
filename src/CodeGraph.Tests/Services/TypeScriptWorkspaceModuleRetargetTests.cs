using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Data;
using CodeGraph.Tests.Extractors;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// sc-26145 (review of #107): a workspace package Module's qualified name flips between
/// <c>name</c> and <c>name#dir</c> when a second package of that name appears or disappears. The
/// importers whose files did not change must re-target their IMPORTS edges in the same run. They
/// do, in both stores: the rewritten module slice carries every buffered edge into the module (the
/// analysis covers all files, not only changed ones), and a stored incoming edge to the retired
/// name cannot resolve once that node is replaced, so it is dropped rather than left dangling.
/// </summary>
public sealed class TypeScriptWorkspaceModuleRetargetTests : IDisposable
{
    private const string Project = "Lens";
    private const string Importer = $"{Project}:web/src/app.ts";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-ts-retarget-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private Task IndexAsync(IGraphStore store) =>
        new IndexingPipeline(
                store,
                [new NoOpTypeScriptExtractor()],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance,
                typeScriptAnalyzer: new SidecarLikeAnalyzer(_root))
            .IndexProjectAsync(Project, _root, ct: CancellationToken.None);

    private static async Task<List<string>> ImportTargetsAsync(IGraphStore store) =>
        (await store.FindEdgesByPropertyAsync(
            Project, [EdgeType.IMPORTS], "classification", ["local_workspace_package"], CancellationToken.None))
        .Where(edge => edge.SourceQN == Importer)
        .Select(edge => edge.TargetQN)
        .ToList();

    private static async Task<List<string>> ModulesAsync(IGraphStore store) =>
        (await store.FindNodesByLabelAsync(Project, NodeLabel.Module, int.MaxValue))
        .Select(node => $"{node.QualifiedName}@{node.FilePath}")
        .Order(StringComparer.Ordinal)
        .ToList();

    private async Task AddThenRemoveSameNamedPackageAsync(Func<IGraphStore> openStore)
    {
        Write("tsconfig.json", "{}");
        Write("packages/desk/package.json", """{ "name": "desk" }""");
        Write("packages/desk/src/index.ts", "export class Index {}\n");
        Write("web/src/app.ts", "import { Index } from 'desk';\n");
        await IndexAsync(openStore());
        (await ImportTargetsAsync(openStore())).ShouldBe([$"typescript-package:{Project}:desk"]);

        // A vendored copy with the same name appears; the importer's file is unchanged.
        Write("vendor/desk/package.json", """{ "name": "desk" }""");
        await IndexAsync(openStore());
        (await ModulesAsync(openStore())).ShouldBe([
            $"typescript-package:{Project}:desk#packages/desk@packages/desk",
            $"typescript-package:{Project}:desk#vendor/desk@vendor/desk"
        ]);
        (await ImportTargetsAsync(openStore())).ShouldBe([$"typescript-package:{Project}:desk#packages/desk"]);

        // The copy goes away again: the module and the import return to the name-only form.
        Directory.Delete(Path.Combine(_root, "vendor"), recursive: true);
        await IndexAsync(openStore());
        (await ModulesAsync(openStore())).ShouldBe([$"typescript-package:{Project}:desk@packages/desk"]);
        (await ImportTargetsAsync(openStore())).ShouldBe([$"typescript-package:{Project}:desk"]);
    }

    [Fact]
    public Task IndexProject_SameNamedPackageAddedThenRemoved_UnchangedImporterFollowsTheModule()
    {
        var store = new InMemoryGraphStore();
        return AddThenRemoveSameNamedPackageAsync(() => store);
    }

    [Fact]
    public async Task IndexProject_SameNamedPackageAddedThenRemoved_UnchangedImporterFollowsTheModule_MariaDb()
    {
        var connectionString = MariaDbTestEnvironment.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = $"codegraph_ts_retarget_{Guid.NewGuid():N}"
        };
        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        var runner = new MariaDbMigrationRunner(storageOptions, NullLogger<MariaDbMigrationRunner>.Instance);
        var contexts = new List<CodeGraphDbContext>();
        try
        {
            await runner.ApplyConfiguredMigrationsAsync();
            await AddThenRemoveSameNamedPackageAsync(() =>
            {
                var context = new CodeGraphDbContext(new DbContextOptionsBuilder<CodeGraphDbContext>()
                    .UseMySql(builder.ConnectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
                    .Options);
                contexts.Add(context);
                return new MySqlGraphStore(
                    context, storageOptions, NullLogger<MySqlGraphStore>.Instance,
                    new MySqlAnalysisStore(context), new MySqlMetricsStore(context),
                    new MySqlReviewStore(context), runner);
            });
        }
        finally
        {
            foreach (var context in contexts)
                await context.DisposeAsync();
            await using var conn = new MySqlConnection(
                new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ConnectionString);
            await conn.OpenAsync();
            // Release this database's pooled connections so the suite stays under max_connections.
            await MySqlConnection.ClearAllPoolsAsync();
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{builder.Database}`");
        }
    }

    private sealed class NoOpTypeScriptExtractor : ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".ts" };

        public Task<ExtractionResult> ExtractAsync(string filePath, string content,
            ExtractorContext context, CancellationToken ct = default) =>
            Task.FromResult(new ExtractionResult());
    }

    /// <summary>
    /// The sidecar's workspace-package output: a Module per package.json (directory-qualified when
    /// its name collides) and an IMPORTS edge from each .ts file importing a package by name.
    /// </summary>
    private sealed class SidecarLikeAnalyzer(string rootPath) : ITypeScriptAnalyzer
    {
        public string ExtractionVersion => "3";

        public Task<IReadOnlyList<ExtractionResult>> AnalyzeProjectAsync(
            string tsconfigPath, ExtractorContext context, IReadOnlyList<string> includedFiles,
            CancellationToken ct = default)
        {
            var packages = includedFiles.Where(file => Path.GetFileName(file) == "package.json")
                .Select(file => (Name: JsonDocument.Parse(File.ReadAllText(Path.Combine(rootPath, file)))
                    .RootElement.GetProperty("name").GetString()!, Dir: Path.GetDirectoryName(file)!.Replace('\\', '/')))
                .ToList();
            string QualifiedName((string Name, string Dir) package) =>
                packages.Count(other => other.Name == package.Name) > 1
                    ? $"typescript-package:{context.ProjectName}:{package.Name}#{package.Dir}"
                    : $"typescript-package:{context.ProjectName}:{package.Name}";

            var nodes = packages.Select(package => new GraphNode
            {
                Project = context.ProjectName,
                Label = NodeLabel.Module,
                Name = package.Name,
                QualifiedName = QualifiedName(package),
                FilePath = package.Dir,
                Properties = new() { ["module_kind"] = "typescript_workspace_package" }
            }).ToList();
            var edges = nodes.Select(node => new PendingEdge(context.ProjectName, node.QualifiedName, EdgeType.CONTAINS_PROJECT))
                .ToList();
            var sources = includedFiles.Where(file => file.EndsWith(".ts", StringComparison.Ordinal)).ToList();
            foreach (var source in sources)
            {
                var text = File.ReadAllText(Path.Combine(rootPath, source));
                foreach (var name in packages.Select(package => package.Name).Distinct()
                             .Where(name => text.Contains($"'{name}'", StringComparison.Ordinal)))
                {
                    // The nearest same-named package: the first by directory here.
                    var target = packages.Where(other => other.Name == name).OrderBy(other => other.Dir).First();
                    edges.Add(new PendingEdge($"{context.ProjectName}:{source}", QualifiedName(target), EdgeType.IMPORTS,
                        new() { ["classification"] = "local_workspace_package" }));
                }
            }

            return Task.FromResult<IReadOnlyList<ExtractionResult>>([
                new ExtractionResult
                {
                    Nodes = nodes,
                    Edges = edges,
                    ProcessedFiles = sources,
                    Metadata = new ProjectMetadata("TypeScript", "Node.js")
                }
            ]);
        }
    }
}
