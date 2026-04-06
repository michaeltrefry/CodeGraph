using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Models;
using CodeGraph.Services.Extensions;

namespace CodeGraph.Services.Analyzers;

public partial class BatchAnalysisService
{
    /// <summary>
    /// After all per-project batch results are stored, synthesize a repo-level summary
    /// by combining all project summaries with a small Claude API call.
    /// </summary>
    public async Task SynthesizeRepoSummaryAsync(string repoName, string batchId, CancellationToken ct)
    {
        var projectAnalyses = await store.GetProjectAnalysesAsync(repoName);
        if (projectAnalyses.Count == 0)
        {
            logger.LogWarning("No project analyses found for {Repo} — skipping synthesis", repoName);
            return;
        }

        logger.LogInformation("Synthesizing repo summary for {Repo} from {Count} project analyses",
            repoName, projectAnalyses.Count);

        var sb = new StringBuilder();
        sb.AppendLine($"You are synthesizing a repository-level summary for '{repoName}' from individual project analyses.");
        sb.AppendLine("Each project below has already been analyzed. Combine them into a cohesive 2-4 sentence repository summary.");
        sb.AppendLine();
        foreach (var pa in projectAnalyses)
        {
            sb.AppendLine($"### {pa.ProjectName} (confidence: {pa.Confidence})");
            sb.AppendLine(pa.Summary);
            sb.AppendLine();
        }

        sb.AppendLine("""
            Respond with JSON only (no markdown fences):
            {
              "repoSummary": "2-4 sentence description of the repository's business purpose and architecture",
              "confidence": "high|medium|low"
            }
            """);

        var synthesisRequest = new
        {
            model = options.Model,
            max_tokens = options.MaxTokensPerSynthesis,
            messages = new[] { new { role = "user", content = sb.ToString() } }
        };

        var http = httpClientFactory.CreateClient();
        using var response = await circuitBreaker.ExecuteAsync(http,
            () => CreateRequest(HttpMethod.Post, options.MessagesApiUrl, synthesisRequest), ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Synthesis API call failed for {Repo}: {Status} {Body}",
                repoName, (int)response.StatusCode, errorBody);
            return;
        }

        var msgResponse = await response.Content.ReadFromJsonAsync<BatchResultMessage>(SnakeOpts, ct);
        var text = msgResponse?.Content
            .Where(c => c.Type == "text")
            .Select(c => c.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

        if (string.IsNullOrWhiteSpace(text))
        {
            logger.LogWarning("Synthesis returned empty response for {Repo}", repoName);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(text.StripCodeFences());
            var summary = doc.RootElement.GetProperty("repoSummary").GetString();
            var confStr = doc.RootElement.GetProperty("confidence").GetString() ?? "medium";
            var confidence = confStr.TryParseEnum<ConfidenceLevel>() ?? ConfidenceLevel.Medium;

            if (!string.IsNullOrWhiteSpace(summary))
            {
                await store.UpsertRepositorySummaryAsync(repoName, summary, confidence,
                    sourceHash: batchId, modelUsed: msgResponse?.Model);
                logger.LogInformation("Synthesized and stored repo summary for {Repo} (confidence: {Confidence})",
                    repoName, confidence);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse synthesis response for {Repo}", repoName);
        }
    }

    /// <summary>
    /// Write CODEGRAPH.md files to the repo after analysis is complete.
    /// Generates both per-project and repo-level docs from stored analysis data.
    /// </summary>
    public async Task WriteCodeGraphDocsAsync(string repoName, CancellationToken ct)
    {
        var repos = await store.ListRepositoriesAsync();
        var repoInfo = repos.FirstOrDefault(r =>
            string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase));

        if (repoInfo?.LocalPath is null || !fileSystem.DirectoryExists(repoInfo.LocalPath))
        {
            logger.LogWarning("Cannot write CODEGRAPH.md for {Repo}: no local path or directory not found", repoName);
            return;
        }

        var docGenerator = new CodeGraphDocGenerator();
        var projectAnalyses = await store.GetProjectAnalysesAsync(repoName);

        // Per-project CODEGRAPH.md files
        foreach (var pa in projectAnalyses)
        {
            var projectDir = FindProjectDirectory(repoInfo.LocalPath, pa.ProjectName);
            if (projectDir is null) continue;

            var projectAnalysis = new ProjectAnalysis(
                pa.ProjectName, pa.Summary, pa.Confidence,
                pa.Endpoints, pa.Services,
                pa.ExternalDependencies, pa.DatabaseTables);

            var projectDoc = docGenerator.GenerateProjectDoc(projectAnalysis);
            await File.WriteAllTextAsync(
                Path.Combine(projectDir, "CODEGRAPH.md"), projectDoc, ct);
            logger.LogDebug("Wrote {Project}/CODEGRAPH.md", pa.ProjectName);
        }

        // Repo-level CODEGRAPH.md
        var repoSummary = await store.GetRepositorySummaryAsync(repoName);
        if (repoSummary is not null)
        {
            var projects = projectAnalyses.Select(pa =>
                new ProjectAnalysis(pa.ProjectName, pa.Summary, pa.Confidence,
                    pa.Endpoints, pa.Services,
                    pa.ExternalDependencies, pa.DatabaseTables)).ToList();

            var repoAnalysis = new RepoAnalysis(
                repoSummary.Summary, repoSummary.Confidence,
                repoSummary.ModelUsed ?? "unknown", projects);

            var crossRepoEdges = await store.FindCrossRepoEdgesAsync(repoName);
            var inbound = crossRepoEdges.Where(e => e.TargetProject == repoName).ToList();
            var outbound = crossRepoEdges.Where(e => e.SourceProject == repoName).ToList();

            var repoDoc = docGenerator.GenerateRepoDoc(repoName, repoAnalysis, inbound, outbound);
            await File.WriteAllTextAsync(
                Path.Combine(repoInfo.LocalPath, "CODEGRAPH.md"), repoDoc, ct);
            logger.LogInformation("Wrote {Repo}/CODEGRAPH.md", repoName);
        }
    }

    /// <summary>
    /// Find a project subdirectory by name within a repo root path.
    /// </summary>
    private string? FindProjectDirectory(string repoRoot, string projectName)
    {
        // Try direct match first
        var direct = Path.Combine(repoRoot, projectName);
        if (fileSystem.DirectoryExists(direct)) return direct;

        // Try src/ subdirectory
        var src = Path.Combine(repoRoot, "src", projectName);
        if (fileSystem.DirectoryExists(src)) return src;

        // Search for matching .csproj
        var csprojFiles = fileSystem.EnumerateFiles(repoRoot, $"{projectName}.csproj", SearchOption.AllDirectories).ToArray();
        if (csprojFiles.Length > 0)
            return Path.GetDirectoryName(csprojFiles[0]);

        return null;
    }
}
