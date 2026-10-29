using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Models.Responses;
using CodeGraph.Models.Work;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using CodeGraph.Services.Work;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>Overlap of work areas (sc-27872 phase 2), without a database.</summary>
public sealed class WorkOverlapUnitTests
{
    [Theory]
    [InlineData("src/a/Foo.cs", "src/a/Foo.cs", true)]
    [InlineData("src/a/Foo.cs", "./SRC/a/foo.cs", true)]
    [InlineData("src\\a\\Foo.cs", "src/a/Foo.cs", true)]
    [InlineData("src/a", "src/a/Foo.cs", true)]
    [InlineData("src/a/", "src/a/b/Foo.cs", true)]
    [InlineData("src/a/Foo.cs", "src/a", true)]
    [InlineData("src/ab", "src/a/Foo.cs", false)]
    [InlineData("src/a/Foo.cs", "src/a/Bar.cs", false)]
    [InlineData("src/**/*.cs", "src/a/b/Foo.cs", true)]
    [InlineData("src/*.cs", "src/a/Foo.cs", false)]
    [InlineData("src/a/*", "src/a/Foo.cs", true)]
    [InlineData("src/a/**", "src/a/x/*.cs", true)]
    [InlineData("src/a/**", "docs/**", false)]
    [InlineData("Ns.Greeter.Greet", "Ns.Greeter.Greet", true)]
    [InlineData("Ns.Greeter.Greet", "Ns.Greeter.Run", false)]
    [InlineData(".github/ci.yml", ".github/ci.yml", true)]
    [InlineData(".github/ci.yml", "github/ci.yml", false)]
    public void Touches_overlap_when_equal_nested_globbed_or_the_same_symbol(string x, string y, bool expected)
    {
        WorkOverlaps.TouchesOverlap(x, y).ShouldBe(expected);
        WorkOverlaps.TouchesOverlap(y, x).ShouldBe(expected);
    }

    private static WorkArea Area(string session, string item, string? repo, params string[] touches) =>
        new(session, "michael", session.StartsWith('c') ? "claude-code" : "codex", item, repo, touches);

    [Fact]
    public async Task Two_sessions_touching_the_same_file_each_get_a_warning_naming_the_other()
    {
        var areas = new[] { Area("c1", "SW-1", "sceneworks", "src/Greeter.cs"), Area("x1", "SW-2", "sceneworks", "src/Greeter.cs", "docs/") };
        var overlaps = new WorkOverlaps();

        var forClaude = (await overlaps.WarningsForAsync("c1", areas)).ShouldHaveSingleItem();
        var forCodex = (await overlaps.WarningsForAsync("x1", areas)).ShouldHaveSingleItem();

        (forClaude.Kind, forClaude.YourItem, forClaude.OtherItem, forClaude.OtherSessionId, forClaude.OtherHarness).ShouldBe(("direct", "SW-1", "SW-2", "x1", "codex"));
        forClaude.Message.ShouldContain("session x1");
        (forCodex.YourItem, forCodex.OtherItem, forCodex.OtherSessionId).ShouldBe(("SW-2", "SW-1", "c1"));
        forCodex.Message.ShouldContain("session c1");
    }

