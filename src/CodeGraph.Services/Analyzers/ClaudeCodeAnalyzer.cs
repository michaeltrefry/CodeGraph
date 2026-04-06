using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Models;

namespace CodeGraph.Services.Analyzers;

public partial class ClaudeCodeAnalyzer(
    AnthropicClient client,
    AnalysisOptions options,
    IGraphStore store,
    IFileSystem fileSystem,
    ILogger<ClaudeCodeAnalyzer> logger)
    : ICodeAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = CodeGraphJsonDefaults.CamelCase;

    public async Task<RepoAnalysis> AnalyzeRepositoryAsync(
        string projectName, string rootPath, string? modelOverride = null,
        Func<ProjectAnalysis, Task>? onProjectComplete = null,
        CancellationToken ct = default)
    {
        var model = modelOverride ?? options.Model;

        // 1. Discover project directories (folders containing .csproj files)
        var projectDirs = DiscoverProjects(rootPath);
        logger.LogInformation("Found {Count} projects in {Repo}",
            projectDirs.Count, projectName);

        // 2. Fan out: analyze each project in parallel, bounded by MaxParallelAnalyses
        using var semaphore = new SemaphoreSlim(options.MaxParallelAnalyses);
        var projectTasks = projectDirs.Select(async dir =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var result = await AnalyzeProjectSafeAsync(dir, projectName, model, ct);
                if (onProjectComplete is not null)
                    await onProjectComplete(result);
                return result;
            }
            finally { semaphore.Release(); }
        });
        var projectAnalyses = await Task.WhenAll(projectTasks);

        // 3. Synthesize: build repo-level summary from project analyses
        var (summary, confidence) = await SynthesizeRepoSummaryAsync(
            projectName, projectAnalyses, model, ct);

        return new RepoAnalysis(summary, confidence, model, projectAnalyses.ToList());
    }

    private async Task<ProjectAnalysis> AnalyzeProjectSafeAsync(
        string projectDir, string repoContext, string model, CancellationToken ct)
    {
        var csprojName = Path.GetFileNameWithoutExtension(
            fileSystem.EnumerateFiles(projectDir, "*.csproj", SearchOption.TopDirectoryOnly).First());
        try
        {
            return await AnalyzeProjectAsync(csprojName, projectDir, repoContext, model, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Analysis failed for project {Project}; substituting low-confidence placeholder", csprojName);
            return new ProjectAnalysis(
                csprojName,
                $"Analysis failed: {ex.Message}",
                ConfidenceLevel.Low,
                [],
                [],
                [],
                []);
        }
    }

    public async Task<ProjectAnalysis> AnalyzeProjectAsync(
        string projectName, string projectPath, string repoContext,
        string? modelOverride = null, CancellationToken ct = default)
    {
        var model = modelOverride ?? options.Model;

        // 1. Gather graph context for this specific project
        var nodes = await store.SearchNodesAsync(repoContext, projectName);
        var graphContext = BuildGraphContext(nodes);

        // 2. Read source files scoped to this project directory only
        var files = await GatherProjectFiles(projectPath);

        logger.LogInformation("Analyzing project {Project} ({FileCount} files)",
            projectName, files.Count);

        // 3. Build focused per-project prompt
        var prompt = BuildProjectAnalysisPrompt(projectName, repoContext,
            graphContext, files);

        // 4. Call Claude
        var response = await CallClaudeAsync(new MessageCreateParams
        {
            Model = model,
            MaxTokens = options.MaxTokensPerAnalysis,
            System = GetSystemPrompt(),
            Messages = [new() { Role = Role.User, Content = prompt }],
        }, ct);

        return ParseProjectAnalysis(GetTextContent(response));
    }

    public async Task<AnalysisUpdate?> AnalyzeChangesAsync(
        string projectName, string rootPath, string diff, string commitMessage,
        string existingSummary, CancellationToken ct = default)
    {
        var prompt = BuildChangeAnalysisPrompt(projectName, diff,
            commitMessage, existingSummary);

        var response = await CallClaudeAsync(new MessageCreateParams
        {
            Model = options.Model, // always Sonnet for incremental — cost matters
            MaxTokens = options.MaxTokensPerSynthesis,
            System = GetSystemPrompt(),
            Messages = [new() { Role = Role.User, Content = prompt }],
        }, ct);

        return ParseChangeAnalysis(GetTextContent(response));
    }

    // ── Private methods ──────────────────────────────────────────────────

    private async Task<(string Summary, ConfidenceLevel Confidence)>
        SynthesizeRepoSummaryAsync(string projectName,
            ProjectAnalysis[] projects, string model, CancellationToken ct)
    {
        var crossRepoEdges = await store.FindCrossRepoEdgesAsync(projectName);

        var prompt = BuildSynthesisPrompt(projectName, projects, crossRepoEdges);

        logger.LogInformation("Synthesizing repo summary for {Project}", projectName);

        var response = await CallClaudeAsync(new MessageCreateParams
        {
            Model = model,
            MaxTokens = options.MaxTokensPerSynthesis,
            System = GetSystemPrompt(),
            Messages = [new() { Role = Role.User, Content = prompt }],
        }, ct);

        return ParseRepoSummary(GetTextContent(response));
    }

    private async Task<Message> CallClaudeAsync(
        MessageCreateParams request, CancellationToken ct)
    {
        const int maxRetries = 5;
        var delay = TimeSpan.FromSeconds(15);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await client.Messages.Create(request, ct);
            }
            catch (AnthropicRateLimitException) when (attempt < maxRetries)
            {
                logger.LogWarning(
                    "Rate limit hit — waiting {Delay}s before retry {Attempt}/{Max}",
                    delay.TotalSeconds, attempt + 1, maxRetries);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 120));
            }
        }
    }

    private static string GetSystemPrompt() => """
        You are analyzing source code for a domain name reseller and auctioneer.
        The company operates HugeDomains.com (domain resale), DropCatch.com
        (domain backorders and auctions), and NameBright.com (full domain management).

        Key business concepts:
        - Drop catching: competing to register expiring domains at the moment they
          become available
        - Domain valuation: AI-augmented scoring of domain value before purchase
        - EPP: Extensible Provisioning Protocol for registry communication
        - Backorders: customer requests to catch specific expiring domains
        - Auctions: competitive bidding on caught or listed domains

        When describing code, use domain industry business terms.
        Be specific about what the code does, not just its structure.
        If you cannot determine the business purpose with confidence, say so.

        Respond in JSON format matching the provided schema.
        """;

    private async Task<IReadOnlyList<(string Path, string Content)>> GatherProjectFiles(
        string projectPath)
    {
        var files = new List<(string, string)>();

        var matcher = new Matcher();
        matcher.AddInclude("**/*.cs");
        matcher.AddInclude("appsettings*.json");
        matcher.AddExclude("bin/**");
        matcher.AddExclude("obj/**");

        foreach (var match in matcher.GetResultsInFullPath(projectPath))
        {
            var content = await fileSystem.ReadAllTextAsync(match);
            // Skip files larger than 512KB
            if (content.Length <= 512 * 1024)
            {
                var relPath = Path.GetRelativePath(projectPath, match);
                files.Add((relPath, content));
            }
        }

        return files;
    }

    private IReadOnlyList<string> DiscoverProjects(string rootPath)
    {
        return fileSystem.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories)
            .Where(f =>
            {
                // Normalize to forward slashes for consistent matching on all platforms
                var normalized = f.Replace('\\', '/');
                return !normalized.Contains("/bin/") && !normalized.Contains("/obj/");
            })
            .Select(f => Path.GetDirectoryName(f))
            .Where(d => d is not null)
            .Select(d => d!)
            .Distinct()
            .ToList();
    }
}
