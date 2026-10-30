using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Models.Work;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Metrics;
using CodeGraph.Services.Query;
using CodeGraph.Services.Telemetry;
using CodeGraph.Services.Usage;
using CodeGraph.Services.Work;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// Explicitly dispatched S8 acceptance, never a normal-CI inference test. It uses the actual
/// GraphAssistant provider loop, actual source checkout and persistent native MariaDB project.
/// It does not fabricate historical sessions, corrections, graph nodes or review evidence.
/// </summary>
public sealed class DreamingLiveAcceptance
{
    [LiveDreamFact]
    public async Task CodeGraph_live_provider_report_and_revert()
    {
        var connectionString = Required("CODEGRAPH_DREAMING_LIVE_CONNECTION");
        var builder = new MySqlConnectionStringBuilder(connectionString);
        // The acceptance command may mutate only its explicit, persistent local database.
        new[] { "localhost", "127.0.0.1", "::1" }.ShouldContain(builder.Server);
        builder.Database.ShouldStartWith("cg_dreaming_live");
        var root = Path.GetFullPath(Required("CODEGRAPH_DREAMING_LIVE_SOURCE_ROOT"));
        File.Exists(Path.Combine(root, "tools/cg/CodeGraph.Cg.csproj")).ShouldBeTrue();
        File.Exists(Path.Combine(root, "src/CodeGraph.Data/CodeGraphStorageOptions.cs")).ShouldBeTrue();
        var artifactRoot = Path.GetFullPath(Required("CODEGRAPH_DREAMING_LIVE_ARTIFACTS"));
        var artifacts = Path.Combine(artifactRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(artifacts);
        var provider = Required("CODEGRAPH_DREAMING_LIVE_PROVIDER").Trim().ToLowerInvariant();
        provider.ShouldBe("lmstudio");
        var model = Required("CODEGRAPH_DREAMING_LIVE_MODEL");
        var endpoint = new Uri(Required("CODEGRAPH_DREAMING_LIVE_BASE_URL"));
        endpoint.IsLoopback.ShouldBeTrue();
        var commit = await GitAsync(root, "rev-parse", "HEAD");
        var sourceStatus = await GitAsync(root, "status", "--porcelain");
        sourceStatus.ShouldBe("", "the live run must read a committed stationary candidate");
        var options = Options.Create(new MariaDbStorageOptions { ConnectionString = connectionString, MigrationsPath = Path.Combine(root, "sql/migrations") });
        var migrationRunner = new MariaDbMigrationRunner(options, NullLogger<MariaDbMigrationRunner>.Instance);
        await migrationRunner.ApplyConfiguredMigrationsAsync();
        var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(connectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb)).Options;
        await using var context = new CodeGraphDbContext(dbOptions);
        var graph = new MySqlGraphStore(context, options, NullLogger<MySqlGraphStore>.Instance,
            new MySqlAnalysisStore(context), new MySqlMetricsStore(context), new MySqlReviewStore(context), migrationRunner);
        await graph.UpsertRepositoryAsync(new RepositoryEntity { Name = "CodeGraph", LocalPath = root, LastCommitSha = commit, DefaultBranch = "main" });
        var work = new MySqlWorkStore(options);
        var service = new WorkService(work);
        var actor = new WorkActor("codex", Harness: "S8 native live acceptance");
        async Task<JsonElement> WorkAsync(string tool, object args)
        {
            var result = await service.ExecuteAsync(tool, JsonSerializer.SerializeToElement(args, WorkJson.Options), actor);
            return JsonSerializer.SerializeToElement(result, result.GetType(), WorkJson.Options);
        }
        var resolved = await work.ResolveAsync("CODEGRAPH");
        if (resolved is null)
            await WorkAsync(WorkTools.CreateWork, new { new_project = new { key = "CODEGRAPH", name = "CodeGraph", description = "Actual CodeGraph project in the local native acceptance environment. Knowledge is imported from the linked committed source checkout; no production history is claimed.", repositories = new[] { new { repository = "CodeGraph", role = "owned" } } } });
        var project = (await work.ResolveAsync("CODEGRAPH"))!.Id;
        var projectView = (await work.GetProjectAsync(project))!;
        projectView.Repositories.ShouldContain(r => r.Repository == "CodeGraph");
        var store = new MySqlDreamingStore(options);
        var instructions = await store.GetInstructionsAsync(project);
        instructions.Base.Content.ShouldContain("Evidence and prevalence");
        if (instructions.Project.Revision == 0)
            await store.SetInstructionsAsync(project, "project", await File.ReadAllTextAsync(Path.Combine(root, "docs/dreaming/codegraph-addendum.md")), 0, actor);
        instructions = await store.GetInstructionsAsync(project);

        // Capture authentic source knowledge, never synthetic work history. This generated claim
        // is copied literally, including its uncertainty; the model must judge it against source.
        const string cgClaim = "CodeGraph.Cg appears to define core graph abstractions.";
        var generated = await File.ReadAllTextAsync(Path.Combine(root, "CODEGRAPH.md"));
        generated.ShouldContain(cgClaim);
        var seedSpecs = new[]
        {
            new Seed("storage-default", "The default configured storage provider is MariaDb.", "high",
                "src/CodeGraph.Data/CodeGraphStorageOptions.cs", "Source-derived fact: CodeGraphStorageOptions.Provider defaults to MariaDb. This describes a source default, not a production host's configuration."),
            new Seed("cg-generated-description", cgClaim, "low", "CODEGRAPH.md",
                "Literal low-confidence claim imported from the repository's generated CODEGRAPH.md, not an observed runtime fact. The current tools/cg/CodeGraph.Cg.csproj is the owning source and may confirm or contradict this description. No historical correction event or user decision is asserted.")
        };
        var provenance = new List<object>();
        foreach (var seed in seedSpecs)
        {
            var sourceText = await File.ReadAllTextAsync(Path.Combine(root, seed.Path));
            var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sourceText))).ToLowerInvariant();
            await graph.UpsertFileHashBatchAsync("CodeGraph", new Dictionary<string, string> { [seed.Path] = hash });
            var recorded = await WorkAsync(WorkTools.Record, new { project = "CODEGRAPH", idempotency_key = "s8-source-" + seed.Key + "-" + commit,
                entry = new { kind = "fact", summary = seed.Summary, confidence = seed.Confidence,
                    body = seed.Body + "\nProvenance: local acceptance import by codex from CodeGraph/" + seed.Path + " at " + commit + "; one source observation, not a historical session or review. Recheck related current source before curation.",
                    repositories = new[] { "CodeGraph" }, paths = new[] { seed.Path }, commit } });
            provenance.Add(new { seed.Key, seed.Path, sha256 = hash, entry_id = recorded.GetProperty("entry").GetProperty("id").GetGuid(), source_commit = commit });
        }
        await WriteAsync("environment.json", new { environment = "local native MariaDB acceptance; not production", database = builder.Database,
            database_server = builder.Server, database_port = builder.Port, source_root = root, source_commit = commit,
            source_status = sourceStatus, provider, model, endpoint = endpoint.ToString(), project, provenance,
            instructions, no_historical_work_seeded = true });

        var metrics = new CapturedMetrics();
        using var httpFactory = new LiveHttpFactory();
        var analysis = new AnalysisOptions { DefaultProvider = provider, LmStudio = new() { BaseUrl = endpoint.ToString(), Model = model } };
        var assistant = new GraphAssistant(null!, httpFactory, new GraphQueryEngine(graph, NullLogger<GraphQueryEngine>.Instance), graph,
            new MySqlWikiStore(context), Options.Create(new RepositorySourceOptions()), Options.Create(analysis), metrics, null!, NullLogger<GraphAssistant>.Instance);
        var actions = (IKnowledgeDreamActions)service;
        var dreaming = new DreamingService(store, work, actions, assistant, new DreamSourceReader(assistant, graph, work));
        var previousSettings = await store.GetSettingsAsync();
        var settings = previousSettings with { Enabled = true, DefaultProvider = provider, DefaultModel = model,
            MaxTurns = 12, MaxTokensPerRun = 160000, MinimumIntervalMinutes = 0, InputCostPerMillion = 0, OutputCostPerMillion = 0 };
        await dreaming.SetSettingsAsync(settings, actor);
        await WriteAsync("settings.json", new { settings, estimated_cost_basis = "local configured provider has no per-token charge; electricity/hardware cost not estimated" });
        await using var snapshotConnection = new MySqlConnection(connectionString);
        var originalIds = (await snapshotConnection.QueryAsync<string>("SELECT id FROM knowledge_entries WHERE project_id=@project", new { project })).ToArray();
        var before = await SnapshotAsync(connectionString, project, originalIds);
        await WriteAsync("before.json", before);
        DreamRun? run = null;
        DreamRevertResult? revert = null;
        string? failure = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            run = await dreaming.RunAsync(project, "run_now", actor, timeout.Token);
            await WriteAsync("run.json", run);
            await WriteAsync("usage.json", metrics.Usage);
            await WriteAsync("after-run.json", await SnapshotAsync(connectionString, project, originalIds));
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            try
            {
                if (run is not null)
                {
                    revert = await actions.RevertAsync(project, run.Id, actor);
                    await WriteAsync("revert.json", revert);
                }
                var after = await SnapshotAsync(connectionString, project, originalIds);
                await WriteAsync("after-revert.json", after);
                await WriteAsync("restoration.json", new { semantically_equal = before == after,
                    comparison = "all preexisting and active knowledge fields, scope, freshness, events, links, challenges and addressed_by; work state; excludes append-only audit, entry revision/version/updated_at and new retracted tombstones",
                    pending_proposals = await ProposalsAsync(connectionString, project), failure });
            }
            finally { await store.SetSettingsAsync(previousSettings, actor); }
        }
        run.ShouldNotBeNull();
        run.Status.ShouldBe("completed", $"See {artifacts}/run.json");
        (run.InputTokens + run.OutputTokens).ShouldBeLessThanOrEqualTo(settings.MaxTokensPerRun);
        run.InputTokens.ShouldBeGreaterThan(0);
        var writes = run.Actions.Where(a => a.Tool is "dream_verify" or "dream_revise" or "dream_relate" or "dream_promote" or "dream_record" or "dream_challenge").ToList();
        writes.ShouldNotBeEmpty("S8 requires an actual useful direct write, not only reads");
        var proposals = run.Actions.Where(a => a.Tool == "dream_propose").ToList();
        proposals.ShouldNotBeEmpty("S8 requires a non-binding evidence-backed proposal");
        foreach (var action in run.Actions)
            action.Result.TryGetProperty("error", out _).ShouldBeFalse("A live acceptance run must not have any refused tool calls");
        foreach (var action in writes.Concat(proposals))
        {
            action.Arguments.GetProperty("evidence").GetArrayLength().ShouldBeGreaterThan(0);
            action.Arguments.GetProperty("prevalence").GetString().ShouldNotBeNullOrWhiteSpace();
        }
        revert.ShouldNotBeNull();
        revert.Entries.ShouldAllBe(e => e.Status == "reverted");
        (await SnapshotAsync(connectionString, project, originalIds)).ShouldBe(before, $"Semantic restoration failed; inspect {artifacts}");
        await WriteAsync("acceptance.json", new { passed = true, run_id = run.Id, direct_writes = writes.Count, proposals = proposals.Count,
            retained = "run report, revisions, write journal, non-binding pending proposals and any retracted new-entry tombstones", artifacts });
        return;

        Task WriteAsync(string file, object value) => File.WriteAllTextAsync(Path.Combine(artifacts, file), value is string json ? json : JsonSerializer.Serialize(value, new JsonSerializerOptions(WorkJson.Options) { WriteIndented = true }));
    }

    private sealed record Seed(string Key, string Summary, string Confidence, string Path, string Body);
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Set {name} explicitly for S8 live acceptance.");
    private static async Task<string> GitAsync(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output.Trim();
    }
    private static async Task<object> ProposalsAsync(string connectionString, Guid project)
    {
        await using var c = new MySqlConnection(connectionString);
        return (await c.QueryAsync("SELECT id,action,target_id,status,evidence_json,prevalence FROM dream_proposals WHERE project_id=@project ORDER BY id", new { project })).ToList();
    }
    private static async Task<string> SnapshotAsync(string connectionString, Guid project, string[] originalIds)
    {
        await using var c = new MySqlConnection(connectionString);
        var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
        // Include all original entries as well as all serving state. A new record's inverse is a
        // retraction, so its tombstone is retained as audit and cannot be mistaken for a live entry.
        var entryRows = (await c.QueryAsync("SELECT * FROM knowledge_entries WHERE project_id=@project AND (status<>'retracted' OR id IN @originalIds)", new { project, originalIds })).ToList();
        var ids = entryRows.Select(r => ((IDictionary<string, object>)r)["id"].ToString()!).ToArray();
        result["knowledge_entries"] = Canonical(entryRows, ["revision", "version", "updated_at"]);
        foreach (var table in new[] { "knowledge_entry_repositories", "knowledge_entry_paths", "knowledge_entry_nodes", "knowledge_entry_files", "lesson_events", "lesson_links", "lesson_challenges", "lesson_addressed_by" })
            result[table] = Canonical((await c.QueryAsync($"SELECT * FROM `{table}` WHERE entry_id IN @ids", new { ids })).ToList(), table == "knowledge_entry_files" ? ["id"] : []);
        // These are operational state, which the dreamer may not change. The change feed itself is audit.
        foreach (var table in new[] { "work_projects", "work_project_repositories", "work_features", "work_items", "work_sessions", "work_messages", "work_holds" })
        {
            var exists = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@table", new { table });
            if (exists > 0) result[table] = Canonical((await c.QueryAsync($"SELECT * FROM `{table}`")).ToList(), []);
        }
        return JsonSerializer.Serialize(result, WorkJson.Options);
    }
    private static string[] Canonical(IEnumerable<dynamic> rows, string[] omitted) => rows.Select(row =>
        JsonSerializer.Serialize(new SortedDictionary<string, object?>(((IDictionary<string, object>)row).Where(pair => !omitted.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => (object?)pair.Value), StringComparer.Ordinal), WorkJson.Options)).Order(StringComparer.Ordinal).ToArray();
    private sealed class LiveHttpFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };
        public HttpClient CreateClient(string name) => client;
        public void Dispose() => client.Dispose();
    }
    private sealed class CapturedMetrics : IMetricsEventPublisher
    {
        public List<LlmUsageRecord> Usage { get; } = [];
        public Task<LlmUsageRecord> PublishLlmUsageAsync(LlmUsageRecord value, CancellationToken ct = default) { Usage.Add(value); return Task.FromResult(value); }
        public Task<IReadOnlyList<LlmUsageRecord>> PublishLlmUsageBatchAsync(IEnumerable<LlmUsageRecord> values, CancellationToken ct = default) { var list = values.ToList(); Usage.AddRange(list); return Task.FromResult<IReadOnlyList<LlmUsageRecord>>(list); }
        public Task<McpToolInvocationRecord> PublishMcpToolInvocationAsync(McpToolInvocationRecord value, CancellationToken ct = default) => Task.FromResult(value);
    }
}

public sealed class LiveDreamFactAttribute : FactAttribute
{
    public LiveDreamFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CODEGRAPH_DREAMING_LIVE") != "1")
            Skip = "Explicit S8 live-provider dispatch only; no model inference in normal CI.";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
            Skip = "Live model inference is excluded from normal CI; use the separate local acceptance command.";
    }
}