    [Fact]
    public async Task Areas_in_different_repositories_or_without_overlap_or_of_one_session_give_no_warning()
    {
        var overlaps = new WorkOverlaps();
        (await overlaps.WarningsForAsync("c1", [Area("c1", "SW-1", "sceneworks", "src/a.cs"), Area("x1", "SW-2", "inference", "src/a.cs")])).ShouldBeEmpty();
        (await overlaps.WarningsForAsync("c1", [Area("c1", "SW-1", "sceneworks", "src/a.cs"), Area("x1", "SW-2", "sceneworks", "src/b.cs")])).ShouldBeEmpty();
        (await overlaps.WarningsForAsync("c1", [Area("c1", "SW-1", "sceneworks", "src/a.cs"), Area("c1", "SW-2", "sceneworks", "src/a.cs")])).ShouldBeEmpty();
        (await overlaps.WarningsForAsync("nobody", [Area("c1", "SW-1", "sceneworks", "src/a.cs"), Area("x1", "SW-2", "sceneworks", "src/a.cs")])).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_touch_inside_the_other_items_impact_radius_warns_and_one_lookup_serves_every_pair()
    {
        var impact = new FakeImpact(("sceneworks", "src/Core.cs"), [("sceneworks", "src/Caller.cs", "App.Caller.Run")]);
        var areas = new[]
        {
            Area("c1", "SW-1", "sceneworks", "src/Core.cs"),
            Area("x1", "SW-2", "sceneworks", "src/Caller.cs"),
            Area("x2", "SW-3", "sceneworks", "src/Elsewhere.cs")
        };

        var warnings = await new WorkOverlaps(impact).WarningsForAsync("c1", areas);

        var warning = warnings.ShouldHaveSingleItem();
        (warning.Kind, warning.OtherSessionId, warning.OtherItem).ShouldBe(("impact", "x1", "SW-2"));
        warning.Via.ShouldContain("src/Caller.cs");
        impact.Lookups.Count(lookup => lookup == "src/Core.cs").ShouldBe(1);
        // The other side sees it too, from the same blast radius.
        (await new WorkOverlaps(impact).WarningsForAsync("x1", areas)).ShouldHaveSingleItem().OtherSessionId.ShouldBe("c1");
    }

    [Fact]
    public async Task A_symbol_touch_is_resolved_as_a_symbol_and_a_failing_graph_leaves_only_direct_overlaps()
    {
        var symbolImpact = new FakeImpact(("sceneworks", "App.Greeter.Greet"), [("sceneworks", "src/Main.cs", "App.Main.Run")]);
        var areas = new[] { Area("c1", "SW-1", "sceneworks", "App.Greeter.Greet"), Area("x1", "SW-2", "sceneworks", "src/Main.cs") };
        (await new WorkOverlaps(symbolImpact).WarningsForAsync("c1", areas)).ShouldHaveSingleItem().Kind.ShouldBe("impact");

        var broken = new FakeImpact(throws: true);
        (await new WorkOverlaps(broken).WarningsForAsync("c1", areas)).ShouldBeEmpty();
        var withDirect = new[] { Area("c1", "SW-1", "sceneworks", "src/a.cs", "src/b.cs"), Area("x1", "SW-2", "sceneworks", "src/b.cs") };
        (await new WorkOverlaps(broken).WarningsForAsync("c1", withDirect)).ShouldHaveSingleItem().Kind.ShouldBe("direct");
    }

    private sealed class FakeImpact : IImpactAnalysisService
    {
        private readonly (string Repository, string Touch) from;
        private readonly (string Repository, string File, string Symbol)[] reaches;
        private readonly bool throws;

        public FakeImpact((string Repository, string Touch) from, (string Repository, string File, string Symbol)[] reaches)
        {
            this.from = from;
            this.reaches = reaches;
        }

        public FakeImpact(bool throws)
        {
            this.throws = throws;
            from = ("", "");
            reaches = [];
        }

        public List<string> Lookups { get; } = [];

        private ImpactReport? Report(string repository, string touch)
        {
            Lookups.Add(touch);
            if (throws)
                throw new InvalidOperationException("graph down");
            if (!string.Equals(repository, from.Repository, StringComparison.OrdinalIgnoreCase) || touch != from.Touch)
                return null;
            var nodes = reaches.Select((reach, index) => new AffectedNode(index + 1, reach.Symbol, reach.Symbol, "Method", reach.Repository, null, reach.File, 1, "CALLS", RiskLevel.Low, [])).ToList();
            return new ImpactReport([], nodes, [], new ImpactSummary(nodes.Count, 0, 0, 0, 0, nodes.Count, [repository]));
        }

        // A file lookup answers only for the touch registered as a file; a symbol only for a symbol.
        public Task<ImpactReport?> AnalyzeFileImpactAsync(string project, string filePath, int maxDepth = 3, IGraphReader? graph = null) =>
            Task.FromResult(from.Touch.Contains('/') ? Report(project, filePath) : NoteMiss(filePath));

        public Task<ImpactReport?> AnalyzeImpactAsync(string qualifiedName, string? project = null, int maxDepth = 3, IGraphReader? graph = null) =>
            Task.FromResult(Report(project ?? "", qualifiedName));

        private ImpactReport? NoteMiss(string touch)
        {
            Lookups.Add(touch);
            if (throws)
                throw new InvalidOperationException("graph down");
            return null;
        }
    }
}

/// <summary>
/// Overlap warnings on an indexed fixture repository (sc-27872 phase 2): the graph is built by the
/// real indexing pipeline over a small Swift project, and the work tools run over MariaDB with the
/// real impact analysis. Two sessions claim items touching different files joined by a call path
/// and each is warned of the other; an item touching an unrelated file gives no warning.
/// </summary>
public sealed class WorkOverlapMariaDbTests(WorkDatabase db) : IClassFixture<WorkDatabase>, IAsyncLifetime
{
    private const string Repository = "sceneworks";
    private const string GreeterPath = "Sources/App/Greeter.swift";
    private const string MainPath = "Sources/App/Main.swift";
    private const string LonelyPath = "Sources/App/Lonely.swift";

