using System.Text.Json;
using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Dapper;
using MySqlConnector;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>
/// The native-memory candidate report for a memory directory (dry run only, nothing stored), run on
/// demand: skipped unless CODEGRAPH_MEMORY_DIR is set. The report is written to CODEGRAPH_IMPORT_REPORT_DIR.
/// </summary>
public sealed class NativeMemoryStagingRunTests(WorkDatabase db) : IClassFixture<WorkDatabase>
{
    private static string? ReportDirectory => Environment.GetEnvironmentVariable("CODEGRAPH_IMPORT_REPORT_DIR");

    private static void Write(string name, object value)
    {
        if (ReportDirectory is not { } directory)
            return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, value.GetType(), new JsonSerializerOptions(WorkJson.Options) { WriteIndented = true }));
    }

    [Fact]
    public async Task Native_memory_candidate_report_dry_run()
    {
        if (Environment.GetEnvironmentVariable("CODEGRAPH_MEMORY_DIR") is not { } memoryDirectory)
            return;

        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        // Read only: the files are read and never written.
        var files = Directory.EnumerateFiles(memoryDirectory, "*.md").Select(path => new MemoryFile(path, File.ReadAllText(path))).ToList();
        var candidates = NativeMemoryParser.ParseClaude(files, new RepositoryCheckout(repositoryRoot)).ToList();
        if (Environment.GetEnvironmentVariable("CODEGRAPH_CODEX_MEMORY") is { } codex && File.Exists(codex))
            candidates.AddRange(NativeMemoryParser.ParseCodex(new MemoryFile(codex, File.ReadAllText(codex)), new RepositoryCheckout(repositoryRoot), "CodeGraph"));

        var key = await db.NewProjectAsync("MEMDEMO", "inference");
        var before = await CountAsync("SELECT COUNT(*) FROM knowledge_entries");
        var outcomes = new List<JsonElement>();
        foreach (var batch in candidates.Chunk(KnowledgeLimits.MaxImportCandidates))
        {
            var preview = await db.CallAsync(WorkTools.ImportKnowledge, new { project = key, candidates = batch.Select(candidate => candidate.Candidate).ToList() });
            preview.GetProperty("stored").GetBoolean().ShouldBeFalse();
            outcomes.AddRange(preview.GetProperty("candidates").EnumerateArray());
        }

        (await CountAsync("SELECT COUNT(*) FROM knowledge_entries")).ShouldBe(before);
        Write("memory-candidates.json", new
        {
            memory_directory = memoryDirectory,
            repository_root = repositoryRoot,
            counts = candidates.GroupBy(candidate => $"{candidate.Harness}:{JsonNamingPolicy.SnakeCaseLower.ConvertName(candidate.Candidate.Verification.ToString())}")
                .ToDictionary(group => group.Key, group => group.Count()),
            candidates = candidates.Select((candidate, index) => new
            {
                index,
                candidate.Harness,
                memory_type = candidate.MemoryType,
                source_file = candidate.Candidate.SourceFile,
                kind = candidate.Candidate.Entry.Kind,
                summary = candidate.Candidate.Entry.Summary,
                pre_check = candidate.Candidate.Verification,
                note = candidate.Candidate.VerificationNote,
                candidate.Referenced,
                candidate.Missing,
                s5_dry_run = index < outcomes.Count ? outcomes[index] : default
            }).ToList()
        });
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new MySqlConnection(db.ConnectionString);
        return await connection.ExecuteScalarAsync<long>(sql);
    }
}
