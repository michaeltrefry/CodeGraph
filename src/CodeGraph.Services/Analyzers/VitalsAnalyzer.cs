using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Services.Analyzers;

public class VitalsAnalyzer(
    IGraphStore store,
    AnthropicClient anthropic,
    IOptions<AnalysisOptions> analysisOptionsAccessor,
    IFileSystem fileSystem,
    ILintRunner lintRunner,
    ILogger<VitalsAnalyzer> logger) : IVitalsAnalyzer
{
    private readonly AnalysisOptions analysisOptions = analysisOptionsAccessor.Value;
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".cfm", ".cfc", ".sql", ".vue", ".py", ".java"
    };

    private static readonly HashSet<string> TestPathSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "spec", "specs", "testing", ".tests", ".test"
    };

    public async Task ComputeMetricsAsync(string projectName, string repoPath, CancellationToken ct = default)
    {
        logger.LogInformation("Computing vitals metrics for {Project}", projectName);
        var sw = Stopwatch.StartNew();

        // 1. Get indexed file nodes from store to know which files & their DotnetProject mapping
        var allNodes = await store.GetAllNodesByProjectAsync(projectName);
        var fileNodes = allNodes.Where(n => n.Label == "File").ToList();

        if (fileNodes.Count == 0)
        {
            logger.LogWarning("No File nodes found for {Project} — skipping vitals", projectName);
            return;
        }

        // Build file path → DotnetProject mapping
        var fileToDotnetProject = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var fn in fileNodes)
        {
            var relPath = fn.FilePath.Replace('\\', '/');
            fileToDotnetProject[relPath] = fn.DotnetProject;
        }

        // Build set of file paths containing untrusted nodes (do_not_trust flag)
        var untrustedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in allNodes.Where(n => n.DoNotTrust))
        {
            var path = n.FilePath.Replace('\\', '/');
            if (!string.IsNullOrEmpty(path))
                untrustedFiles.Add(path);
        }

        // Also include source files on disk that may not have File nodes
        var sourceFilesOnDisk = GetSourceFiles(repoPath);
        foreach (var relPath in sourceFilesOnDisk)
        {
            fileToDotnetProject.TryAdd(relPath, null);
        }

        var filePaths = fileToDotnetProject.Keys.ToList();

        // 2. Run git-based metrics + ESLint in parallel
        var churnTask = ComputeChurnAsync(repoPath, ct: ct);
        var couplingTask = ComputeCouplingAsync(repoPath, ct: ct);
        var knowledgeTask = ComputeKnowledgeRiskAsync(repoPath, ct: ct);

        // Only lint if the repo has TS/JS files
        var hasLintableFiles = filePaths.Any(f =>
            f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase));
        var lintTask = hasLintableFiles
            ? lintRunner.LintProjectAsync(repoPath, ct)
            : Task.FromResult<IReadOnlyDictionary<string, LintResult>>(
                new Dictionary<string, LintResult>());

        await Task.WhenAll(churnTask, couplingTask, knowledgeTask, lintTask);

        var churn = await churnTask;
        var coupling = await couplingTask;
        var knowledge = await knowledgeTask;
        var lint = await lintTask;

        if (lint.Count > 0)
            logger.LogInformation("ESLint found issues in {Count} files for {Project}",
                lint.Count, projectName);

        // 3. Run structural complexity
        var complexity = ComputeComplexityBatch(repoPath, filePaths);

        // 4. Build FileMetricsEntity list
        var now = DateTime.UtcNow;
        var metrics = new List<FileMetricsEntity>();

        foreach (var relPath in filePaths)
        {
            ct.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(relPath);
            if (!SourceExtensions.Contains(ext)) continue;

            churn.TryGetValue(relPath, out var ch);
            coupling.TryGetValue(relPath, out var co);
            knowledge.TryGetValue(relPath, out var kn);
            complexity.TryGetValue(relPath, out var cx);
            lint.TryGetValue(relPath, out var ln);

            var role = ClassifyRole(relPath);
            var health = ComputeHealthScore(ch, co, kn, cx, ln);
            var changes = ch?.Changes ?? 0;
            var couplingPartners = co?.CouplingPartners ?? 0;
            var risk = ComputeRiskScore(health, changes, role, couplingPartners);
            var isUntrusted = untrustedFiles.Contains(relPath);
            var trust = ComputeTrustScore(ch, ln, isUntrusted);

            fileToDotnetProject.TryGetValue(relPath, out var dotnetProject);

            metrics.Add(new FileMetricsEntity
            {
                Project = projectName,
                FilePath = relPath,
                DotnetProject = dotnetProject,
                Changes = changes,
                LinesAdded = ch?.LinesAdded ?? 0,
                LinesRemoved = ch?.LinesRemoved ?? 0,
                AuthorCount = ch?.AuthorCount ?? 0,
                LastChangeAt = ch?.LastChangeAt,
                ComplexityScore = cx?.Score ?? 0,
                MaxNestingDepth = cx?.MaxNestingDepth ?? 0,
                DeepNestingLines = cx?.DeepNestingLines ?? 0,
                FunctionCount = cx?.FunctionCount ?? 0,
                LongestFunction = cx?.LongestFunction ?? 0,
                LintErrors = ln?.ErrorCount ?? 0,
                LintWarnings = ln?.WarningCount ?? 0,
                TrustScore = trust,
                MaxCouplingStrength = co?.MaxCouplingStrength ?? 0,
                CouplingPartners = couplingPartners,
                TruckFactor = kn?.TruckFactor ?? 0,
                TopAuthors = kn?.TopAuthors is { Count: > 0 }
                    ? JsonSerializer.Serialize(kn.TopAuthors.Take(5))
                    : null,
                HealthScore = health,
                Role = role,
                RiskScore = risk,
                ComputedAt = now
            });
        }

        // 5. Delete old metrics and upsert new
        await store.DeleteFileMetricsAsync(projectName);
        await store.UpsertFileMetricsBatchAsync(projectName, metrics);

        // 6. Compute and store project health summaries
        await ComputeAndStoreHealthSummaries(projectName, metrics);

        logger.LogInformation(
            "Vitals complete for {Project}: {Count} files analyzed in {Elapsed:F1}s",
            projectName, metrics.Count, sw.Elapsed.TotalSeconds);
    }

    public async Task AnalyzeHealthAsync(string projectName, CancellationToken ct = default)
    {
        var healthSummaries = await store.GetProjectHealthSummariesAsync(projectName);
        if (healthSummaries.Count == 0)
        {
            logger.LogWarning("No health summaries for {Project} — run ComputeMetricsAsync first", projectName);
            return;
        }

        var model = analysisOptions.Model;
        var now = DateTime.UtcNow;

        // Load security summary for inclusion in health prompts
        var securitySummary = await store.GetProjectSecuritySummaryAsync(projectName);

        // Analyze each DotnetProject separately, then repo-level
        var projectSummaries = healthSummaries.Where(s => !string.IsNullOrEmpty(s.DotnetProject)).ToList();
        var repoSummary = healthSummaries.FirstOrDefault(s => string.IsNullOrEmpty(s.DotnetProject));

        // Run per-project analyses in parallel — each is an independent Claude API call
        var projectTasks = projectSummaries.Select(async summary =>
        {
            var metrics = await store.GetFileMetricsAsync(projectName, summary.DotnetProject);
            if (metrics.Count == 0) return;

            var prompt = BuildHealthPrompt(projectName, summary.DotnetProject!, summary, metrics, securitySummary);

            try
            {
                var response = await CallClaudeAsync(prompt, model, ct);
                if (!string.IsNullOrWhiteSpace(response))
                {
                    await store.UpsertProjectHealthAnalysisAsync(new ProjectHealthAnalysisEntity
                    {
                        Project = projectName,
                        DotnetProject = summary.DotnetProject,
                        Analysis = response,
                        Confidence = summary.OverallHealth < 4.0 ? "high" : "medium",
                        ModelUsed = model,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                    logger.LogInformation("Health analysis stored for {Project}/{Dp}",
                        projectName, summary.DotnetProject);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Health analysis failed for {Project}/{Dp}",
                    projectName, summary.DotnetProject);
            }
        }).ToList();

        await Task.WhenAll(projectTasks);

        // Repo-level synthesis
        if (repoSummary is not null)
        {
            var allMetrics = await store.GetFileMetricsAsync(projectName);
            var prompt = BuildRepoHealthPrompt(projectName, repoSummary, projectSummaries, allMetrics, securitySummary);

            try
            {
                var response = await CallClaudeAsync(prompt, model, ct);
                if (!string.IsNullOrWhiteSpace(response))
                {
                    await store.UpsertProjectHealthAnalysisAsync(new ProjectHealthAnalysisEntity
                    {
                        Project = projectName,
                        DotnetProject = null,
                        Analysis = response,
                        Confidence = repoSummary.OverallHealth < 4.0 ? "high" : "medium",
                        ModelUsed = model,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                    logger.LogInformation("Repo-level health analysis stored for {Project}", projectName);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Repo-level health analysis failed for {Project}", projectName);
            }
        }
    }

    private static string BuildHealthPrompt(string repoName, string dotnetProject,
        ProjectHealthSummaryEntity summary, IReadOnlyList<FileMetricsEntity> metrics,
        ProjectSecuritySummaryEntity? securitySummary = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are a codebase health analyst. Analyze the following metrics for project '{dotnetProject}' in repository '{repoName}'.");
        sb.AppendLine();
        sb.AppendLine($"Overall health: {summary.OverallHealth:F1}/10");
        sb.AppendLine($"Total files: {summary.TotalFiles}");
        sb.AppendLine($"Hotspots (health < 4.0): {summary.HotspotCount}");
        sb.AppendLine($"Alerts (health < 2.5): {summary.AlertCount}");
        AppendSecurityContext(sb, securitySummary);
        sb.AppendLine();

        var hotspots = metrics.OrderBy(m => m.HealthScore).Take(20).ToList();
        if (hotspots.Count > 0)
        {
            sb.AppendLine("File-level metrics (worst health first):");
            sb.AppendLine("| File | Health | Churn (90d) | Complexity | Coupling | Truck Factor | Role |");
            sb.AppendLine("|------|--------|-------------|------------|----------|-------------|------|");
            foreach (var m in hotspots)
            {
                sb.AppendLine($"| {m.FilePath} | {m.HealthScore:F1} | {m.Changes} changes, +{m.LinesAdded}/-{m.LinesRemoved} | {m.ComplexityScore}/100 (depth {m.MaxNestingDepth}) | {m.MaxCouplingStrength:F2} ({m.CouplingPartners} partners) | {m.TruckFactor} | {m.Role} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("""
            Provide a concise health analysis covering:
            1. **Risk Assessment**: What are the highest-risk files and why?
            2. **Remediation Priorities**: Top 3 actions ranked by impact.
            3. **Knowledge Risk**: Any bus-factor concerns (truck factor 1)?
            4. **Coupling Concerns**: Files with high coupling that may cause cascading issues.
            5. **Security**: Any security concerns from the security scan (if data available).

            Be specific — reference actual file names and metrics. Keep it under 300 words.
            """);

        return sb.ToString();
    }

    private static string BuildRepoHealthPrompt(string repoName,
        ProjectHealthSummaryEntity repoSummary,
        IReadOnlyList<ProjectHealthSummaryEntity> projectSummaries,
        IReadOnlyList<FileMetricsEntity> allMetrics,
        ProjectSecuritySummaryEntity? securitySummary = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are a codebase health analyst. Synthesize the health status of repository '{repoName}'.");
        sb.AppendLine();
        sb.AppendLine($"Repository health: {repoSummary.OverallHealth:F1}/10");
        sb.AppendLine($"Total files: {repoSummary.TotalFiles}");
        sb.AppendLine($"Hotspots: {repoSummary.HotspotCount}");
        sb.AppendLine($"Alerts: {repoSummary.AlertCount}");
        AppendSecurityContext(sb, securitySummary);
        sb.AppendLine();

        if (projectSummaries.Count > 0)
        {
            sb.AppendLine("Per-project breakdown:");
            sb.AppendLine("| Project | Health | Files | Hotspots | Alerts |");
            sb.AppendLine("|---------|--------|-------|----------|--------|");
            foreach (var ps in projectSummaries.OrderBy(p => p.OverallHealth))
                sb.AppendLine($"| {ps.DotnetProject} | {ps.OverallHealth:F1} | {ps.TotalFiles} | {ps.HotspotCount} | {ps.AlertCount} |");
            sb.AppendLine();
        }

        var topHotspots = allMetrics.OrderBy(m => m.HealthScore).Take(10).ToList();
        if (topHotspots.Count > 0)
        {
            sb.AppendLine("Top 10 riskiest files across all projects:");
            foreach (var m in topHotspots)
                sb.AppendLine($"- {m.FilePath} (health {m.HealthScore:F1}, {m.Changes} changes, truck factor {m.TruckFactor})");
            sb.AppendLine();
        }

        sb.AppendLine("""
            Provide a repository-level health summary covering:
            1. **Overall Assessment**: Is this repo healthy, at risk, or critical?
            2. **Worst Projects**: Which projects need attention and why?
            3. **Top Remediation Actions**: 3-5 prioritized recommendations.
            4. **Systemic Risks**: Cross-cutting concerns (knowledge concentration, high coupling between projects).
            5. **Security**: Highlight any security findings if present (secrets, vulnerable packages, attack surface).

            Be specific and actionable. Keep it under 400 words.
            """);

        return sb.ToString();
    }

    private static void AppendSecurityContext(StringBuilder sb, ProjectSecuritySummaryEntity? sec)
    {
        if (sec is null) return;

        sb.AppendLine();
        sb.AppendLine($"Security score: {sec.SecurityScore:F1}/10");
        var total = sec.CriticalCount + sec.HighCount + sec.MediumCount + sec.LowCount;
        if (total > 0)
        {
            sb.Append($"Security findings: {total} total");
            if (sec.CriticalCount > 0) sb.Append($", {sec.CriticalCount} critical");
            if (sec.HighCount > 0) sb.Append($", {sec.HighCount} high");
            if (sec.MediumCount > 0) sb.Append($", {sec.MediumCount} medium");
            if (sec.LowCount > 0) sb.Append($", {sec.LowCount} low");
            sb.AppendLine();
        }
    }

    private async Task<string?> CallClaudeAsync(string prompt, string model, CancellationToken ct)
    {
        const int maxRetries = 3;
        var delay = TimeSpan.FromSeconds(15);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var response = await anthropic.Messages.Create(new MessageCreateParams
                {
                    Model = model,
                    MaxTokens = 1024,
                    Messages = [new MessageParam { Role = "user", Content = prompt }]
                }, ct);

                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out var textBlock) &&
                        !string.IsNullOrWhiteSpace(textBlock.Text))
                        return textBlock.Text;
                }
                return null;
            }
            catch (AnthropicRateLimitException) when (attempt < maxRetries)
            {
                logger.LogWarning("Rate limit — retrying in {Delay}s ({Attempt}/{Max})",
                    delay.TotalSeconds, attempt + 1, maxRetries);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
    }

    // --- Git Analysis Methods ---

    internal async Task<Dictionary<string, ChurnData>> ComputeChurnAsync(
        string repoPath, int days = 90, CancellationToken ct = default)
    {
        var result = new Dictionary<string, ChurnData>(StringComparer.OrdinalIgnoreCase);

        var output = await RunGitAsync(repoPath,
            $"log --numstat --format=%H%x00%aI%x00%aN --no-merges --since={days}.days.ago", ct);

        if (string.IsNullOrWhiteSpace(output)) return result;

        string? currentAuthor = null;
        DateTime? currentDate = null;
        var fileAuthors = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\0');
            if (parts.Length == 3)
            {
                // Commit header: hash\0date\0author
                currentAuthor = parts[2].Trim();
                DateTime.TryParse(parts[1], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var d);
                currentDate = d;
                continue;
            }

            // Numstat line: added\tremoved\tpath
            var tabs = line.Split('\t');
            if (tabs.Length != 3) continue;

            if (!int.TryParse(tabs[0], out var added)) continue; // binary file
            int.TryParse(tabs[1], out var removed);
            var path = tabs[2].Trim().Replace('\\', '/');

            if (!result.TryGetValue(path, out var data))
            {
                data = new ChurnData();
                result[path] = data;
            }

            data.Changes++;
            data.LinesAdded += added;
            data.LinesRemoved += removed;
            if (currentDate > data.LastChangeAt)
                data.LastChangeAt = currentDate;

            if (currentAuthor is not null)
            {
                if (!fileAuthors.TryGetValue(path, out var authors))
                {
                    authors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    fileAuthors[path] = authors;
                }
                authors.Add(currentAuthor);
            }
        }

        foreach (var (path, authors) in fileAuthors)
        {
            if (result.TryGetValue(path, out var data))
                data.AuthorCount = authors.Count;
        }

        logger.LogDebug("Churn: {Count} files with changes in last {Days} days", result.Count, days);
        return result;
    }

    internal async Task<Dictionary<string, CouplingData>> ComputeCouplingAsync(
        string repoPath, int days = 180, CancellationToken ct = default)
    {
        var result = new Dictionary<string, CouplingData>(StringComparer.OrdinalIgnoreCase);

        var output = await RunGitAsync(repoPath,
            $"log --name-only --format=%aI --no-merges --since={days}.days.ago", ct);

        if (string.IsNullOrWhiteSpace(output)) return result;

        // Group files by calendar day
        var dailyFileSets = new Dictionary<string, HashSet<string>>();
        string? currentDay = null;
        HashSet<string>? currentFiles = null;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Date line (ISO 8601)
            if (trimmed.Length >= 10 && trimmed[4] == '-' && trimmed[7] == '-')
            {
                var day = trimmed[..10];
                if (!dailyFileSets.TryGetValue(day, out currentFiles))
                {
                    currentFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    dailyFileSets[day] = currentFiles;
                }
                currentDay = day;
                continue;
            }

            // File path
            currentFiles?.Add(trimmed.Replace('\\', '/'));
        }

        // Compute pairwise co-change counts
        var pairCounts = new Dictionary<(string, string), int>();

        foreach (var files in dailyFileSets.Values)
        {
            var fileList = files.Where(f => SourceExtensions.Contains(Path.GetExtension(f)))
                .Take(50) // Cap to avoid combinatorial explosion
                .OrderBy(f => f)
                .ToList();

            for (int i = 0; i < fileList.Count; i++)
            {
                for (int j = i + 1; j < fileList.Count; j++)
                {
                    var key = (fileList[i], fileList[j]);
                    pairCounts.TryGetValue(key, out var count);
                    pairCounts[key] = count + 1;
                }
            }
        }

        // Convert to per-file coupling data
        var filePartners = new Dictionary<string, List<(string Partner, int Count)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var ((a, b), count) in pairCounts)
        {
            if (!filePartners.TryGetValue(a, out var listA))
            {
                listA = [];
                filePartners[a] = listA;
            }
            listA.Add((b, count));

            if (!filePartners.TryGetValue(b, out var listB))
            {
                listB = [];
                filePartners[b] = listB;
            }
            listB.Add((a, count));
        }

        foreach (var (file, partners) in filePartners)
        {
            var maxStrength = 0.0;
            if (partners.Count > 0)
            {
                var maxCount = partners.Max(p => p.Count);
                // Normalize: strength = co-change-count / total-days-with-changes
                var totalDaysWithChanges = dailyFileSets.Values.Count(d => d.Contains(file));
                maxStrength = totalDaysWithChanges > 0 ? (double)maxCount / totalDaysWithChanges : 0;
                maxStrength = Math.Min(maxStrength, 1.0);
            }

            result[file] = new CouplingData
            {
                MaxCouplingStrength = maxStrength,
                CouplingPartners = partners.Select(p => p.Partner).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            };
        }

        logger.LogDebug("Coupling: {Count} files with co-change data", result.Count);
        return result;
    }

    internal async Task<Dictionary<string, KnowledgeData>> ComputeKnowledgeRiskAsync(
        string repoPath, int years = 2, CancellationToken ct = default)
    {
        var result = new Dictionary<string, KnowledgeData>(StringComparer.OrdinalIgnoreCase);

        var output = await RunGitAsync(repoPath,
            $"log --format=%aN%x00%H --no-merges --no-renames --name-only --since={years}.years.ago", ct);

        if (string.IsNullOrWhiteSpace(output)) return result;

        // Count commits per author per file
        var fileAuthorCommits = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        string? currentAuthor = null;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var parts = trimmed.Split('\0');
            if (parts.Length == 2)
            {
                currentAuthor = parts[0];
                continue;
            }

            if (currentAuthor is null) continue;

            var path = trimmed.Replace('\\', '/');
            if (!fileAuthorCommits.TryGetValue(path, out var authors))
            {
                authors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                fileAuthorCommits[path] = authors;
            }

            authors.TryGetValue(currentAuthor, out var count);
            authors[currentAuthor] = count + 1;
        }

        foreach (var (file, authors) in fileAuthorCommits)
        {
            var totalCommits = authors.Values.Sum();
            var sortedAuthors = authors.OrderByDescending(a => a.Value).ToList();

            // Truck factor: minimum authors covering > 50% of commits
            var truckFactor = 0;
            var cumulative = 0;
            foreach (var (_, commits) in sortedAuthors)
            {
                cumulative += commits;
                truckFactor++;
                if (cumulative > totalCommits * 0.5) break;
            }

            result[file] = new KnowledgeData
            {
                TruckFactor = truckFactor,
                TopAuthors = sortedAuthors.Take(5)
                    .Select(a => new AuthorCommits { Name = a.Key, Commits = a.Value })
                    .ToList()
            };
        }

        logger.LogDebug("Knowledge: {Count} files with author data", result.Count);
        return result;
    }

    // --- Complexity Analysis ---

    internal Dictionary<string, ComplexityData> ComputeComplexityBatch(
        string repoPath, IReadOnlyList<string> filePaths)
    {
        var result = new Dictionary<string, ComplexityData>(StringComparer.OrdinalIgnoreCase);

        foreach (var relPath in filePaths)
        {
            var ext = Path.GetExtension(relPath);
            if (!SourceExtensions.Contains(ext)) continue;

            var fullPath = Path.Combine(repoPath, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!fileSystem.FileExists(fullPath)) continue;

            try
            {
                string content;
                try { content = fileSystem.ReadAllText(fullPath); }
                catch { continue; }

                var data = ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                    ? ComputeCSharpComplexity(content)
                    : ComputeIndentationComplexity(content);

                if (data.MaxNestingDepth >= 2)
                    result[relPath] = data;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Complexity analysis failed for {File}", relPath);
            }
        }

        logger.LogDebug("Complexity: {Count} files analyzed", result.Count);
        return result;
    }

    /// <summary>
    /// C# complexity via brace-counting heuristic. Avoids pulling Roslyn into Services
    /// (Roslyn is isolated in Extractors.CSharp). Accuracy is sufficient for health scoring.
    /// </summary>
    private static ComplexityData ComputeCSharpComplexity(string source)
    {
        var lines = source.Split('\n');
        int maxDepth = 0;
        int deepLines = 0;
        int functionCount = 0;
        int longestFunction = 0;
        int branchCount = 0;
        int braceDepth = 0;
        int currentFunctionLength = 0;
        bool inFunction = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Count braces for depth tracking
            foreach (var c in trimmed)
            {
                if (c == '{') braceDepth++;
                else if (c == '}') braceDepth--;
            }

            var effectiveDepth = Math.Max(0, braceDepth);
            if (effectiveDepth > maxDepth) maxDepth = effectiveDepth;
            if (effectiveDepth >= 3) deepLines++;

            // Detect method/constructor/property declarations (brace depth 2 = inside class)
            if (braceDepth == 2 && trimmed.Contains('(') && !trimmed.StartsWith("//")
                && (trimmed.Contains("void ") || trimmed.Contains("async ") ||
                    trimmed.Contains("Task") || trimmed.Contains("string ") ||
                    trimmed.Contains("int ") || trimmed.Contains("bool ") ||
                    trimmed.Contains("public ") || trimmed.Contains("private ") ||
                    trimmed.Contains("protected ") || trimmed.Contains("internal ")))
            {
                if (inFunction && currentFunctionLength > longestFunction)
                    longestFunction = currentFunctionLength;

                functionCount++;
                inFunction = true;
                currentFunctionLength = 1;
            }
            else if (inFunction)
            {
                currentFunctionLength++;
            }

            // Branch counting
            if (trimmed.StartsWith("if ") || trimmed.StartsWith("if(") ||
                trimmed.StartsWith("switch ") || trimmed.StartsWith("switch("))
                branchCount++;
        }

        if (inFunction && currentFunctionLength > longestFunction)
            longestFunction = currentFunctionLength;

        var score = ComputeComplexityScore(maxDepth, deepLines, longestFunction,
            lines.Length, branchCount, functionCount);

        return new ComplexityData
        {
            Score = score,
            MaxNestingDepth = maxDepth,
            DeepNestingLines = deepLines,
            FunctionCount = functionCount,
            LongestFunction = longestFunction
        };
    }

    private static ComplexityData ComputeIndentationComplexity(string source)
    {
        var lines = source.Split('\n');
        var indentUnit = DetectIndentUnit(lines);
        if (indentUnit <= 0) indentUnit = 4;

        int maxDepth = 0;
        int deepLines = 0;
        int functionCount = 0;
        int currentFunctionLength = 0;
        int longestFunction = 0;
        bool inFunction = false;
        int prevDepth = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var indent = 0;
            foreach (var c in line)
            {
                if (c == ' ') indent++;
                else if (c == '\t') indent += indentUnit;
                else break;
            }

            var depth = indent / indentUnit;
            if (depth > maxDepth) maxDepth = depth;
            if (depth >= 3) deepLines++;

            // Estimate function boundaries from indentation transitions
            if (depth == 1 && prevDepth <= 0)
            {
                if (inFunction && currentFunctionLength > longestFunction)
                    longestFunction = currentFunctionLength;

                functionCount++;
                inFunction = true;
                currentFunctionLength = 1;
            }
            else if (inFunction)
            {
                currentFunctionLength++;
            }

            prevDepth = depth;
        }

        if (inFunction && currentFunctionLength > longestFunction)
            longestFunction = currentFunctionLength;

        var score = ComputeComplexityScore(maxDepth, deepLines, longestFunction,
            lines.Length, 0, functionCount);

        return new ComplexityData
        {
            Score = score,
            MaxNestingDepth = maxDepth,
            DeepNestingLines = deepLines,
            FunctionCount = functionCount,
            LongestFunction = longestFunction
        };
    }

    private static int DetectIndentUnit(string[] lines)
    {
        var indents = new Dictionary<int, int>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line[0] == '\t') return 4; // tab-indented

            var spaces = 0;
            foreach (var c in line)
            {
                if (c == ' ') spaces++;
                else break;
            }

            if (spaces > 0 && spaces <= 8)
            {
                indents.TryGetValue(spaces, out var count);
                indents[spaces] = count + 1;
            }
        }

        if (indents.Count == 0) return 4;
        return indents.OrderByDescending(kv => kv.Value).First().Key;
    }

    // --- Scoring ---

    private static int ComputeComplexityScore(int maxNesting, int deepLines,
        int longestFunction, int fileLength, int branchCount, int functionCount)
    {
        if (maxNesting <= 2) return 0; // Config/data files

        // Nesting score (35%)
        var nestingScore = Math.Min(maxNesting / 8.0, 1.0) * 35;

        // Deep ratio (25%)
        var deepRatio = fileLength > 0 ? (double)deepLines / fileLength : 0;
        var deepScore = Math.Min(deepRatio / 0.3, 1.0) * 25;

        // Longest function (20%)
        var funcScore = Math.Min(longestFunction / 200.0, 1.0) * 20;

        // File length (10%)
        var lengthScore = Math.Min(fileLength / 1000.0, 1.0) * 10;

        // Branch density (10%)
        var branchDensity = functionCount > 0 ? (double)branchCount / functionCount : 0;
        var branchScore = Math.Min(branchDensity / 5.0, 1.0) * 10;

        return (int)Math.Round(nestingScore + deepScore + funcScore + lengthScore + branchScore);
    }

    private static double ComputeHealthScore(ChurnData? churn, CouplingData? coupling,
        KnowledgeData? knowledge, ComplexityData? complexity, LintResult? lint = null)
    {
        var complexitySubscore = ComplexitySubscore(complexity?.Score ?? 0);
        var churnSubscore = ChurnSubscore(churn?.Changes ?? 0);
        var couplingSubscore = CouplingSubscore(coupling?.MaxCouplingStrength ?? 0, coupling?.CouplingPartners ?? 0);
        var knowledgeSubscore = KnowledgeSubscore(knowledge?.TruckFactor ?? 0);

        double health;
        if (lint is { ErrorCount: > 0 } or { WarningCount: > 0 })
        {
            // When ESLint data is available, give it 15% weight (borrowed from complexity + churn)
            var lintSubscore = EslintSubscore(lint.ErrorCount, lint.WarningCount);
            health = 0.25 * complexitySubscore
                   + 0.25 * churnSubscore
                   + 0.20 * couplingSubscore
                   + 0.15 * knowledgeSubscore
                   + 0.15 * lintSubscore;
        }
        else
        {
            // No ESLint data — original weights (C#-only repos, or ESLint not configured)
            health = 0.30 * complexitySubscore
                   + 0.30 * churnSubscore
                   + 0.20 * couplingSubscore
                   + 0.20 * knowledgeSubscore;
        }

        return Math.Round(Math.Clamp(health, 1.0, 10.0), 1);
    }

    private static double ComplexitySubscore(int score)
    {
        // 0 → 10, 100 → 1
        return score <= 0 ? 10.0 : Math.Max(1.0, 10.0 - score * 0.09);
    }

    private static double ChurnSubscore(int changes)
    {
        // 0 changes → 10, 50+ → 1
        return changes <= 0 ? 10.0 : Math.Max(1.0, 10.0 - changes * 0.18);
    }

    private static double CouplingSubscore(double strength, int partners)
    {
        // Low coupling → 10, high → 1
        var strengthPenalty = strength * 5.0;
        var partnerPenalty = Math.Min(partners / 10.0, 4.0);
        return Math.Max(1.0, 10.0 - strengthPenalty - partnerPenalty);
    }

    /// <summary>
    /// Computes a 0.0–1.0 trust score for a file, inspired by moe-kb's trust rating system.
    /// Combines stability (low churn = high trust) and lint quality (few errors = high trust).
    /// Baseline 0.7 (neutral). Files with no lint data get stability-only scoring.
    /// </summary>
    private static double ComputeTrustScore(ChurnData? churn, LintResult? lint, bool doNotTrust = false)
    {
        // do_not_trust flag forces very low trust (same as moe-kb's do_not_model)
        if (doNotTrust)
            return 0.1;

        var score = 0.7; // Neutral baseline

        // Stability signal (from moe-kb: stable files are more trustworthy)
        var daysSinceChange = churn?.LastChangeAt is not null
            ? (DateTime.UtcNow - churn.LastChangeAt.Value).TotalDays
            : 90.0; // default to "reasonably stable"

        if (daysSinceChange >= 90) score += 0.15;       // Very stable
        else if (daysSinceChange >= 30) score += 0.05;   // Moderately stable

        // Churn penalty (high churn = less trustworthy)
        var commitCount = churn?.Changes ?? 0;
        score -= Math.Min(commitCount / 50.0, 0.4);

        // Recent change penalty
        if (daysSinceChange <= 7) score -= 0.05;

        // Lint signal (errors/warnings from ESLint or Roslyn)
        if (lint is not null)
        {
            score -= Math.Min(lint.ErrorCount * 0.05, 0.3);
            score -= Math.Min(lint.WarningCount * 0.02, 0.1);
        }

        return Math.Round(Math.Clamp(score, 0.0, 1.0), 2);
    }

    private static double EslintSubscore(int errors, int warnings)
    {
        // 0 errors/warnings → 10, 6+ errors → 1
        var errorPenalty = Math.Min(errors * 1.5, 9.0);
        var warningPenalty = Math.Min(warnings * 0.4, 3.0);
        return Math.Max(1.0, 10.0 - errorPenalty - warningPenalty);
    }

    private static double KnowledgeSubscore(int truckFactor)
    {
        // truck_factor 0 (no data) → 7 (neutral), 1 → 2, 2 → 5, 3+ → 8-10
        return truckFactor switch
        {
            0 => 7.0,
            1 => 2.0,
            2 => 5.0,
            3 => 8.0,
            _ => Math.Min(10.0, 8.0 + (truckFactor - 3) * 0.5)
        };
    }

    private static double ComputeRiskScore(double health, int changes, string role, int couplingPartners)
    {
        var roleWeight = role == "test" ? 0.3 : 1.0;
        var centralityBoost = 1.0 + couplingPartners * 0.2;
        return Math.Round((10 - health) * changes * roleWeight * centralityBoost, 1);
    }

    private static string ClassifyRole(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Any(p => TestPathSegments.Contains(p)) ? "test" : "core";
    }

    // --- Health Summary Aggregation ---

    private async Task ComputeAndStoreHealthSummaries(
        string projectName, List<FileMetricsEntity> metrics)
    {
        var now = DateTime.UtcNow;

        // Group by DotnetProject
        var groups = metrics
            .GroupBy(m => m.DotnetProject ?? "")
            .ToList();

        foreach (var group in groups)
        {
            var files = group.ToList();
            var summary = AggregateHealth(projectName,
                group.Key == "" ? null : group.Key, files, now);
            await store.UpsertProjectHealthSummaryAsync(summary);
        }

        // Repo-level aggregate (DotnetProject = null)
        if (groups.Count > 1 || (groups.Count == 1 && groups[0].Key != ""))
        {
            var repoSummary = AggregateHealth(projectName, null, metrics, now);
            await store.UpsertProjectHealthSummaryAsync(repoSummary);
        }
    }

    private static ProjectHealthSummaryEntity AggregateHealth(
        string project, string? dotnetProject,
        IReadOnlyList<FileMetricsEntity> files, DateTime now)
    {
        if (files.Count == 0)
        {
            return new ProjectHealthSummaryEntity
            {
                Project = project,
                DotnetProject = dotnetProject,
                OverallHealth = 5.0,
                ComputedAt = now
            };
        }

        // Weighted average: unhealthy files weighted more
        double totalWeight = 0;
        double weightedSum = 0;
        foreach (var f in files)
        {
            var weight = 11 - f.HealthScore;
            weightedSum += f.HealthScore * weight;
            totalWeight += weight;
        }

        var overallHealth = totalWeight > 0
            ? Math.Round(weightedSum / totalWeight, 1)
            : 5.0;

        var hotspots = files.Count(f => f.HealthScore < 4.0);
        var alerts = files.Count(f => f.HealthScore < 2.5);

        var topHotspots = files
            .OrderByDescending(f => f.RiskScore)
            .Take(10)
            .Select(f => new { file = f.FilePath, health = f.HealthScore, risk = f.RiskScore })
            .ToList();

        return new ProjectHealthSummaryEntity
        {
            Project = project,
            DotnetProject = dotnetProject,
            OverallHealth = overallHealth,
            TotalFiles = files.Count,
            HotspotCount = hotspots,
            AlertCount = alerts,
            TopHotspots = topHotspots.Count > 0
                ? JsonSerializer.Serialize(topHotspots)
                : null,
            ComputedAt = now
        };
    }

    // --- Helpers ---

    private List<string> GetSourceFiles(string repoPath)
    {
        var result = new List<string>();
        try
        {
            foreach (var file in fileSystem.EnumerateFiles(repoPath, "*", SearchOption.AllDirectories))
            {
                var rel = fileSystem.GetRelativePath(repoPath, file);
                if (rel.Contains("/bin/") || rel.Contains("/obj/") ||
                    rel.Contains("/node_modules/") || rel.StartsWith(".git/"))
                    continue;

                if (SourceExtensions.Contains(Path.GetExtension(rel)))
                    result.Add(rel);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to enumerate source files in {RepoPath}", repoPath);
        }
        return result;
    }

    private async Task<string> RunGitAsync(string repoPath, string arguments,
        CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var proc = Process.Start(psi);
            if (proc is null) return "";

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                var error = await proc.StandardError.ReadToEndAsync(ct);
                logger.LogDebug("git {Args} failed (exit {Code}): {Error}",
                    arguments.Split(' ')[0], proc.ExitCode, error);
            }

            return output;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "git {Args} failed", arguments.Split(' ')[0]);
            return "";
        }
    }

    // --- Data Types ---

    internal class ChurnData
    {
        public int Changes { get; set; }
        public int LinesAdded { get; set; }
        public int LinesRemoved { get; set; }
        public int AuthorCount { get; set; }
        public DateTime? LastChangeAt { get; set; }
    }

    internal class CouplingData
    {
        public double MaxCouplingStrength { get; set; }
        public int CouplingPartners { get; set; }
    }

    internal class KnowledgeData
    {
        public int TruckFactor { get; set; }
        public List<AuthorCommits>? TopAuthors { get; set; }
    }

    internal class AuthorCommits
    {
        public string Name { get; set; } = "";
        public int Commits { get; set; }
    }

    internal class ComplexityData
    {
        public int Score { get; set; }
        public int MaxNestingDepth { get; set; }
        public int DeepNestingLines { get; set; }
        public int FunctionCount { get; set; }
        public int LongestFunction { get; set; }
    }
}