    private string root = null!;
    private WorkService service = null!;

    public async Task InitializeAsync()
    {
        root = Path.Combine(Path.GetTempPath(), $"codegraph-work-overlap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Sources", "App"));
        File.WriteAllText(Path.Combine(root, GreeterPath), "struct Greeter {\n    func greet(name: String) -> String {\n        return name\n    }\n}\n");
        File.WriteAllText(Path.Combine(root, MainPath), "func run() {\n    let greeter = Greeter()\n    _ = greeter.greet(name: \"a\")\n}\n");
        File.WriteAllText(Path.Combine(root, LonelyPath), "func lonely() -> Int {\n    return 1\n}\n");

        var storageOptions = Options.Create(new MariaDbStorageOptions
        {
            ConnectionString = db.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations")
        });
        var runner = new MariaDbMigrationRunner(storageOptions, NullLogger<MariaDbMigrationRunner>.Instance);
        var dbOptions = new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(db.ConnectionString, ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
            .Options;
        var context = new CodeGraphDbContext(dbOptions);
        var store = new MySqlGraphStore(
            context, storageOptions, NullLogger<MySqlGraphStore>.Instance,
            new MySqlAnalysisStore(context), new MySqlMetricsStore(context), new MySqlReviewStore(context), runner);
        await new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance)
            .IndexProjectAsync(Repository, root, ct: CancellationToken.None);
        var sha = new string('c', 40);
        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = Repository, LocalPath = root, LastCommitSha = sha });
        await store.UpsertSyncStateAsync(new SyncStateEntity { Project = Repository, LastCommitSha = sha });

        var impact = new ImpactAnalysisService(store, NullLogger<ImpactAnalysisService>.Instance);
        service = new WorkService(db.Store, db.Clock, Options.Create(new WorkSessionOptions { IdleSeconds = WorkDatabase.IdleSeconds }), impact);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        return Task.CompletedTask;
    }

    private async Task<JsonElement> CallAsync(string tool, object arguments, string user = "michael")
    {
        var result = await service.ExecuteAsync(tool, JsonSerializer.SerializeToElement(arguments, WorkJson.Options), new WorkActor(user));
        return JsonSerializer.SerializeToElement(result, result.GetType(), WorkJson.Options);
    }

    private async Task<string> ProjectAsync(string prefix, string repository = Repository)
    {
        var key = (prefix + Guid.NewGuid().ToString("N")[..4]).ToUpperInvariant();
        await CallAsync(WorkTools.CreateWork, new { new_project = new { key, name = key, repositories = new[] { new { repository, role = "owned" } } } });
        return key;
    }

    private async Task<string> ItemAsync(string project, string title, string[] touches, string repository = Repository) =>
        (await CallAsync(WorkTools.CreateWork, new { project, item = new { title, status = "ready", repository, touches } }))
            .GetProperty("item").GetProperty("key").GetString()!;

    private async Task<string> SessionAsync(string project, string harness) =>
        (await CallAsync(WorkTools.StartSession, new { project, harness })).GetProperty("session").GetProperty("id").GetString()!;

    private Task<JsonElement> ClaimAsync(string item, string session)
    {
        return ClaimCore();

        async Task<JsonElement> ClaimCore()
        {
            var version = (await CallAsync(WorkTools.GetWork, new { id = item })).GetProperty("version").GetInt64();
            return await CallAsync(WorkTools.UpdateWork, new { id = item, expected_version = version, session_id = session, claim = new { } });
        }
    }

    [Fact]
    public async Task Two_sessions_claiming_files_joined_by_a_call_path_each_get_a_warning_naming_the_other_and_an_unrelated_pair_gets_none()
    {
        var project = await ProjectAsync("OV");
        var greeter = await ItemAsync(project, "Change the greeter", [GreeterPath]);
        var main = await ItemAsync(project, "Change the entry point", [MainPath]);
        var lonely = await ItemAsync(project, "Change something else", [LonelyPath]);
        var claude = await SessionAsync(project, "claude-code");
        var codex = await SessionAsync(project, "codex");
        var third = await SessionAsync(project, "gemini");

        // The first claim has nobody to overlap with.
        (await ClaimAsync(greeter, claude)).TryGetProperty("overlap_warnings", out _).ShouldBeFalse();

        // The second claim's own response warns the claimer ...
        var claimed = await ClaimAsync(main, codex);
        var warning = claimed.GetProperty("overlap_warnings").EnumerateArray().Single();
        (warning.GetProperty("kind").GetString(), warning.GetProperty("your_item").GetString(), warning.GetProperty("other_item").GetString(),
            warning.GetProperty("other_session_id").GetString()).ShouldBe(("impact", main, greeter, claude));
        warning.GetProperty("message").GetString()!.ShouldContain(claude);

        // ... and the first session hears of it on its next response and in its briefing, naming the second.
        var next = await CallAsync(WorkTools.GetWork, new { id = greeter, session_id = claude });
        var forClaude = next.GetProperty("overlap_warnings").EnumerateArray().Single();
        (forClaude.GetProperty("other_session_id").GetString(), forClaude.GetProperty("other_item").GetString()).ShouldBe((codex, main));
        var briefing = await CallAsync(WorkTools.GetBriefing, new { session_id = claude });
        briefing.GetProperty("overlap_warnings").EnumerateArray().Single().GetProperty("other_session_id").GetString().ShouldBe(codex);
        (await CallAsync(WorkTools.GetBriefing, new { session_id = codex })).GetProperty("overlap_warnings").EnumerateArray().Single()
            .GetProperty("other_session_id").GetString().ShouldBe(claude);

        // An unrelated file warns nobody.
        var unrelated = await ClaimAsync(lonely, third);
        unrelated.TryGetProperty("overlap_warnings", out _).ShouldBeFalse();
        (await CallAsync(WorkTools.GetBriefing, new { session_id = third })).TryGetProperty("overlap_warnings", out _).ShouldBeFalse();
        (await CallAsync(WorkTools.GetWork, new { id = greeter, session_id = claude })).GetProperty("overlap_warnings").GetArrayLength().ShouldBe(1);

        // Letting go ends the warning; a session gone silent is not live and warns nobody.
        await CallAsync(WorkTools.UpdateWork, new
        {
            id = main,
            expected_version = (await CallAsync(WorkTools.GetWork, new { id = main })).GetProperty("version").GetInt64(),
            session_id = codex,
            release_claim = new { }
        });
        (await CallAsync(WorkTools.GetWork, new { id = greeter, session_id = claude })).TryGetProperty("overlap_warnings", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Items_touching_the_same_file_warn_directly_and_a_silent_session_does_not_warn()
    {
        var project = await ProjectAsync("OD", "inference");
        var first = await ItemAsync(project, "First", [GreeterPath], "inference");
        var second = await ItemAsync(project, "Second", ["Sources/App"], "inference");
        var claude = await SessionAsync(project, "claude-code");
        var codex = await SessionAsync(project, "codex");
        await ClaimAsync(first, claude);

        var warning = (await ClaimAsync(second, codex)).GetProperty("overlap_warnings").EnumerateArray().Single();
        (warning.GetProperty("kind").GetString(), warning.GetProperty("other_session_id").GetString()).ShouldBe(("direct", claude));

        // Claude goes silent past the idle threshold: its claim lapses with it, and codex hears no more of it.
        db.Clock.Advance(TimeSpan.FromSeconds(WorkDatabase.IdleSeconds + 5));
        (await CallAsync(WorkTools.GetWork, new { id = second, session_id = codex })).TryGetProperty("overlap_warnings", out _).ShouldBeFalse();
    }
}
