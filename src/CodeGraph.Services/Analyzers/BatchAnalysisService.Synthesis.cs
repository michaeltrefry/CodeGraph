using System.Net.Http.Json;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Exceptions;
using CodeGraph.Services.Models;
using CodeGraph.Services.Extensions;
using CodeGraph.Services.Prompts;

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
                new AnalysisPrompt(
                    await GetRepositoryAnalysisSystemPromptAsync("repository synthesis"),
                    promptText),
                new AnalysisRequestOptions(MaxTokens: options.MaxTokensPerSynthesis),
                ct);
        }
        catch (RetryableAnalysisException)
        {
            throw;
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

    private Task<string> GetRepositoryAnalysisSystemPromptAsync(string usage)
        => AgentPromptExecution.GetEffectivePromptOrDefaultAsync(
            agentPromptService,
            AgentPromptCatalog.RepositoryAnalysisSystemPromptKey,
            AnalysisPromptBuilder.SystemPrompt,
            logger,
            usage);

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
        {
            var publishedCommitSha = await PublishGeneratedDocsAsync(repoInfo.LocalPath, generatedFiles, ct);
            if (!string.IsNullOrWhiteSpace(publishedCommitSha))
                await UpdateRepositoryCommitStateAsync(repoName, publishedCommitSha);
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

    private async Task<string?> PublishGeneratedDocsAsync(string repoRoot, IReadOnlyList<string> generatedFiles, CancellationToken ct)
    {
        if (!options.AutoCommitDocs)
            return null;

        var relativeFiles = generatedFiles
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (relativeFiles.Count == 0)
            return null;

        await RunGitAsync(repoRoot, ["add", "--", .. relativeFiles], ct);

        var stagedChanges = await TryRunGitAsync(repoRoot, ["diff", "--cached", "--name-only", "--", .. relativeFiles], ct);
        if (string.IsNullOrWhiteSpace(stagedChanges))
        {
            logger.LogInformation("Generated CODEGRAPH.md files for {RepoRoot} produced no staged changes", repoRoot);
            return null;
        }

        await RunGitAsync(repoRoot,
            ["-c", $"user.name={options.AutoCommitAuthorName}",
             "-c", $"user.email={options.AutoCommitAuthorEmail}",
             "commit", "-m", options.AutoCommitMessage], ct);

        var publishedCommitSha = await TryGetHeadCommitShaAsync(repoRoot, ct);
        if (options.AutoPushDocs)
            await RunGitAsync(repoRoot, ["push", "origin", "HEAD"], ct);

        return publishedCommitSha;
    }

    private async Task UpdateRepositoryCommitStateAsync(string repoName, string commitSha)
    {
        await store.UpdateRepositoryCommitShaAsync(repoName, commitSha);
        await store.UpsertSyncStateAsync(new SyncStateEntity
        {
            Project = repoName,
            LastCommitSha = commitSha,
            LastSyncAt = DateTime.UtcNow,
            Status = "idle",
            ErrorMessage = null
        });
    }

    private async Task<string?> TryGetHeadCommitShaAsync(string repoRoot, CancellationToken ct)
    {
        var sha = (await TryRunGitAsync(repoRoot, ["rev-parse", "HEAD"], ct)).Trim();
        return sha.Length >= 40 ? sha : null;
    }

    private async Task<string> TryRunGitAsync(string repoRoot, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        var formattedArgs = FormatGitArguments(arguments);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {formattedArgs}");

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {formattedArgs} failed with exit code {proc.ExitCode}: {stderr}");

        return stdout;
    }

    private async Task RunGitAsync(string repoRoot, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        _ = await TryRunGitAsync(repoRoot, arguments, ct);
    }

    private static string FormatGitArguments(IReadOnlyList<string> arguments)
    {
        return string.Join(" ", arguments.Select(argument =>
            argument.Any(char.IsWhiteSpace)
                ? $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
                : argument));
    }
}
