using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Services.Models;

namespace TC.CodeGraphApi.Services;

public class ClaudeCodeAnalyzer : ICodeAnalyzer
{
    private readonly AnthropicClient _client;
    private readonly AnalysisOptions _options;
    private readonly IGraphStore _store;
    private readonly ILogger<ClaudeCodeAnalyzer> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ClaudeCodeAnalyzer(
        AnthropicClient client,
        IOptions<AnalysisOptions> options,
        IGraphStore store,
        ILogger<ClaudeCodeAnalyzer> logger)
    {
        _client = client;
        _options = options.Value;
        _store = store;
        _logger = logger;
    }

    public async Task<RepoAnalysis> AnalyzeRepositoryAsync(
        string projectName, string rootPath, string? modelOverride = null,
        CancellationToken ct = default)
    {
        var model = modelOverride ?? _options.Model;

        // 1. Discover project directories (folders containing .csproj files)
        var projectDirs = DiscoverProjects(rootPath);
        _logger.LogInformation("Found {Count} projects in {Repo}",
            projectDirs.Count, projectName);

        // 2. Fan out: analyze each project in parallel
        var projectTasks = projectDirs.Select(dir =>
            AnalyzeProjectAsync(
                Path.GetFileNameWithoutExtension(
                    Directory.GetFiles(dir, "*.csproj").First()),
                dir, projectName, model, ct));

        var projectAnalyses = await Task.WhenAll(projectTasks);

        // 3. Synthesize: build repo-level summary from project analyses
        var (summary, confidence) = await SynthesizeRepoSummaryAsync(
            projectName, projectAnalyses, model, ct);

        return new RepoAnalysis(summary, confidence, model, projectAnalyses.ToList());
    }

