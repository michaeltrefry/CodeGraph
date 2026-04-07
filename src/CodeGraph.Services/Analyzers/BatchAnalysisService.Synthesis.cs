using System.Net.Http.Json;
using System.Diagnostics;
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

        var projects = projectAnalyses.Select(pa =>
            new ProjectAnalysis(pa.ProjectName, pa.Summary, pa.Confidence,
                pa.Endpoints, pa.Services,
                pa.ExternalDependencies, pa.DatabaseTables)).ToList();
        var crossRepoEdges = await store.FindCrossRepoEdgesAsync(repoName);
        var promptText = AnalysisPromptBuilder.BuildRepoSynthesisPrompt(
            repoName, projects, crossRepoEdges, summaryPropertyName: "repoSummary");
        var provider = providerRegistry.GetProvider();
        AnalysisTextResponse response;
        try
        {
            response = await provider.ExecuteAsync(
                new AnalysisPrompt(AnalysisPromptBuilder.SystemPrompt, promptText),
                new AnalysisRequestOptions(MaxTokens: options.MaxTokensPerSynthesis),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Synthesis API call failed for {Repo}", repoName);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(response.Text.NormalizeJsonResponse());
            var summary = doc.RootElement.GetProperty("repoSummary").GetString();
            var confStr = doc.RootElement.GetProperty("confidence").GetString() ?? "medium";
            var confidence = confStr.TryParseEnum<ConfidenceLevel>() ?? ConfidenceLevel.Medium;

            if (!string.IsNullOrWhiteSpace(summary))
            {
                await store.UpsertRepositorySummaryAsync(repoName, summary, confidence,
                    sourceHash: batchId, modelUsed: response.ModelUsed);
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
        var repoInfo = await store.GetRepositoryByName(repoName);

        if (repoInfo?.LocalPath is null || !fileSystem.DirectoryExists(repoInfo.LocalPath))
        {
            logger.LogWarning("Cannot write CODEGRAPH.md for {Repo}: no local path or directory not found", repoName);
            return;
        }

        var docGenerator = new CodeGraphDocGenerator();
        var projectAnalyses = await store.GetProjectAnalysesAsync(repoName);
        var generatedFiles = new List<string>();

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
            var projectDocPath = Path.Combine(projectDir, "CODEGRAPH.md");
            await File.WriteAllTextAsync(projectDocPath, projectDoc, ct);
            generatedFiles.Add(projectDocPath);
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
            var repoDocPath = Path.Combine(repoInfo.LocalPath, "CODEGRAPH.md");
            await File.WriteAllTextAsync(repoDocPath, repoDoc, ct);
            generatedFiles.Add(repoDocPath);
            logger.LogInformation("Wrote {Repo}/CODEGRAPH.md", repoName);
        }

        if (generatedFiles.Count > 0)
            await PublishGeneratedDocsAsync(repoInfo.LocalPath, generatedFiles, ct);
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

    private async Task PublishGeneratedDocsAsync(string repoRoot, IReadOnlyList<string> generatedFiles, CancellationToken ct)
    {
        if (!options.AutoCommitDocs)
            return;

        var relativeFiles = generatedFiles
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (relativeFiles.Count == 0)
            return;

        await RunGitAsync(repoRoot, BuildGitPathCommand("add", relativeFiles), ct);

        var stagedChanges = await TryRunGitAsync(repoRoot, BuildGitPathCommand("diff --cached --name-only --", relativeFiles), ct);
        if (string.IsNullOrWhiteSpace(stagedChanges))
        {
            logger.LogInformation("Generated CODEGRAPH.md files for {RepoRoot} produced no staged changes", repoRoot);
            return;
        }

        await RunGitAsync(repoRoot, $"commit -m \"{EscapeGitArgument(options.AutoCommitMessage)}\"", ct);

        if (options.AutoPushDocs)
            await RunGitAsync(repoRoot, "push origin HEAD", ct);
    }

    private async Task<string> TryRunGitAsync(string repoRoot, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {arguments}");

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed with exit code {proc.ExitCode}: {stderr}");

        return stdout;
    }

    private async Task RunGitAsync(string repoRoot, string arguments, CancellationToken ct)
    {
        _ = await TryRunGitAsync(repoRoot, arguments, ct);
    }

    private static string BuildGitPathCommand(string prefix, IReadOnlyList<string> paths)
    {
        var escapedPaths = paths.Select(EscapeGitArgument);
        return $"{prefix} {string.Join(" ", escapedPaths)}";
    }

    private static string EscapeGitArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
