using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extensions;
using CodeGraph.Services.Prompts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Reviews;

public class ProjectReviewService(
    IGraphStore store,
    IAnalysisProviderRegistry providerRegistry,
    IFileSystem fileSystem,
    ISourceFileProvider sourceFileProvider,
    IProjectReviewBackgroundRunner backgroundRunner,
    IOptions<RepositorySourceOptions> sourceOptionsAccessor,
    IOptions<AnalysisOptions> analysisOptionsAccessor,
    ILogger<ProjectReviewService> logger,
    IAgentPromptService? agentPromptService = null) : IProjectReviewService
{
    private readonly RepositorySourceOptions sourceOptions = sourceOptionsAccessor.Value;
    private readonly AnalysisOptions analysisOptions = analysisOptionsAccessor.Value;
    private static readonly JsonSerializerOptions CamelOpts = CodeGraphJsonDefaults.CamelCase;
    private static readonly HashSet<string> TestPathSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "spec", "specs", ".tests", ".test"
    };
    internal const string CurrentPromptVersion = "v2";
    private static readonly Regex SentenceBoundaryRegex = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);

    public async Task<long> StartReviewAsync(string repo, string projectName, string mode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository name is required.", nameof(repo));
        if (string.IsNullOrWhiteSpace(projectName))
            throw new ArgumentException("Project name is required.", nameof(projectName));

        var repository = await store.GetRepositoryByName(repo)
            ?? throw new InvalidOperationException($"Repository '{repo}' was not found.");
        var reviewMode = NormalizeMode(mode);
        var repoRoot = ResolveRepoRoot(repo, repository);
        var reviewedCommitSha = ResolveReviewedCommitSha(repository, repoRoot);
        var reviewRun = new ProjectReviewRunEntity
        {
            Project = repo,
            ProjectName = projectName,
            ReviewedCommitSha = reviewedCommitSha,
            Status = "queued",
            ReviewMode = reviewMode,
            PromptVersion = CurrentPromptVersion,
            ModelUsed = string.IsNullOrWhiteSpace(analysisOptions.Review.Model) ? null : analysisOptions.Review.Model,
            CreatedAt = DateTime.UtcNow
        };

        var reviewRunId = await store.CreateProjectReviewRunAsync(reviewRun);
        await backgroundRunner.EnqueueAsync(reviewRunId, CancellationToken.None);
        return reviewRunId;
    }

    public async Task ExecuteReviewRunAsync(long reviewRunId, CancellationToken ct = default)
    {
        var run = await store.GetProjectReviewRunAsync(reviewRunId)
            ?? throw new InvalidOperationException($"Project review run '{reviewRunId}' was not found.");
        if (IsTerminalStatus(run.Status))
            return;

        var reviewMode = NormalizeMode(run.ReviewMode);
        var repository = await store.GetRepositoryByName(run.Project)
            ?? throw new InvalidOperationException($"Repository '{run.Project}' was not found.");
        var repoRoot = ResolveRepoRoot(run.Project, repository);

        try
        {
            await store.UpdateProjectReviewRunStatusAsync(reviewRunId, "running");

            var finalReview = await GenerateReviewAsync(run.Project, run.ProjectName, reviewMode, ct);

            var overviewPayload = new StoredProjectReviewOverview(
                finalReview.Overview,
                finalReview.Strengths,
                finalReview.ReviewedAreas,
                finalReview.SkippedAreas,
                finalReview.FollowUps);

            await store.UpsertProjectReviewFindingsAsync(reviewRunId,
                finalReview.Findings.Select((finding, index) => new ProjectReviewFindingEntity
                {
                    Ordinal = index,
                    Severity = finding.Severity,
                    Category = finding.Category,
                    Title = finding.Title,
                    Explanation = finding.Explanation,
                    Evidence = finding.Evidence,
                    FilePath = finding.FilePath,
                    LineStart = finding.LineStart,
                    LineEnd = finding.LineEnd,
                    SuggestedImprovement = finding.SuggestedImprovement,
                    Confidence = finding.Confidence
                }).ToList());

            await store.UpdateProjectReviewRunStatusAsync(
                reviewRunId,
                "completed",
                JsonSerializer.Serialize(overviewPayload, CamelOpts),
                DateTime.UtcNow);

            logger.LogInformation(
                "Completed project review {ReviewRunId} for {Repo}/{ProjectName} with {FindingCount} findings",
                reviewRunId, run.Project, run.ProjectName, finalReview.Findings.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Project review {ReviewRunId} failed for {Repo}/{ProjectName}",
                reviewRunId, run.Project, run.ProjectName);
            await store.UpdateProjectReviewRunStatusAsync(
                reviewRunId,
                "failed",
                completedAt: DateTime.UtcNow,
                error: ex.Message);
            throw;
        }
    }

    public async Task<ProjectReviewResponse> GenerateReviewAsync(
        string repo,
        string projectName,
        string mode,
        CancellationToken ct = default)
        => await GenerateReviewAsync(repo, projectName, new ProjectReviewExecutionInput(mode), ct);

    public async Task<ProjectReviewResponse> GenerateReviewAsync(
        string repo,
        string projectName,
        ProjectReviewExecutionInput input,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository name is required.", nameof(repo));
        if (string.IsNullOrWhiteSpace(projectName))
            throw new ArgumentException("Project name is required.", nameof(projectName));
        ArgumentNullException.ThrowIfNull(input);

        var provider = providerRegistry.GetProvider();
        var reviewMode = NormalizeMode(input.ReviewMode);
        var repository = await store.GetRepositoryByName(repo)
            ?? throw new InvalidOperationException($"Repository '{repo}' was not found.");
        var repoRoot = ResolveRepoRoot(repo, repository);
        var reviewedCommitSha = ResolveReviewedCommitSha(repository, repoRoot);
        var executionInput = NormalizeExecutionInput(input, reviewMode);
        var context = await BuildContextAsync(repository, repo, repoRoot, projectName, reviewMode, executionInput, ct);
        var workflow = await RunWorkflowAsync(repo, projectName, reviewMode, provider, context, ct);
        var verifiedWorkflow = VerifyWorkflow(workflow, context);
        var finalReview = await RunSynthesisAsync(repo, projectName, reviewMode, provider, verifiedWorkflow, ct);

        var now = DateTime.UtcNow;
        return new ProjectReviewResponse(
            new ProjectReviewRunResponse(
                0,
                repo,
                projectName,
                reviewedCommitSha,
                "completed",
                reviewMode,
                CurrentPromptVersion,
                string.IsNullOrWhiteSpace(analysisOptions.Review.Model) ? null : analysisOptions.Review.Model,
                now,
                now,
                now,
                null),
            finalReview.Overview,
            finalReview.Findings.Select(f => new ProjectReviewFindingResponse(
                f.Severity,
                f.Category,
                f.Title,
                f.Explanation,
                f.Evidence,
                f.FilePath,
                f.LineStart,
                f.LineEnd,
                f.SuggestedImprovement,
                f.Confidence)).ToList(),
            finalReview.Strengths,
            finalReview.ReviewedAreas,
            finalReview.SkippedAreas,
            finalReview.FollowUps);
    }

    public async Task<ProjectReviewResponse?> GetReviewAsync(long reviewRunId, CancellationToken ct = default)
    {
        var run = await store.GetProjectReviewRunAsync(reviewRunId);
        return run is null ? null : await BuildReviewResponseAsync(run);
    }

    public async Task<ProjectReviewResponse?> GetLatestReviewAsync(
        string repo,
        string projectName,
        CancellationToken ct = default)
    {
        var run = await store.GetLatestProjectReviewRunAsync(repo, projectName);
        return run is null ? null : await BuildReviewResponseAsync(run);
    }

    public async Task<ProjectDiagnosticsResponse> GetDiagnosticsAsync(
        string repo,
        string? dotnetProject = null,
        CancellationToken ct = default)
    {
        var diagnostics = await store.GetProjectDiagnosticsAsync(repo, dotnetProject);
        return new ProjectDiagnosticsResponse(
            repo,
            dotnetProject,
            diagnostics.Count(d => string.Equals(d.Severity, "error", StringComparison.OrdinalIgnoreCase)),
            diagnostics.Count(d => string.Equals(d.Severity, "warning", StringComparison.OrdinalIgnoreCase)),
            diagnostics.Count(d => !string.Equals(d.Severity, "error", StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(d.Severity, "warning", StringComparison.OrdinalIgnoreCase)),
            diagnostics.Select(d => new ProjectDiagnosticResponse(
                d.Source,
                d.DiagnosticId,
                d.Severity,
                d.Message,
                d.Category,
                d.FilePath,
                d.LineStart,
                d.LineEnd,
                d.ComputedAt)).ToList());
    }

    private async Task<ProjectReviewContext> BuildContextAsync(
        ProjectInfo repository,
        string repo,
        string? repoRoot,
        string projectName,
        string mode,
        ProjectReviewExecutionInput executionInput,
        CancellationToken ct)
    {
        if (repoRoot is null)
            throw new InvalidOperationException($"Could not resolve a local path for repository '{repo}'.");

        var allNodes = await store.GetAllNodesByProjectAsync(repo);
        var projectNodes = allNodes
            .Where(n => string.Equals(n.DotnetProject, projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (projectNodes.Count == 0)
        {
            projectNodes = allNodes.Where(n => string.IsNullOrWhiteSpace(n.DotnetProject)).ToList();
        }

        var nodeCounts = projectNodes
            .GroupBy(n => n.Label)
            .ToDictionary(g => g.Key.ToString(), g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var metrics = await store.GetFileMetricsAsync(repo, projectName);
        var diagnostics = await store.GetProjectDiagnosticsAsync(repo, projectName);
        var securityFindings = (await store.GetSecurityFindingsAsync(repo))
            .Where(f => string.IsNullOrWhiteSpace(f.DotnetProject) ||
                        string.Equals(f.DotnetProject, projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var analyses = await store.GetProjectAnalysesAsync(repo);
        var projectAnalysis = analyses.FirstOrDefault(a =>
            string.Equals(a.ProjectName, projectName, StringComparison.OrdinalIgnoreCase));

        var edges = projectNodes.Count > 0
            ? await store.GetEdgesForNodesAsync(projectNodes.Select(n => n.Id).Distinct().ToList())
            : [];

        var relationshipCounts = edges
            .Where(e => e.Type is not ("DEFINES" or "DEFINES_METHOD" or "CONTAINS_FILE" or "CONTAINS_FOLDER" or "CONTAINS_NAMESPACE" or "CONTAINS_PROJECT"))
            .GroupBy(e => e.Type)
            .Select(g => (Type: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Type)
            .Take(10)
            .ToList();

        var inspectionBudget = GetInspectionBudget(mode);
        var inspectionTargets = SelectInspectionTargets(
            projectNodes,
            metrics,
            diagnostics,
            securityFindings,
            inspectionBudget,
            executionInput);
        var inspectionFiles = await ReadInspectionFilesAsync(
            repo,
            repoRoot,
            inspectionTargets,
            executionInput.ChangedLineSpans,
            preferFocusedSnippets: string.Equals(mode, "update", StringComparison.OrdinalIgnoreCase),
            ct);
        var candidateTests = SelectCandidateTests(repoRoot, inspectionFiles.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase))
            .Concat(executionInput.CandidateTests ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        if (inspectionFiles.Count == 0)
            throw new InvalidOperationException($"No source files could be loaded for {repo}/{projectName} review.");

        return new ProjectReviewContext(
            Repository: repository,
            RepoRoot: repoRoot,
            ReviewMode: mode,
            ProjectName: projectName,
            ProjectSummary: projectAnalysis?.Summary,
            UpdateSummary: executionInput.UpdateSummary,
            BaselineContext: executionInput.BaselineContext,
            ChangedFiles: NormalizePathList(executionInput.SeedFiles),
            BlastRadiusFiles: NormalizePathList(executionInput.BlastRadiusFiles),
            NodeCounts: nodeCounts,
            Metrics: metrics.OrderByDescending(m => m.RiskScore).ToList(),
            Diagnostics: diagnostics,
            SecurityFindings: securityFindings,
            RelationshipCounts: relationshipCounts,
            InspectionFiles: inspectionFiles,
            CandidateTests: candidateTests,
            MaxFindings: Math.Min(analysisOptions.Review.MaxFindings, 20));
    }

    private async Task<ProjectReviewWorkflowResult> RunWorkflowAsync(
        string repo,
        string projectName,
        string mode,
        IAnalysisModelProvider provider,
        ProjectReviewContext context,
        CancellationToken ct)
    {
        var prompt = ProjectReviewWorkflowPromptBuilder.Build(
            repo,
            projectName,
            mode,
            context.ProjectSummary,
            context.UpdateSummary,
            context.BaselineContext,
            context.ChangedFiles,
            context.BlastRadiusFiles,
            context.NodeCounts,
            context.Metrics,
            context.Diagnostics,
            context.SecurityFindings,
            context.CandidateTests,
            context.RelationshipCounts,
            context.InspectionFiles,
            context.MaxFindings);

        var response = await provider.ExecuteAsync(
            new AnalysisPrompt(
                await GetProjectReviewSystemPromptAsync(
                    AgentPromptCatalog.CodeReviewWorkflowSystemPromptKey,
                    ProjectReviewWorkflowPromptBuilder.SystemPrompt,
                    "project review workflow"),
                prompt),
            new AnalysisRequestOptions(
                Model: string.IsNullOrWhiteSpace(analysisOptions.Review.Model) ? null : analysisOptions.Review.Model,
                MaxTokens: Math.Min(analysisOptions.MaxTokensPerSynthesis, 8_000)),
            ct);

        var workflow = DeserializeOrThrow<ProjectReviewWorkflowModel>(response.Text);
        return new ProjectReviewWorkflowResult(
            workflow.Overview ?? "",
            NormalizeStringList(workflow.Strengths),
            NormalizeStringList(workflow.ReviewedAreas),
            NormalizeStringList(workflow.SkippedAreas),
            NormalizeStringList(workflow.FollowUps),
            NormalizeFindings(workflow.CandidateFindings));
    }

    private async Task<ProjectReviewResponseModel> RunSynthesisAsync(
        string repo,
        string projectName,
        string mode,
        IAnalysisModelProvider provider,
        ProjectReviewWorkflowResult workflow,
        CancellationToken ct)
    {
        var prompt = ProjectReviewSynthesisPromptBuilder.Build(
            repo,
            projectName,
            mode,
            JsonSerializer.Serialize(workflow, CamelOpts),
            Math.Min(workflow.Findings.Count, analysisOptions.Review.MaxFindings));

        try
        {
            var response = await provider.ExecuteAsync(
                new AnalysisPrompt(
                    await GetProjectReviewSystemPromptAsync(
                        AgentPromptCatalog.CodeReviewSynthesisSystemPromptKey,
                        ProjectReviewSynthesisPromptBuilder.SystemPrompt,
                        "project review synthesis"),
                    prompt),
                new AnalysisRequestOptions(
                    Model: string.IsNullOrWhiteSpace(analysisOptions.Review.Model) ? null : analysisOptions.Review.Model,
                    MaxTokens: Math.Min(analysisOptions.MaxTokensPerSynthesis, 6_000)),
                ct);

            var synthesis = DeserializeOrThrow<ProjectReviewSynthesisModel>(response.Text);
            return NormalizeFinalReview(new ProjectReviewResponseModel(
                synthesis.Overview ?? workflow.Overview,
                NormalizeStringList(synthesis.Strengths).DefaultIfEmptyList(workflow.Strengths),
                NormalizeStringList(synthesis.ReviewedAreas).DefaultIfEmptyList(workflow.ReviewedAreas),
                NormalizeStringList(synthesis.SkippedAreas).DefaultIfEmptyList(workflow.SkippedAreas),
                NormalizeStringList(synthesis.FollowUps).DefaultIfEmptyList(workflow.FollowUps),
                NormalizeFindings(synthesis.Findings)),
                analysisOptions.Review.MaxFindings);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Review synthesis failed, falling back to verified workflow notes");
            return NormalizeFinalReview(new ProjectReviewResponseModel(
                workflow.Overview,
                workflow.Strengths,
                workflow.ReviewedAreas,
                workflow.SkippedAreas,
                workflow.FollowUps,
                workflow.Findings),
                analysisOptions.Review.MaxFindings);
        }
    }

    private Task<string> GetProjectReviewSystemPromptAsync(string promptKey, string defaultPrompt, string usage)
        => AgentPromptExecution.GetEffectivePromptOrDefaultAsync(
            agentPromptService,
            promptKey,
            defaultPrompt,
            logger,
            usage);

    private ProjectReviewWorkflowResult VerifyWorkflow(ProjectReviewWorkflowResult workflow, ProjectReviewContext context)
    {
        var inspectedByPath = context.InspectionFiles.ToDictionary(
            f => NormalizePath(f.Path),
            f => f,
            StringComparer.OrdinalIgnoreCase);

        var deduped = new List<ProjectReviewFindingModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var finding in workflow.Findings)
        {
            var resolvedPath = ResolveFindingPath(finding.FilePath, inspectedByPath.Keys);
            if (resolvedPath is null)
                continue;
            if (string.IsNullOrWhiteSpace(finding.Title) ||
                string.IsNullOrWhiteSpace(finding.Explanation) ||
                string.IsNullOrWhiteSpace(finding.Evidence))
                continue;

            var inspected = inspectedByPath[resolvedPath];
            var lineStart = NormalizeLine(finding.LineStart, inspected.LineCount);
            var lineEnd = NormalizeLine(finding.LineEnd, inspected.LineCount);
            if (lineStart is not null && lineEnd is not null && lineEnd < lineStart)
                lineEnd = lineStart;

            var normalized = finding with
            {
                Severity = NormalizeSeverity(finding.Severity),
                Category = NormalizeCategory(finding.Category),
                Confidence = NormalizeConfidence(finding.Confidence),
                FilePath = inspected.Path,
                LineStart = lineStart,
                LineEnd = lineEnd
            };

            var key = $"{normalized.FilePath}|{normalized.Category}|{normalized.Title}";
            if (!seen.Add(key))
                continue;

            deduped.Add(normalized);
            if (deduped.Count >= context.MaxFindings)
                break;
        }

        return workflow with
        {
            ReviewedAreas = NormalizeStringList(workflow.ReviewedAreas).DefaultIfEmptyList(
                context.InspectionFiles.Select(f => $"{f.Path} ({f.Reason})").ToList()),
            SkippedAreas = NormalizeStringList(workflow.SkippedAreas).DefaultIfEmptyList(
                BuildSkippedAreas(context)),
            Findings = deduped
        };
    }

    private async Task<IReadOnlyList<ReviewInspectionFile>> ReadInspectionFilesAsync(
        string repo,
        string repoRoot,
        IReadOnlyList<InspectionTarget> targets,
        IReadOnlyDictionary<string, IReadOnlyList<ProjectReviewLineSpan>>? changedLineSpans,
        bool preferFocusedSnippets,
        CancellationToken ct)
    {
        var results = new List<ReviewInspectionFile>();
        foreach (var target in targets)
        {
            var fullPath = RepoFileResolver.Resolve(repo, target.FilePath, sourceOptions.ReposCachePath, repoRoot)
                ?? Path.Combine(repoRoot, target.FilePath.Replace('/', Path.DirectorySeparatorChar));
            if (!fileSystem.FileExists(fullPath))
                continue;

            var content = await fileSystem.ReadAllTextAsync(fullPath, ct);
            var lines = content.Replace("\r\n", "\n").Split('\n');
            var normalizedPath = NormalizePath(target.FilePath);
            var focusedSpans = changedLineSpans is not null &&
                               changedLineSpans.TryGetValue(normalizedPath, out var spans)
                ? spans
                : null;
            var hasFocusedSpans = preferFocusedSnippets && focusedSpans is not null && focusedSpans.Count > 0;
            var renderedContent = hasFocusedSpans
                ? BuildFocusedInspectionContent(lines, focusedSpans!, analysisOptions.Review.MaxSourceCharsPerFile)
                : BuildNumberedInspectionContent(lines, analysisOptions.Review.MaxSourceCharsPerFile);
            var reason = hasFocusedSpans
                ? $"{target.Reason}; changed lines {FormatLineSpans(focusedSpans!)} with surrounding context"
                : target.Reason;

            results.Add(new ReviewInspectionFile(target.FilePath, reason, renderedContent, lines.Length));
        }

        return results;
    }

    private IReadOnlyList<string> SelectCandidateTests(string repoRoot, IReadOnlySet<string> inspectedFiles)
    {
        if (!sourceFileProvider.RootExists(repoRoot))
            return [];

        var inspectedNames = inspectedFiles
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return sourceFileProvider.EnumerateSourceFiles(repoRoot)
            .Where(file => IsTestFile(file.RelativePath))
            .Where(file =>
            {
                var stem = Path.GetFileNameWithoutExtension(file.RelativePath);
                return inspectedNames.Any(name => stem.Contains(name, StringComparison.OrdinalIgnoreCase)
                                                  || name.Contains(stem, StringComparison.OrdinalIgnoreCase));
            })
            .Select(file => file.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    private IReadOnlyList<InspectionTarget> SelectInspectionTargets(
        IReadOnlyList<NodeEntity> projectNodes,
        IReadOnlyList<FileMetricsEntity> metrics,
        IReadOnlyList<ProjectDiagnosticEntity> diagnostics,
        IReadOnlyList<SecurityFindingEntity> securityFindings,
        int maxFiles,
        ProjectReviewExecutionInput executionInput)
    {
        var candidates = new Dictionary<string, InspectionTargetBuilder>(StringComparer.OrdinalIgnoreCase);
        var prioritizedFiles = NormalizePathList(executionInput.SeedFiles)
            .Concat(NormalizePathList(executionInput.BlastRadiusFiles))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var metric in metrics)
        {
            var candidate = GetOrAddCandidate(candidates, metric.FilePath);
            candidate.Score += metric.RiskScore * 2 + metric.ComplexityScore / 20.0 + metric.LongestFunction / 40.0;
            if (metric.LintErrors > 0) candidate.Score += metric.LintErrors * 4;
            if (metric.LintWarnings > 0) candidate.Score += metric.LintWarnings * 1.5;
            candidate.Reasons.Add(
                $"risk {metric.RiskScore:F1}, complexity {metric.ComplexityScore}, longest function {metric.LongestFunction}, lint {metric.LintErrors}/{metric.LintWarnings}");
        }

        foreach (var diagnostic in diagnostics)
        {
            var candidate = GetOrAddCandidate(candidates, diagnostic.FilePath);
            candidate.Score += diagnostic.Severity.ToLowerInvariant() switch
            {
                "error" => 8,
                "warning" => 3,
                "info" => 1,
                _ => 0.5
            };
            candidate.Reasons.Add($"diagnostic {diagnostic.DiagnosticId}: {diagnostic.Message}");
        }

        foreach (var finding in securityFindings.Where(f => !string.IsNullOrWhiteSpace(f.FilePath)))
        {
            var candidate = GetOrAddCandidate(candidates, finding.FilePath!);
            candidate.Score += finding.Severity.ToLowerInvariant() switch
            {
                "critical" => 10,
                "high" => 7,
                "medium" => 4,
                "low" => 2,
                _ => 1
            };
            candidate.Reasons.Add($"security {finding.Severity}: {finding.Title}");
        }

        var labelsByFile = projectNodes
            .Where(n => !string.IsNullOrWhiteSpace(n.FilePath))
            .GroupBy(n => n.FilePath)
            .ToDictionary(g => g.Key, g => g.Select(n => n.Label.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var (path, labels) in labelsByFile)
        {
            if (!candidates.TryGetValue(path, out var candidate))
                continue;

            if (labels.Any(l => l is "Class" or "Method"))
                candidate.Score += 1;
            if (labels.Any(l => l is "Route" or "Controller" or "Service"))
                candidate.Score += 1.5;
        }

        if (candidates.Count == 0)
        {
            foreach (var filePath in projectNodes
                         .Where(n => n.Label == "File" && !string.IsNullOrWhiteSpace(n.FilePath))
                         .Select(n => n.FilePath)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Take(maxFiles))
            {
                var candidate = GetOrAddCandidate(candidates, filePath);
                candidate.Score += 1;
                candidate.Reasons.Add("representative project source file");
            }
        }

        foreach (var filePath in NormalizePathList(executionInput.BlastRadiusFiles))
        {
            var candidate = GetOrAddCandidate(candidates, filePath);
            candidate.Score += 30;
            candidate.Reasons.Add("blast-radius file related to the changed scope");
        }

        foreach (var filePath in NormalizePathList(executionInput.SeedFiles))
        {
            var candidate = GetOrAddCandidate(candidates, filePath);
            candidate.Score += 100;
            candidate.Reasons.Add("changed file in the update scope");
        }

        var selectedCandidates = prioritizedFiles.Count > 0
            ? candidates.Values.Where(c => prioritizedFiles.Contains(c.FilePath))
            : candidates.Values;

        return selectedCandidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxFiles))
            .Select(c => new InspectionTarget(c.FilePath, string.Join("; ", c.Reasons.Take(3)), c.Score))
            .ToList();
    }

    private static InspectionTargetBuilder GetOrAddCandidate(
        IDictionary<string, InspectionTargetBuilder> candidates,
        string filePath)
    {
        var normalized = NormalizePath(filePath);
        if (!candidates.TryGetValue(normalized, out var candidate))
        {
            candidate = new InspectionTargetBuilder(normalized);
            candidates[normalized] = candidate;
        }

        return candidate;
    }

    private int GetInspectionBudget(string mode)
    {
        return NormalizeMode(mode) switch
        {
            "update" => Math.Max(1, Math.Min(analysisOptions.Review.MaxFilesToInspect, 12)),
            "quick" => Math.Max(1, analysisOptions.Review.MaxFilesToInspect / 2),
            "deep" => Math.Max(1, Math.Min(analysisOptions.Review.MaxFilesToInspect * 2, 50)),
            _ => Math.Max(1, analysisOptions.Review.MaxFilesToInspect)
        };
    }

    private static string NormalizeMode(string mode)
        => mode.Trim().ToLowerInvariant() switch
        {
            "update" => "update",
            "quick" => "quick",
            "deep" => "deep",
            _ => "standard"
        };

    private string? ResolveRepoRoot(string repo, ProjectInfo repository)
    {
        if (!string.IsNullOrWhiteSpace(repository.LocalPath) && Directory.Exists(repository.LocalPath))
            return repository.LocalPath;

        if (!string.IsNullOrWhiteSpace(sourceOptions.ReposCachePath))
        {
            var cachePath = Path.Combine(sourceOptions.ReposCachePath, repo);
            if (Directory.Exists(cachePath))
                return cachePath;
        }

        return null;
    }

    private string? ResolveReviewedCommitSha(ProjectInfo repository, string? repoRoot)
    {
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            var headSha = GetHeadCommitSha(repoRoot);
            if (!string.IsNullOrWhiteSpace(headSha))
                return headSha;
        }

        return repository.LastCommitSha;
    }

    private string? GetHeadCommitSha(string repoPath)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            var sha = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            return proc.ExitCode == 0 && sha.Length >= 7 ? sha : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to get HEAD commit SHA for review repo path {RepoPath}", repoPath);
            return null;
        }
    }

    private async Task<ProjectReviewResponse> BuildReviewResponseAsync(ProjectReviewRunEntity run)
    {
        var findings = await store.GetProjectReviewFindingsAsync(run.Id);
        var overview = DeserializeOrDefault<StoredProjectReviewOverview>(run.OverviewJson)
            ?? new StoredProjectReviewOverview("", [], [], [], []);

        return new ProjectReviewResponse(
            MapRun(run),
            overview.Overview,
            findings.OrderBy(f => f.Ordinal).Select(MapFinding).ToList(),
            overview.Strengths,
            overview.ReviewedAreas,
            overview.SkippedAreas,
            overview.FollowUps);
    }

    private static ProjectReviewRunResponse MapRun(ProjectReviewRunEntity run)
        => new(
            run.Id,
            run.Project,
            run.ProjectName,
            run.ReviewedCommitSha,
            run.Status,
            run.ReviewMode,
            run.PromptVersion,
            run.ModelUsed,
            run.CreatedAt,
            run.StartedAt,
            run.CompletedAt,
            run.Error);

    private static bool IsTerminalStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);

    private static ProjectReviewFindingResponse MapFinding(ProjectReviewFindingEntity finding)
        => new(
            finding.Severity,
            finding.Category,
            finding.Title,
            finding.Explanation,
            finding.Evidence,
            finding.FilePath,
            finding.LineStart,
            finding.LineEnd,
            finding.SuggestedImprovement,
            finding.Confidence);

    private static T DeserializeOrThrow<T>(string text)
        where T : class
        => JsonSerializer.Deserialize<T>(text.NormalizeJsonResponse(), CamelOpts)
           ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name}.");

    private static T? DeserializeOrDefault<T>(string? text)
        where T : class
        => string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<T>(text, CamelOpts);

    private static IReadOnlyList<string> NormalizeStringList(IReadOnlyList<string>? values)
        => values?.Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

    private static IReadOnlyList<string> BuildSkippedAreas(ProjectReviewContext context)
    {
        if (string.Equals(context.ReviewMode, "update", StringComparison.OrdinalIgnoreCase))
        {
            var skipped = new List<string>();
            if (context.BlastRadiusFiles.Count > context.InspectionFiles.Count)
                skipped.Add($"{context.BlastRadiusFiles.Count - context.InspectionFiles.Count} related files in the blast radius were not inspected in this update pass.");
            if (context.ChangedFiles.Count == 0)
                skipped.Add("No changed source files were available for direct inspection in this update pass.");
            return skipped;
        }

        if (context.Metrics.Count <= context.InspectionFiles.Count)
            return [];

        return [$"{context.Metrics.Count - context.InspectionFiles.Count} lower-priority files were not inspected due to review budget limits."];
    }

    private static IReadOnlyList<ProjectReviewFindingModel> NormalizeFindings(IReadOnlyList<ProjectReviewFindingModel>? findings)
        => findings?
            .Where(f => !string.IsNullOrWhiteSpace(f.Title))
            .Select(f => f with
            {
                Severity = NormalizeSeverity(f.Severity),
                Category = NormalizeCategory(f.Category),
                Confidence = NormalizeConfidence(f.Confidence),
                FilePath = NormalizePath(f.FilePath)
            })
            .ToList() ?? [];

    private static ProjectReviewResponseModel NormalizeFinalReview(ProjectReviewResponseModel review, int maxFindings)
        => review with
        {
            Overview = ShortenText(review.Overview, maxChars: 320, maxSentences: 2),
            Strengths = NormalizeSummaryList(review.Strengths, maxItems: 4, maxChars: 140),
            ReviewedAreas = NormalizeSummaryList(review.ReviewedAreas, maxItems: 5, maxChars: 140),
            SkippedAreas = NormalizeSummaryList(review.SkippedAreas, maxItems: 3, maxChars: 160),
            FollowUps = NormalizeSummaryList(review.FollowUps, maxItems: 4, maxChars: 160),
            Findings = review.Findings
                .Take(Math.Max(0, maxFindings))
                .Select(f => f with
                {
                    Title = ShortenText(f.Title, maxChars: 120, maxSentences: 1),
                    Explanation = ShortenText(f.Explanation, maxChars: 260, maxSentences: 2),
                    Evidence = ShortenText(f.Evidence, maxChars: 260, maxSentences: 2),
                    SuggestedImprovement = ShortenText(f.SuggestedImprovement, maxChars: 220, maxSentences: 2)
                })
                .ToList()
        };

    private static IReadOnlyList<string> NormalizeSummaryList(
        IReadOnlyList<string>? values,
        int maxItems,
        int maxChars)
        => NormalizeStringList(values)
            .Select(v => ShortenText(v, maxChars, maxSentences: 1))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Take(Math.Max(0, maxItems))
            .ToList();

    private static string ShortenText(string? value, int maxChars, int maxSentences)
    {
        var normalized = NormalizeWhitespace(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        if (maxSentences > 0)
        {
            var limitedSentences = SentenceBoundaryRegex
                .Split(normalized)
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Take(maxSentences)
                .ToArray();

            if (limitedSentences.Length > 0)
                normalized = string.Join(" ", limitedSentences);
        }

        if (normalized.Length <= maxChars)
            return normalized;

        var truncated = normalized[..Math.Min(maxChars, normalized.Length)].TrimEnd();
        var lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace >= maxChars / 2)
            truncated = truncated[..lastSpace].TrimEnd();

        return truncated + "...";
    }

    private static string NormalizeWhitespace(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? ""
            : string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static ProjectReviewExecutionInput NormalizeExecutionInput(ProjectReviewExecutionInput input, string normalizedMode)
        => input with
        {
            ReviewMode = normalizedMode,
            SeedFiles = NormalizePathList(input.SeedFiles),
            BlastRadiusFiles = NormalizePathList(input.BlastRadiusFiles),
            ChangedLineSpans = NormalizeChangedLineSpans(input.ChangedLineSpans),
            CandidateTests = NormalizePathList(input.CandidateTests),
            UpdateSummary = NormalizeWhitespace(input.UpdateSummary)
        };

    private static IReadOnlyList<string> NormalizePathList(IReadOnlyList<string>? values)
        => values?
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

    private static IReadOnlyDictionary<string, IReadOnlyList<ProjectReviewLineSpan>> NormalizeChangedLineSpans(
        IReadOnlyDictionary<string, IReadOnlyList<ProjectReviewLineSpan>>? changedLineSpans)
    {
        if (changedLineSpans is null || changedLineSpans.Count == 0)
            return EmptyChangedLineSpans;

        return changedLineSpans
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToDictionary(
                entry => NormalizePath(entry.Key),
                entry => (IReadOnlyList<ProjectReviewLineSpan>)entry.Value
                    .Where(span => span.StartLine > 0 && span.EndLine >= span.StartLine)
                    .Distinct()
                    .OrderBy(span => span.StartLine)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildNumberedInspectionContent(string[] lines, int maxChars)
    {
        var numbered = new StringBuilder();
        var written = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = $"{i + 1,4}: {lines[i]}";
            if (written + line.Length + 1 > maxChars)
            {
                numbered.AppendLine("... truncated ...");
                break;
            }

            numbered.AppendLine(line);
            written += line.Length + 1;
        }

        return numbered.ToString().TrimEnd();
    }

    private static string BuildFocusedInspectionContent(
        string[] lines,
        IReadOnlyList<ProjectReviewLineSpan> spans,
        int maxChars)
    {
        var windowedRanges = MergeLineSpans(spans, lines.Length, contextWindow: 8);
        var numbered = new StringBuilder();
        var written = 0;

        for (var rangeIndex = 0; rangeIndex < windowedRanges.Count; rangeIndex++)
        {
            var range = windowedRanges[rangeIndex];
            var header = $"... focus lines {range.StartLine}-{range.EndLine} ...";
            if (written + header.Length + 1 > maxChars)
            {
                numbered.AppendLine("... truncated ...");
                break;
            }

            numbered.AppendLine(header);
            written += header.Length + 1;

            for (var lineNumber = range.StartLine; lineNumber <= range.EndLine; lineNumber++)
            {
                var line = $"{lineNumber,4}: {lines[lineNumber - 1]}";
                if (written + line.Length + 1 > maxChars)
                {
                    numbered.AppendLine("... truncated ...");
                    return numbered.ToString().TrimEnd();
                }

                numbered.AppendLine(line);
                written += line.Length + 1;
            }

            if (rangeIndex < windowedRanges.Count - 1)
            {
                const string divider = "... omitted unchanged lines ...";
                if (written + divider.Length + 1 > maxChars)
                {
                    numbered.AppendLine("... truncated ...");
                    break;
                }

                numbered.AppendLine(divider);
                written += divider.Length + 1;
            }
        }

        return numbered.ToString().TrimEnd();
    }

    private static IReadOnlyList<ProjectReviewLineSpan> MergeLineSpans(
        IReadOnlyList<ProjectReviewLineSpan> spans,
        int lineCount,
        int contextWindow)
    {
        var normalized = spans
            .Select(span => new ProjectReviewLineSpan(
                Math.Max(1, span.StartLine - contextWindow),
                Math.Min(lineCount, span.EndLine + contextWindow)))
            .OrderBy(span => span.StartLine)
            .ToList();
        if (normalized.Count == 0)
            return [];

        var merged = new List<ProjectReviewLineSpan> { normalized[0] };
        for (var i = 1; i < normalized.Count; i++)
        {
            var current = normalized[i];
            var last = merged[^1];
            if (current.StartLine <= last.EndLine + 1)
            {
                merged[^1] = last with { EndLine = Math.Max(last.EndLine, current.EndLine) };
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged;
    }

    private static string FormatLineSpans(IReadOnlyList<ProjectReviewLineSpan> spans)
        => string.Join(", ", spans.Select(span => span.StartLine == span.EndLine
            ? span.StartLine.ToString()
            : $"{span.StartLine}-{span.EndLine}"));

    private static string NormalizeSeverity(string? severity) => severity?.Trim().ToLowerInvariant() switch
    {
        "critical" => "critical",
        "high" => "high",
        "medium" => "medium",
        "low" => "low",
        _ => "medium"
    };

    private static string NormalizeCategory(string? category) => category?.Trim().ToLowerInvariant() switch
    {
        "bug" => "bug",
        "security" => "security",
        "reliability" => "reliability",
        "maintainability" => "maintainability",
        "readability" => "readability",
        "design" => "design",
        "dead-code" => "dead-code",
        "test-gap" => "test-gap",
        _ => "maintainability"
    };

    private static string NormalizeConfidence(string? confidence) => confidence?.Trim().ToLowerInvariant() switch
    {
        "high" => "high",
        "low" => "low",
        _ => "medium"
    };

    private static string NormalizePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').Trim();

    private static int? NormalizeLine(int? line, int lineCount)
    {
        if (line is null || line <= 0 || line > lineCount)
            return null;
        return line;
    }

    private static string? ResolveFindingPath(string? modelPath, IEnumerable<string> inspectedPaths)
    {
        var normalized = NormalizePath(modelPath);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var inspectedList = inspectedPaths.ToList();
        var exact = inspectedList.FirstOrDefault(path => path.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var suffixMatches = inspectedList
            .Where(path => path.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase)
                           || Path.GetFileName(path).Equals(Path.GetFileName(normalized), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return suffixMatches.Count == 1 ? suffixMatches[0] : null;
    }

    private static bool IsTestFile(string relativePath)
    {
        var normalized = NormalizePath(relativePath);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => TestPathSegments.Contains(p))
               || Path.GetFileNameWithoutExtension(normalized).Contains("test", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProjectReviewContext(
        ProjectInfo Repository,
        string RepoRoot,
        string ReviewMode,
        string ProjectName,
        string? ProjectSummary,
        string? UpdateSummary,
        ProjectReviewBaselineContext? BaselineContext,
        IReadOnlyList<string> ChangedFiles,
        IReadOnlyList<string> BlastRadiusFiles,
        IReadOnlyDictionary<string, int> NodeCounts,
        IReadOnlyList<FileMetricsEntity> Metrics,
        IReadOnlyList<ProjectDiagnosticEntity> Diagnostics,
        IReadOnlyList<SecurityFindingEntity> SecurityFindings,
        IReadOnlyList<(string Type, int Count)> RelationshipCounts,
        IReadOnlyList<ReviewInspectionFile> InspectionFiles,
        IReadOnlyList<string> CandidateTests,
        int MaxFindings);

    private sealed record InspectionTarget(string FilePath, string Reason, double Score);

    private sealed class InspectionTargetBuilder(string filePath)
    {
        public string FilePath { get; } = filePath;
        public double Score { get; set; }
        public List<string> Reasons { get; } = [];
    }

    private sealed record ProjectReviewWorkflowModel(
        string? Overview,
        IReadOnlyList<string>? Strengths,
        IReadOnlyList<string>? ReviewedAreas,
        IReadOnlyList<string>? SkippedAreas,
        IReadOnlyList<string>? FollowUps,
        IReadOnlyList<ProjectReviewFindingModel>? CandidateFindings);

    private sealed record ProjectReviewSynthesisModel(
        string? Overview,
        IReadOnlyList<string>? Strengths,
        IReadOnlyList<string>? ReviewedAreas,
        IReadOnlyList<string>? SkippedAreas,
        IReadOnlyList<string>? FollowUps,
        IReadOnlyList<ProjectReviewFindingModel>? Findings);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ProjectReviewLineSpan>> EmptyChangedLineSpans =
        new Dictionary<string, IReadOnlyList<ProjectReviewLineSpan>>(StringComparer.OrdinalIgnoreCase);

    internal sealed record ProjectReviewWorkflowResult(
        string Overview,
        IReadOnlyList<string> Strengths,
        IReadOnlyList<string> ReviewedAreas,
        IReadOnlyList<string> SkippedAreas,
        IReadOnlyList<string> FollowUps,
        IReadOnlyList<ProjectReviewFindingModel> Findings);

    private sealed record ProjectReviewResponseModel(
        string Overview,
        IReadOnlyList<string> Strengths,
        IReadOnlyList<string> ReviewedAreas,
        IReadOnlyList<string> SkippedAreas,
        IReadOnlyList<string> FollowUps,
        IReadOnlyList<ProjectReviewFindingModel> Findings);

    internal sealed record ProjectReviewFindingModel(
        string Severity,
        string Category,
        string Title,
        string Explanation,
        string Evidence,
        string FilePath,
        int? LineStart,
        int? LineEnd,
        string SuggestedImprovement,
        string Confidence);

    private sealed record StoredProjectReviewOverview(
        string Overview,
        IReadOnlyList<string> Strengths,
        IReadOnlyList<string> ReviewedAreas,
        IReadOnlyList<string> SkippedAreas,
        IReadOnlyList<string> FollowUps);
}

internal static class ProjectReviewListExtensions
{
    public static IReadOnlyList<T> DefaultIfEmptyList<T>(this IReadOnlyList<T> value, IReadOnlyList<T> fallback)
        => value.Count > 0 ? value : fallback;
}
