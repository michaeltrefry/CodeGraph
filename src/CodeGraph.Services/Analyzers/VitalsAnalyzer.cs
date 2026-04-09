using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeGraph.Data;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Services.Analyzers;

public class VitalsAnalyzer(
    IGraphStore store,
    IAnalysisProviderRegistry providerRegistry,
    IOptions<AnalysisOptions> analysisOptionsAccessor,
    IFileSystem fileSystem,
    ILintRunner lintRunner,
    LintResultCache lintCache,
    DiagnosticDetailCache diagnosticDetailCache,
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

    private static readonly Regex BugFixKeywordPattern = new(@"\b(fix|bug|broken|defect|patch)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FirefightingKeywordPattern = new(@"\b(hotfix|incident|urgent|emergency|rollback|revert)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
        var historyTask = ReadHistorySnapshotAsync(repoPath, ct);
        var couplingTask = ComputeCouplingAsync(repoPath, ct: ct);
        var knowledgeTask = ComputeKnowledgeRiskAsync(repoPath, ct: ct);

        // Only lint if the repo has TS/JS files
        var hasLintableFiles = filePaths.Any(f =>
            f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase));
        var hasRoslynLintResults = lintCache.HasResults(projectName);
        var lintTask = hasLintableFiles || hasRoslynLintResults
            ? lintRunner.LintProjectAsync(repoPath, ct)
            : Task.FromResult<IReadOnlyDictionary<string, LintResult>>(
                new Dictionary<string, LintResult>());

        await Task.WhenAll(historyTask, couplingTask, knowledgeTask, lintTask);

        var history = await historyTask;
        var coupling = await couplingTask;
        var knowledge = await knowledgeTask;
        var lint = await lintTask;
        var diagnostics = diagnosticDetailCache.Take(projectName);

        if (lint.Count > 0)
            logger.LogInformation("Lint found issues in {Count} files for {Project}",
                lint.Count, projectName);

        await store.DeleteProjectDiagnosticsAsync(projectName);
        if (diagnostics.Count > 0)
        {
            await store.UpsertProjectDiagnosticsBatchAsync(projectName, diagnostics);
            logger.LogInformation("Persisted {Count} diagnostics for {Project}",
                diagnostics.Count, projectName);
        }

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

            history.FileMetricsByPath.TryGetValue(relPath, out var fileHistory);
            coupling.TryGetValue(relPath, out var co);
            knowledge.TryGetValue(relPath, out var kn);
            complexity.TryGetValue(relPath, out var cx);
            lint.TryGetValue(relPath, out var ln);

            var ch = fileHistory?.ToChurnData();
            var role = ClassifyRole(relPath);
            var health = ComputeHealthScore(ch, co, kn, cx, ln);
            var changes = ch?.Changes ?? 0;
            var couplingPartners = co?.CouplingPartners ?? 0;
            var risk = ComputeRiskScore(health, changes, role, couplingPartners);
            var recurringChurnScore = ComputeRecurringChurnScore(
                fileHistory?.WeightedChurn30d ?? 0,
                fileHistory?.WeightedChurn90d ?? 0,
                fileHistory?.WeightedChurn365d ?? 0);
            var concernScore = ComputeConcernScore(
                health,
                risk,
                role,
                recurringChurnScore,
                fileHistory?.WeightedBugFixCommits365d ?? 0,
                fileHistory?.WeightedTouches365d ?? 0);
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
                ConcernScore = concernScore,
                Churn30d = fileHistory?.WeightedChurn30d ?? 0,
                Churn90d = fileHistory?.WeightedChurn90d ?? 0,
                Churn365d = fileHistory?.WeightedChurn365d ?? 0,
                BugFixCommits90d = fileHistory?.WeightedBugFixCommits90d ?? 0,
                BugFixCommits365d = fileHistory?.WeightedBugFixCommits365d ?? 0,
                BugFixRatio365d = ComputeBugFixRatio(
                    fileHistory?.WeightedBugFixCommits365d ?? 0,
                    fileHistory?.WeightedTouches365d ?? 0),
                BugFixWeightedTouches365d = fileHistory?.WeightedTouches365d ?? 0,
                RecurringChurnScore = recurringChurnScore,
                ComputedAt = now
            });
        }

        // 5. Delete old metrics and upsert new
        await store.DeleteFileMetricsAsync(projectName);
        await store.UpsertFileMetricsBatchAsync(projectName, metrics);

        // 6. Compute and store project health summaries
        await ComputeAndStoreHealthSummaries(projectName, metrics, history);

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
                var response = await CallAnalysisAsync(prompt, ct);
                if (response is not null && !string.IsNullOrWhiteSpace(response.Text))
                {
                    await store.UpsertProjectHealthAnalysisAsync(new ProjectHealthAnalysisEntity
                    {
                        Project = projectName,
                        DotnetProject = summary.DotnetProject,
                        Analysis = response.Text,
                        Confidence = summary.OverallHealth < 4.0 ? "high" : "medium",
                        ModelUsed = response.ModelUsed,
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
                var response = await CallAnalysisAsync(prompt, ct);
                if (response is not null && !string.IsNullOrWhiteSpace(response.Text))
                {
                    await store.UpsertProjectHealthAnalysisAsync(new ProjectHealthAnalysisEntity
                    {
                        Project = projectName,
                        DotnetProject = null,
                        Analysis = response.Text,
                        Confidence = repoSummary.OverallHealth < 4.0 ? "high" : "medium",
                        ModelUsed = response.ModelUsed,
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
        sb.AppendLine($"You are a solo-maintenance code health analyst. Analyze the following metrics for project '{dotnetProject}' in repository '{repoName}'.");
        sb.AppendLine();
        sb.AppendLine($"Overall health: {summary.OverallHealth:F1}/10");
        sb.AppendLine($"Total files: {summary.TotalFiles}");
        sb.AppendLine($"Hotspots (health < 4.0): {summary.HotspotCount}");
        sb.AppendLine($"Alerts (health < 2.5): {summary.AlertCount}");
        AppendSecurityContext(sb, securitySummary);
        sb.AppendLine();

        var hotspots = metrics
            .OrderByDescending(m => m.ConcernScore)
            .ThenBy(m => m.HealthScore)
            .Take(20)
            .ToList();
        if (hotspots.Count > 0)
        {
            sb.AppendLine("File-level metrics (highest concern first):");
            sb.AppendLine("| File | Concern | Health | Bug/Fix | Recurring Churn | Churn | Complexity | Coupling | Role |");
            sb.AppendLine("|------|---------|--------|---------|-----------------|-------|------------|----------|------|");
            foreach (var m in hotspots)
            {
                sb.AppendLine($"| {m.FilePath} | {m.ConcernScore:F1} | {m.HealthScore:F1} | {m.BugFixCommits365d:F2} fixes ({m.BugFixRatio365d:P0}) | {m.RecurringChurnScore:F2} | {m.Changes} recent / {m.Churn365d:F2} yearly | {m.ComplexityScore}/100 (depth {m.MaxNestingDepth}) | {m.MaxCouplingStrength:F2} ({m.CouplingPartners} partners) | {m.Role} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("""
            Provide a concise health analysis covering:
            1. **Repeated Maintenance Drag**: Which files look like repeated fix areas or persistent work friction, and why?
            2. **Remediation Priorities**: Top 3 actions ranked by impact.
            3. **Persistent Churn**: Which files keep being revisited across time windows?
            4. **Coupling Concerns**: Files with high coupling that may cause cascading issues.
            5. **Security**: Any security concerns from the security scan (if data available).

            Optimize for solo maintenance pain, not team-dynamics language.
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
        sb.AppendLine($"You are a solo-maintenance code health analyst. Synthesize the health status of repository '{repoName}'.");
        sb.AppendLine();
        sb.AppendLine($"Repository health: {repoSummary.OverallHealth:F1}/10");
        sb.AppendLine($"Total files: {repoSummary.TotalFiles}");
        sb.AppendLine($"Hotspots: {repoSummary.HotspotCount}");
        sb.AppendLine($"Alerts: {repoSummary.AlertCount}");
        if (!string.IsNullOrWhiteSpace(repoSummary.HistoryMaturity))
            sb.AppendLine($"History maturity: {repoSummary.HistoryMaturity}");
        if (repoSummary.HasSufficientHistoryForTrends)
        {
            if (!string.IsNullOrWhiteSpace(repoSummary.ActivityStatus))
                sb.AppendLine($"Activity status: {repoSummary.ActivityStatus}");
            if (!string.IsNullOrWhiteSpace(repoSummary.FirefightingStatus))
                sb.AppendLine($"Firefighting status: {repoSummary.FirefightingStatus}");
            sb.AppendLine($"Velocity: {repoSummary.VelocityLast6Months} commits in last 6 months vs {repoSummary.VelocityPrior6Months} in prior 6 months ({repoSummary.VelocityChangePercent:+0.0;-0.0;0.0}%)");
            sb.AppendLine($"Dormant months (12m): {repoSummary.DormantMonths12m}, max inactive streak: {repoSummary.MaxInactiveStreakMonths}");
        }
        else if (!string.IsNullOrWhiteSpace(repoSummary.HistoryMaturity))
        {
            sb.AppendLine("Trend signals are immature due to limited repo history.");
        }
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

        var topHotspots = allMetrics
            .OrderByDescending(m => m.ConcernScore)
            .ThenBy(m => m.HealthScore)
            .Take(10)
            .ToList();
        if (topHotspots.Count > 0)
        {
            sb.AppendLine("Top 10 maintenance hotspots across all projects:");
            foreach (var m in topHotspots)
                sb.AppendLine($"- {m.FilePath} (concern {m.ConcernScore:F1}, health {m.HealthScore:F1}, fixes {m.BugFixCommits365d:F2}, recurring churn {m.RecurringChurnScore:F2})");
            sb.AppendLine();
        }

        sb.AppendLine("""
            Provide a repository-level health summary covering:
            1. **Overall Assessment**: What is creating the most solo maintenance drag in this repo?
            2. **Worst Projects**: Which projects need attention and why?
            3. **Top Remediation Actions**: 3-5 prioritized recommendations.
            4. **Vitality Context**: Is the repo stable, slowing, dormant, revived, or too young for trend confidence?
            5. **Security**: Highlight any security findings if present (secrets, vulnerable packages, attack surface).

            Emphasize repeated fix areas, persistent work friction, and maintenance hotspots.
            Avoid team-dynamics framing. Be specific and actionable. Keep it under 400 words.
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

    private async Task<AnalysisTextResponse?> CallAnalysisAsync(string prompt, CancellationToken ct)
    {
        var provider = providerRegistry.GetProvider();
        var response = await provider.ExecuteAsync(
            new AnalysisPrompt(
                SystemPrompt: "You are a software architecture and code health analyst. " +
                              "Return concise, practical prose grounded in the provided metrics.",
                UserPrompt: prompt),
            new AnalysisRequestOptions(
                Model: null,
                MaxTokens: 1024),
            ct);

        return string.IsNullOrWhiteSpace(response.Text) ? null : response;
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

    internal async Task<HistorySnapshot> ReadHistorySnapshotAsync(
        string repoPath,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var trailingMonths = 12;
        var historyOutputTask = RunGitAsync(
            repoPath,
            "log --numstat --format=%H%x00%aI%x00%aN%x00%s --no-merges --no-renames --date-order --since=400.days.ago",
            ct);
        var totalCommitCountTask = RunGitAsync(repoPath, "rev-list --count --no-merges HEAD", ct);
        var firstCommitTask = RunGitAsync(repoPath, "log --reverse --format=%aI --no-merges --max-count=1", ct);

        await Task.WhenAll(historyOutputTask, totalCommitCountTask, firstCommitTask);

        var snapshot = new HistorySnapshot();
        var historyOutput = await historyOutputTask;
        var totalCommitCountOutput = await totalCommitCountTask;
        var firstCommitOutput = await firstCommitTask;

        snapshot.TotalCommitCount = int.TryParse(totalCommitCountOutput.Trim(), out var totalCommitCount)
            ? totalCommitCount
            : 0;
        snapshot.FirstCommitAt = DateTime.TryParse(firstCommitOutput.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var firstCommitAt)
            ? firstCommitAt
            : null;

        if (!string.IsNullOrWhiteSpace(historyOutput))
        {
            PendingCommitRecord? currentCommit = null;

            void FlushPendingCommit()
            {
                if (currentCommit is null)
                    return;

                var sourceTouches = currentCommit.FileTouches
                    .Where(t => SourceExtensions.Contains(Path.GetExtension(t.Path)))
                    .GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Last())
                    .ToList();

                if (sourceTouches.Count == 0)
                {
                    currentCommit = null;
                    return;
                }

                var ageDays = (now - currentCommit.AuthorDate).TotalDays;
                var isBugFix = IsBugFixCommit(currentCommit.Message);
                var isFirefighting = IsFirefightingCommit(currentCommit.Message);
                var totalChangedLines = sourceTouches.Sum(t => Math.Max(0, t.LinesAdded) + Math.Max(0, t.LinesRemoved));
                var fallbackWeight = 1.0 / sourceTouches.Count;

                for (var i = 0; i < sourceTouches.Count; i++)
                {
                    var touch = sourceTouches[i];
                    var changedLines = Math.Max(0, touch.LinesAdded) + Math.Max(0, touch.LinesRemoved);
                    var weight = totalChangedLines > 0
                        ? changedLines / (double)totalChangedLines
                        : fallbackWeight;
                    if (!snapshot.FileMetricsByPath.TryGetValue(touch.Path, out var data))
                    {
                        data = new FileHistoryData();
                        snapshot.FileMetricsByPath[touch.Path] = data;
                    }

                    if (ageDays <= 365)
                    {
                        data.WeightedTouches365d += weight;
                        data.WeightedChurn365d += weight;
                        if (isBugFix)
                            data.WeightedBugFixCommits365d += weight;
                    }

                    if (ageDays <= 90)
                    {
                        data.Changes90d++;
                        data.LinesAdded90d += touch.LinesAdded;
                        data.LinesRemoved90d += touch.LinesRemoved;
                        data.Authors90d.Add(currentCommit.Author);
                        data.LastChangeAt = data.LastChangeAt is null || currentCommit.AuthorDate > data.LastChangeAt
                            ? currentCommit.AuthorDate
                            : data.LastChangeAt;
                        data.WeightedChurn90d += weight;
                        if (isBugFix)
                            data.WeightedBugFixCommits90d += weight;
                    }

                    if (ageDays <= 30)
                        data.WeightedChurn30d += weight;
                }

                var monthStart = new DateTime(currentCommit.AuthorDate.Year, currentCommit.AuthorDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                snapshot.MonthlyCommitCounts.TryGetValue(monthStart, out var currentMonthCount);
                snapshot.MonthlyCommitCounts[monthStart] = currentMonthCount + 1;

                if (ageDays <= 365)
                {
                    snapshot.SourceCommits365d++;
                    if (isFirefighting)
                        snapshot.FirefightingCommits365d++;
                }

                if (ageDays <= 90)
                {
                    snapshot.SourceCommits90d++;
                    if (isFirefighting)
                        snapshot.FirefightingCommits90d++;
                }

                currentCommit = null;
            }

            foreach (var rawLine in historyOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = rawLine.Split('\0');
                if (parts.Length == 4)
                {
                    FlushPendingCommit();

                    if (!DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var authorDate))
                        continue;

                    currentCommit = new PendingCommitRecord
                    {
                        AuthorDate = authorDate,
                        Author = parts[2].Trim(),
                        Message = parts[3].Trim()
                    };
                    continue;
                }

                if (currentCommit is null)
                    continue;

                var tabs = rawLine.Split('\t');
                if (tabs.Length != 3)
                    continue;

                if (!int.TryParse(tabs[0], out var added))
                    continue;

                _ = int.TryParse(tabs[1], out var removed);
                currentCommit.FileTouches.Add(new PendingFileTouch
                {
                    Path = tabs[2].Trim().Replace('\\', '/'),
                    LinesAdded = added,
                    LinesRemoved = removed
                });
            }

            FlushPendingCommit();
        }

        snapshot.HistoryMaturity = DetermineHistoryMaturity(snapshot.FirstCommitAt, snapshot.TotalCommitCount, now);
        snapshot.HasSufficientHistoryForTrends = snapshot.HistoryMaturity is not "Young";
        snapshot.TrailingMonthlyPoints = BuildTrailingMonthlyPoints(snapshot.MonthlyCommitCounts, now, trailingMonths);
        snapshot.MonthlyCommitCountsJson = JsonSerializer.Serialize(snapshot.TrailingMonthlyPoints);
        snapshot.VelocityLast6Months = snapshot.TrailingMonthlyPoints.TakeLast(6).Sum(p => p.CommitCount);
        snapshot.VelocityPrior6Months = snapshot.TrailingMonthlyPoints.Count > 6
            ? snapshot.TrailingMonthlyPoints.Take(Math.Max(0, snapshot.TrailingMonthlyPoints.Count - 6)).TakeLast(6).Sum(p => p.CommitCount)
            : 0;
        snapshot.VelocityChangePercent = snapshot.VelocityPrior6Months > 0
            ? Math.Round(((snapshot.VelocityLast6Months - snapshot.VelocityPrior6Months) / (double)snapshot.VelocityPrior6Months) * 100, 1)
            : snapshot.VelocityLast6Months > 0 ? 100 : 0;
        snapshot.DormantMonths12m = snapshot.TrailingMonthlyPoints.Count(p => p.CommitCount == 0);
        snapshot.MaxInactiveStreakMonths = ComputeMaxInactiveStreak(snapshot.TrailingMonthlyPoints);
        snapshot.ActivityStatus = DetermineActivityStatus(snapshot.TrailingMonthlyPoints, snapshot.HasSufficientHistoryForTrends, snapshot.VelocityLast6Months, snapshot.VelocityPrior6Months);
        snapshot.FirefightingRate90d = snapshot.SourceCommits90d > 0
            ? Math.Round(snapshot.FirefightingCommits90d / (double)snapshot.SourceCommits90d, 3)
            : 0;
        snapshot.FirefightingRate365d = snapshot.SourceCommits365d > 0
            ? Math.Round(snapshot.FirefightingCommits365d / (double)snapshot.SourceCommits365d, 3)
            : 0;
        snapshot.FirefightingStatus = DetermineFirefightingStatus(snapshot.FirefightingRate365d);

        logger.LogDebug("History snapshot: {FileCount} files, {CommitCount} commits, maturity {HistoryMaturity}",
            snapshot.FileMetricsByPath.Count, snapshot.TotalCommitCount, snapshot.HistoryMaturity ?? "unknown");

        return snapshot;
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

    private static double ComputeRecurringChurnScore(double churn30d, double churn90d, double churn365d)
    {
        var normalized30 = Math.Min(churn30d / 10.0, 1.0);
        var normalized90 = Math.Min(churn90d / 25.0, 1.0);
        var normalized365 = Math.Min(churn365d / 100.0, 1.0);
        return Math.Round((0.2 * normalized30) + (0.3 * normalized90) + (0.5 * normalized365), 2);
    }

    private static double ComputeConcernScore(
        double health,
        double riskScore,
        string role,
        double recurringChurnScore,
        double bugFixCommits365d,
        double weightedTouches365d)
    {
        var bugFixRatio365d = ComputeBugFixRatio(bugFixCommits365d, weightedTouches365d);
        var baseConcern = Math.Max(riskScore, (10 - health) * 2);
        var persistentChurnContribution = recurringChurnScore * 8;
        var bugFixContribution = Math.Min(bugFixRatio365d * 12, 8) + Math.Min(bugFixCommits365d, 4);
        var roleWeight = role == "test" ? 0.5 : 1.0;
        return Math.Round((baseConcern + persistentChurnContribution + bugFixContribution) * roleWeight, 1);
    }

    private static double ComputeBugFixRatio(double bugFixCommits365d, double weightedTouches365d)
    {
        if (weightedTouches365d <= 0)
            return 0;

        return Math.Round(bugFixCommits365d / weightedTouches365d, 3);
    }

    private async Task ComputeAndStoreHealthSummaries(
        string projectName, List<FileMetricsEntity> metrics, HistorySnapshot history)
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
        var repoSummary = AggregateHealth(projectName, null, metrics, now);
        repoSummary.HistoryMaturity = history.HistoryMaturity;
        repoSummary.HasSufficientHistoryForTrends = history.HasSufficientHistoryForTrends;
        repoSummary.ActivityStatus = history.ActivityStatus;
        repoSummary.FirefightingStatus = history.FirefightingStatus;
        repoSummary.MonthlyCommitCounts = history.MonthlyCommitCountsJson;
        repoSummary.VelocityLast6Months = history.VelocityLast6Months;
        repoSummary.VelocityPrior6Months = history.VelocityPrior6Months;
        repoSummary.VelocityChangePercent = history.VelocityChangePercent;
        repoSummary.DormantMonths12m = history.DormantMonths12m;
        repoSummary.MaxInactiveStreakMonths = history.MaxInactiveStreakMonths;
        repoSummary.FirefightingCommits90d = history.FirefightingCommits90d;
        repoSummary.FirefightingCommits365d = history.FirefightingCommits365d;
        repoSummary.FirefightingRate90d = history.FirefightingRate90d;
        repoSummary.FirefightingRate365d = history.FirefightingRate365d;
        await store.UpsertProjectHealthSummaryAsync(repoSummary);
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
            .OrderByDescending(f => f.ConcernScore)
            .ThenByDescending(f => f.RiskScore)
            .Take(10)
            .Select(f => new { file = f.FilePath, health = f.HealthScore, risk = f.RiskScore, concern = f.ConcernScore })
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

    private static string? DetermineHistoryMaturity(DateTime? firstCommitAt, int totalCommitCount, DateTime now)
    {
        if (firstCommitAt is null)
            return null;

        var ageDays = (now - firstCommitAt.Value).TotalDays;
        if (ageDays < 180 || totalCommitCount < 100)
            return "Young";
        if (ageDays < 365 || totalCommitCount < 300)
            return "Growing";
        return "Mature";
    }

    private static string? DetermineActivityStatus(
        IReadOnlyList<MonthlyCommitBucket> monthlyPoints,
        bool hasSufficientHistoryForTrends,
        int velocityLast6Months,
        int velocityPrior6Months)
    {
        if (!hasSufficientHistoryForTrends || monthlyPoints.Count == 0)
            return null;

        var recentZeroStreak = 0;
        for (var i = monthlyPoints.Count - 1; i >= 0; i--)
        {
            if (monthlyPoints[i].CommitCount != 0)
                break;

            recentZeroStreak++;
        }

        var dormantMonths = monthlyPoints.Count(p => p.CommitCount == 0);

        if (recentZeroStreak >= 3 && velocityLast6Months <= Math.Max(1, velocityPrior6Months / 4))
            return "PossiblyAbandoned";
        if (recentZeroStreak >= 2)
            return "Dormant";
        if (dormantMonths >= 2 && velocityPrior6Months > 0 && velocityLast6Months >= Math.Ceiling(velocityPrior6Months * 1.3))
            return "Revived";
        if (velocityPrior6Months > 0 && velocityLast6Months <= Math.Floor(velocityPrior6Months * 0.7))
            return "Slowing";

        var changePercent = velocityPrior6Months > 0
            ? Math.Abs(((velocityLast6Months - velocityPrior6Months) / (double)velocityPrior6Months) * 100)
            : 0;

        return changePercent <= 20 ? "Stable" : "Active";
    }

    private static string DetermineFirefightingStatus(double firefightingRate365d)
    {
        return firefightingRate365d switch
        {
            < 0.10 => "Low",
            < 0.25 => "Moderate",
            < 0.40 => "High",
            _ => "Critical"
        };
    }

    private static IReadOnlyList<MonthlyCommitBucket> BuildTrailingMonthlyPoints(
        IReadOnlyDictionary<DateTime, int> monthlyCommitCounts,
        DateTime now,
        int monthCount)
    {
        var currentMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var points = new List<MonthlyCommitBucket>(monthCount);

        for (var offset = monthCount - 1; offset >= 0; offset--)
        {
            var month = currentMonthStart.AddMonths(-offset);
            monthlyCommitCounts.TryGetValue(month, out var commitCount);
            points.Add(new MonthlyCommitBucket(month.ToString("yyyy-MM", CultureInfo.InvariantCulture), commitCount));
        }

        return points;
    }

    private static int ComputeMaxInactiveStreak(IReadOnlyList<MonthlyCommitBucket> monthlyPoints)
    {
        var maxStreak = 0;
        var currentStreak = 0;

        for (var i = 0; i < monthlyPoints.Count; i++)
        {
            if (monthlyPoints[i].CommitCount == 0)
            {
                currentStreak++;
                maxStreak = Math.Max(maxStreak, currentStreak);
            }
            else
            {
                currentStreak = 0;
            }
        }

        return maxStreak;
    }

    private static bool IsBugFixCommit(string? message) =>
        !string.IsNullOrWhiteSpace(message) && BugFixKeywordPattern.IsMatch(message);

    private static bool IsFirefightingCommit(string? message) =>
        !string.IsNullOrWhiteSpace(message) && FirefightingKeywordPattern.IsMatch(message);

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

    internal sealed class FileHistoryData
    {
        public int Changes90d { get; set; }
        public int LinesAdded90d { get; set; }
        public int LinesRemoved90d { get; set; }
        public HashSet<string> Authors90d { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime? LastChangeAt { get; set; }
        public double WeightedTouches365d { get; set; }
        public double WeightedChurn30d { get; set; }
        public double WeightedChurn90d { get; set; }
        public double WeightedChurn365d { get; set; }
        public double WeightedBugFixCommits90d { get; set; }
        public double WeightedBugFixCommits365d { get; set; }

        public ChurnData ToChurnData() => new()
        {
            Changes = Changes90d,
            LinesAdded = LinesAdded90d,
            LinesRemoved = LinesRemoved90d,
            AuthorCount = Authors90d.Count,
            LastChangeAt = LastChangeAt
        };
    }

    internal sealed class HistorySnapshot
    {
        public Dictionary<string, FileHistoryData> FileMetricsByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<DateTime, int> MonthlyCommitCounts { get; } = new();
        public IReadOnlyList<MonthlyCommitBucket> TrailingMonthlyPoints { get; set; } = [];
        public DateTime? FirstCommitAt { get; set; }
        public int TotalCommitCount { get; set; }
        public int SourceCommits90d { get; set; }
        public int SourceCommits365d { get; set; }
        public int FirefightingCommits90d { get; set; }
        public int FirefightingCommits365d { get; set; }
        public double FirefightingRate90d { get; set; }
        public double FirefightingRate365d { get; set; }
        public string? HistoryMaturity { get; set; }
        public bool HasSufficientHistoryForTrends { get; set; }
        public string? ActivityStatus { get; set; }
        public string FirefightingStatus { get; set; } = "Low";
        public string? MonthlyCommitCountsJson { get; set; }
        public int VelocityLast6Months { get; set; }
        public int VelocityPrior6Months { get; set; }
        public double VelocityChangePercent { get; set; }
        public int DormantMonths12m { get; set; }
        public int MaxInactiveStreakMonths { get; set; }
    }

    internal sealed class PendingCommitRecord
    {
        public DateTime AuthorDate { get; set; }
        public string Author { get; set; } = "";
        public string Message { get; set; } = "";
        public List<PendingFileTouch> FileTouches { get; } = [];
    }

    internal sealed class PendingFileTouch
    {
        public string Path { get; set; } = "";
        public int LinesAdded { get; set; }
        public int LinesRemoved { get; set; }
    }

    internal sealed record MonthlyCommitBucket(string Month, int CommitCount);
}