    public async Task<ProjectAnalysis> AnalyzeProjectAsync(
        string projectName, string projectPath, string repoContext,
        string? modelOverride = null, CancellationToken ct = default)
    {
        var model = modelOverride ?? _options.Model;

        // 1. Gather graph context for this specific project
        var nodes = await _store.SearchNodesAsync(repoContext, projectName);
        var graphContext = BuildGraphContext(nodes);

        // 2. Read source files scoped to this project directory only
        var files = await GatherProjectFiles(projectPath);

        _logger.LogInformation("Analyzing project {Project} ({FileCount} files)",
            projectName, files.Count);

        // 3. Build focused per-project prompt
        var prompt = BuildProjectAnalysisPrompt(projectName, repoContext,
            graphContext, files);

        // 4. Call Claude
        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = _options.MaxTokensPerAnalysis,
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

        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = _options.Model, // always Sonnet for incremental — cost matters
            MaxTokens = _options.MaxTokensPerSynthesis,
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
        var crossRepoEdges = await _store.FindCrossRepoEdgesAsync(projectName);

        var prompt = BuildSynthesisPrompt(projectName, projects, crossRepoEdges);

        _logger.LogInformation("Synthesizing repo summary for {Project}", projectName);

        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = _options.MaxTokensPerSynthesis,
            System = GetSystemPrompt(),
            Messages = [new() { Role = Role.User, Content = prompt }],
        }, ct);

        return ParseRepoSummary(GetTextContent(response));
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

    private static async Task<IReadOnlyList<(string Path, string Content)>> GatherProjectFiles(
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
            var content = await File.ReadAllTextAsync(match);
            // Skip files larger than 512KB
            if (content.Length <= 512 * 1024)
            {
                var relPath = Path.GetRelativePath(projectPath, match);
                files.Add((relPath, content));
            }
        }

        return files;
    }

    private static string BuildGraphContext(IReadOnlyList<GraphNode> nodes)
    {
        if (nodes.Count == 0)
            return "(No graph data available yet)";

        var sb = new StringBuilder();
        foreach (var group in nodes.GroupBy(n => n.Label))
        {
            sb.AppendLine($"### {group.Key}s");
            foreach (var node in group.Take(50))
            {
                sb.AppendLine($"- {node.QualifiedName}");
            }
            if (group.Count() > 50)
                sb.AppendLine($"  ... and {group.Count() - 50} more");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildProjectAnalysisPrompt(string projectName,
        string repoContext, string graphContext,
        IReadOnlyList<(string Path, string Content)> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Analyze Project: {projectName}");
        sb.AppendLine($"Part of repository: {repoContext}");
        sb.AppendLine();
        sb.AppendLine("## Graph Context (already extracted)");
        sb.AppendLine(graphContext);
        sb.AppendLine();
        sb.AppendLine("## Source Files");
        foreach (var (path, content) in files)
        {
            sb.AppendLine($"### {path}");
            sb.AppendLine("```csharp");
            sb.AppendLine(content);
            sb.AppendLine("```");
            sb.AppendLine();
        }
        sb.AppendLine("## Instructions");
        sb.AppendLine("""
            Analyze this single project/assembly and produce:
            1. A summary (1-2 paragraphs) describing what this project does
               in business terms.
            2. A confidence level (high/medium/low) for your analysis.
            3. Its public endpoints (if any) with route, method, and description.
            4. Its services with descriptions and DI lifetime.
            5. External dependencies (databases, other APIs, message queues).
            6. Database tables it accesses.

            Respond as JSON matching this schema:
            {
              "projectName": "string",
              "summary": "string",
              "confidence": "high|medium|low",
              "endpoints": [
                { "route": "string", "httpMethod": "string",
                  "description": "string",
                  "requestModel": "string|null",
                  "responseModel": "string|null" }
              ],
              "services": [
                { "name": "string", "description": "string",
                  "interfaceName": "string|null", "lifetime": "string" }
              ],
              "externalDependencies": ["string"],
              "databaseTables": ["string"]
            }
            """);
        return sb.ToString();
    }

    private static string BuildSynthesisPrompt(string projectName,
        ProjectAnalysis[] projects,
        IReadOnlyList<CrossRepoEdge> crossRepoEdges)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Synthesize Repository Summary: {projectName}");
        sb.AppendLine();
        sb.AppendLine("## Project Analyses");
        foreach (var p in projects)
        {
            sb.AppendLine($"### {p.ProjectName} (confidence: {p.Confidence})");
            sb.AppendLine(p.Summary);
            sb.AppendLine();
        }
        sb.AppendLine("## Cross-Repository Dependencies");
        if (crossRepoEdges.Count == 0)
        {
            sb.AppendLine("(No cross-repo dependencies found yet)");
        }
        else
        {
            foreach (var edge in crossRepoEdges)
                sb.AppendLine($"- {edge.SourceProject} --{edge.Type}--> {edge.TargetProject}");
        }
        sb.AppendLine();
        sb.AppendLine("## Instructions");
        sb.AppendLine("""
            Based on the per-project analyses above, write a repo-level summary
            (2-4 paragraphs) describing:
            1. What this service does as a whole in business terms.
            2. How the projects within it work together.
            3. What it depends on and what depends on it (cross-repo).
            4. An overall confidence level.

            Respond as JSON: { "summary": "string", "confidence": "high|medium|low" }
            """);
        return sb.ToString();
    }

    private static string BuildChangeAnalysisPrompt(string projectName, string diff,
        string commitMessage, string existingSummary)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Incremental Analysis: {projectName}");
        sb.AppendLine();
        sb.AppendLine("## Current Summary");
        sb.AppendLine(existingSummary);
        sb.AppendLine();
        sb.AppendLine("## Commit Message");
        sb.AppendLine(commitMessage);
        sb.AppendLine();
        sb.AppendLine("## Diff");
        sb.AppendLine("```diff");
        sb.AppendLine(diff.Length > 50_000 ? diff[..50_000] + "\n... (truncated)" : diff);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Instructions");
        sb.AppendLine("""
            Review this diff against the current summary. Determine:
            1. Does this change affect the business-level description of the service?
               (New endpoints, removed features, changed behavior, new dependencies)
            2. Or is it trivial? (Refactoring, tests, comments, formatting, bug fixes
               that don't change described behavior)

            If the summary needs updating, respond with:
            {
              "needsUpdate": true,
              "updatedSummary": "the full revised summary",
              "confidence": "high|medium|low",
              "changeDescription": "brief description of what changed and why the summary was updated"
            }

            If the summary is still accurate, respond with:
            { "needsUpdate": false }
            """);
        return sb.ToString();
    }

    private static IReadOnlyList<string> DiscoverProjects(string rootPath)
    {
        return Directory.GetFiles(rootPath, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"))
            .Select(f => Path.GetDirectoryName(f))
            .Where(d => d is not null)
            .Select(d => d!)
            .Distinct()
            .ToList();
    }

    // ── JSON Parsing ─────────────────────────────────────────────────────

    private static string GetTextContent(Message response)
    {
        // Anthropic SDK uses discriminated unions with TryPick* methods
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var textBlock))
                return textBlock.Text;
        }
        return "";
    }

    private static ProjectAnalysis ParseProjectAnalysis(string json)
    {
        // Strip markdown code fences if present
        json = StripCodeFences(json);

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new ProjectAnalysis(
            ProjectName: root.GetProperty("projectName").GetString() ?? "",
            Summary: root.GetProperty("summary").GetString() ?? "",
            Confidence: ParseConfidence(root.GetProperty("confidence").GetString()),
            Endpoints: ParseEndpoints(root),
            Services: ParseServices(root),
            ExternalDependencies: ParseStringArray(root, "externalDependencies"),
            DatabaseTables: ParseStringArray(root, "databaseTables")
        );
    }

    private static (string Summary, ConfidenceLevel Confidence) ParseRepoSummary(string json)
    {
        json = StripCodeFences(json);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return (
            root.GetProperty("summary").GetString() ?? "",
            ParseConfidence(root.GetProperty("confidence").GetString())
        );
    }

    private static AnalysisUpdate? ParseChangeAnalysis(string json)
    {
        json = StripCodeFences(json);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.GetProperty("needsUpdate").GetBoolean())
            return null;

        return new AnalysisUpdate(
            UpdatedSummary: root.GetProperty("updatedSummary").GetString() ?? "",
            Confidence: ParseConfidence(root.GetProperty("confidence").GetString()),
            ChangeDescription: root.GetProperty("changeDescription").GetString() ?? ""
        );
    }

    private static ConfidenceLevel ParseConfidence(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "high" => ConfidenceLevel.High,
            "low" => ConfidenceLevel.Low,
            _ => ConfidenceLevel.Medium
        };
    }

    private static IReadOnlyList<EndpointDescription> ParseEndpoints(JsonElement root)
    {
        if (!root.TryGetProperty("endpoints", out var arr))
            return [];

        return arr.EnumerateArray().Select(e => new EndpointDescription(
            Route: e.GetProperty("route").GetString() ?? "",
            HttpMethod: e.GetProperty("httpMethod").GetString() ?? "",
            Description: e.GetProperty("description").GetString() ?? "",
            RequestModel: e.TryGetProperty("requestModel", out var rm) ? rm.GetString() : null,
            ResponseModel: e.TryGetProperty("responseModel", out var rsp) ? rsp.GetString() : null
        )).ToList();
    }

    private static IReadOnlyList<ServiceDescription> ParseServices(JsonElement root)
    {
        if (!root.TryGetProperty("services", out var arr))
            return [];

        return arr.EnumerateArray().Select(s => new ServiceDescription(
            Name: s.GetProperty("name").GetString() ?? "",
            Description: s.GetProperty("description").GetString() ?? "",
            InterfaceName: s.TryGetProperty("interfaceName", out var iface) ? iface.GetString() : null,
            Lifetime: s.TryGetProperty("lifetime", out var lt) ? lt.GetString() ?? "scoped" : "scoped"
        )).ToList();
    }

    private static List<string> ParseStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arr))
            return [];

        return arr.EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static string StripCodeFences(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
                text = text[(firstNewline + 1)..];
        }
        if (text.EndsWith("```"))
            text = text[..^3];
        return text.Trim();
    }
}
