using System.Diagnostics;
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

public class RepositoryReviewService(
    IGraphStore store,
    IAnalysisProviderRegistry providerRegistry,
    ProjectReviewService projectReviewService,
    IRepositoryReviewBackgroundRunner backgroundRunner,
    IOptions<RepositorySourceOptions> sourceOptionsAccessor,
    IOptions<AnalysisOptions> analysisOptionsAccessor,
    ILogger<RepositoryReviewService> logger,
    IAgentPromptService? agentPromptService = null,
    IDbBackedReviewSettingsResolver? reviewSettingsResolver = null) : IRepositoryReviewService
{
    private readonly RepositorySourceOptions sourceOptions = sourceOptionsAccessor.Value;
    private readonly AnalysisOptions analysisOptions = analysisOptionsAccessor.Value;
    private static readonly JsonSerializerOptions CamelOpts = CodeGraphJsonDefaults.CamelCase;
    private static readonly Regex DiffHunkRegex = new(@"^@@ -\d+(?:,\d+)? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@", RegexOptions.Compiled);
    private static readonly HashSet<string> BlastRadiusEdgeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CALLS", "IMPLEMENTS", "INHERITS", "USES_TYPE", "INJECTS", "HTTP_CALLS", "HANDLES",
        "QUERIES", "PUBLISHES", "CONSUMES", "ROUTED_TO", "REGISTERS", "CARRIES_FIELD",
        "SUBSCRIBES", "RENDERS", "SCHEDULES", "FILE_CHANGES_WITH"
    };
    internal const string CurrentPromptVersion = "v2";
    private const int MaxChangedFilesForIncrementalReview = 40;
    private const double MaxImpactedProjectRatioForIncrementalReview = 0.7;

    public async Task<long> StartReviewAsync(string repo, string mode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository name is required.", nameof(repo));

        var repository = await store.GetRepositoryByName(repo)
            ?? throw new InvalidOperationException($"Repository '{repo}' was not found.");
        var reviewMode = NormalizeMode(mode);
        var repoRoot = ResolveRepoRoot(repo, repository);
        var reviewedCommitSha = ResolveReviewedCommitSha(repository, repoRoot);
        var reviewSettings = await GetReviewSettingsAsync(ct);

        long? baselineReviewRunId = null;
        string? baselineCommitSha = null;

        if (string.Equals(reviewMode, "update", StringComparison.OrdinalIgnoreCase))
        {
            var latest = await store.GetLatestRepositoryReviewRunAsync(repo);
            if (latest is not null &&
                string.Equals(latest.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(latest.ReviewedCommitSha) &&
                !string.Equals(latest.ReviewedCommitSha, reviewedCommitSha, StringComparison.OrdinalIgnoreCase))
            {
                baselineReviewRunId = latest.Id;
                baselineCommitSha = latest.ReviewedCommitSha;
            }
            else if (latest is not null &&
                     string.Equals(latest.Status, "interrupted", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(latest.ReviewMode, "update", StringComparison.OrdinalIgnoreCase) &&
                     latest.BaselineReviewRunId.HasValue &&
                     !string.IsNullOrWhiteSpace(latest.BaselineCommitSha) &&
                     string.Equals(latest.ReviewedCommitSha, reviewedCommitSha, StringComparison.OrdinalIgnoreCase))
            {
                baselineReviewRunId = latest.BaselineReviewRunId;
                baselineCommitSha = latest.BaselineCommitSha;
            }
            else
            {
                reviewMode = "full";
            }
        }

        var reviewRun = new RepositoryReviewRunEntity
        {
            Repo = repo,
            ReviewedCommitSha = reviewedCommitSha,
            BaselineReviewRunId = baselineReviewRunId,
            BaselineCommitSha = baselineCommitSha,
            Status = "queued",
            ReviewMode = reviewMode,
            PromptVersion = CurrentPromptVersion,
            ModelUsed = string.IsNullOrWhiteSpace(reviewSettings.DefaultModel) ? null : reviewSettings.DefaultModel,
            CreatedAt = DateTime.UtcNow
        };

        var reviewRunId = await store.CreateRepositoryReviewRunAsync(reviewRun);
        await backgroundRunner.EnqueueAsync(reviewRunId, CancellationToken.None);
        return reviewRunId;
    }

    public async Task ExecuteReviewRunAsync(long reviewRunId, CancellationToken ct = default)
    {
        var run = await store.GetRepositoryReviewRunAsync(reviewRunId)
            ?? throw new InvalidOperationException($"Repository review run '{reviewRunId}' was not found.");
        if (IsTerminalStatus(run.Status))
            return;

        var repository = await store.GetRepositoryByName(run.Repo)
            ?? throw new InvalidOperationException($"Repository '{run.Repo}' was not found.");
        var repoRoot = ResolveRepoRoot(run.Repo, repository);

        try
        {
            await store.UpdateRepositoryReviewRunStatusAsync(reviewRunId, "running");

            var projectNames = await GetProjectNamesAsync(run.Repo);
            if (projectNames.Count == 0)
                throw new InvalidOperationException($"Repository '{run.Repo}' has no projects available for review.");

            var baselineReview = run.BaselineReviewRunId.HasValue
                ? await GetReviewAsync(run.BaselineReviewRunId.Value, ct)
                : null;
            var executionPlan = await BuildExecutionPlanAsync(run, repoRoot, projectNames, baselineReview, ct);

            var projectSections = new List<RepositoryProjectReviewSectionResult>();
            foreach (var projectName in projectNames)
            {
                ct.ThrowIfCancellationRequested();

                var projectPlan = executionPlan.ProjectPlans[projectName];
                if (projectPlan.ReuseBaselineSection)
                {
                    var baselineSection = baselineReview?.ProjectReviews
                        .FirstOrDefault(section => string.Equals(section.ProjectName, projectName, StringComparison.OrdinalIgnoreCase));
                    if (baselineSection is null)
                        throw new InvalidOperationException($"Baseline repository review is missing section '{projectName}'.");

                    projectSections.Add(new RepositoryProjectReviewSectionResult(
                        ReuseBaselineProjectReview(run.Repo, baselineReview!, baselineSection),
                        true,
                        projectPlan.ReuseReason));
                    continue;
                }

                var review = executionPlan.UseFullReview
                    ? await projectReviewService.GenerateReviewAsync(run.Repo, projectName, "standard", ct)
                    : await projectReviewService.GenerateReviewAsync(
                        run.Repo,
                        projectName,
                        new ProjectReviewExecutionInput(
                            projectPlan.ReviewMode,
                            SeedFiles: projectPlan.SeedFiles,
                            BlastRadiusFiles: projectPlan.BlastRadiusFiles,
                            ChangedLineSpans: projectPlan.ChangedLineSpans,
                            CandidateTests: projectPlan.CandidateTests,
                            UpdateSummary: BuildProjectUpdateSummary(projectPlan),
                            BaselineContext: BuildBaselineContext(
                                baselineReview?.ProjectReviews.FirstOrDefault(section =>
                                    string.Equals(section.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)))),
                        ct);
                projectSections.Add(new RepositoryProjectReviewSectionResult(review, false, null));
            }

            var reviewSettings = await GetReviewSettingsAsync(ct);
            var synthesized = await SynthesizeRepositoryReviewAsync(
                run.Repo,
                executionPlan.Mode,
                run.ReviewedCommitSha,
                baselineReview,
                executionPlan,
                projectSections,
                reviewSettings,
                ct);

            var orderedFindings = projectSections
                .SelectMany(section => section.Review.Findings.Select(f => new RepositoryReviewFindingResponse(
                    f.Severity,
                    f.Category,
                    f.Title,
                    f.Explanation,
                    f.Evidence,
                    f.FilePath,
                    f.LineStart,
                    f.LineEnd,
                    f.SuggestedImprovement,
                    f.Confidence,
                    section.Review.Run.ProjectName)))
                .OrderBy(f => SeverityRank(f.Severity))
                .ThenBy(f => ProjectNameSortKey(f.ProjectName))
                .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, reviewSettings.MaxFindings))
                .ToList();

            await store.UpsertRepositoryReviewFindingsAsync(
                reviewRunId,
                orderedFindings.Select((finding, index) => new RepositoryReviewFindingEntity
                {
                    Ordinal = index,
                    ProjectName = finding.ProjectName,
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

            await store.UpsertRepositoryReviewProjectSectionsAsync(
                reviewRunId,
                projectSections.Select(section => new RepositoryReviewProjectSectionEntity
                {
                    ProjectName = section.Review.Run.ProjectName,
                    Overview = section.Review.Overview,
                    StrengthsJson = JsonSerializer.Serialize(section.Review.Strengths, CamelOpts),
                    ReviewedAreasJson = JsonSerializer.Serialize(section.Review.ReviewedAreas, CamelOpts),
                    SkippedAreasJson = JsonSerializer.Serialize(section.Review.SkippedAreas, CamelOpts),
                    FollowUpsJson = JsonSerializer.Serialize(section.Review.FollowUps, CamelOpts),
                    ReusedFromBaseline = section.ReusedFromBaseline
                }).ToList());

            var overviewPayload = new StoredRepositoryReviewOverview(
                synthesized.Overview,
                synthesized.Strengths,
                synthesized.ReviewedAreas,
                synthesized.SkippedAreas,
                synthesized.FollowUps);

            await store.UpdateRepositoryReviewRunStatusAsync(
                reviewRunId,
                "completed",
                JsonSerializer.Serialize(overviewPayload, CamelOpts),
                DateTime.UtcNow);

            logger.LogInformation(
                "Completed repository review {ReviewRunId} for {Repo} across {ProjectCount} projects with {FindingCount} findings (fullReview={UseFullReview}, impactedProjects={ImpactedProjectCount})",
                reviewRunId, run.Repo, projectSections.Count, orderedFindings.Count, executionPlan.UseFullReview, executionPlan.ImpactedProjects.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Repository review {ReviewRunId} failed for {Repo}", reviewRunId, run.Repo);
            await store.UpdateRepositoryReviewRunStatusAsync(
                reviewRunId,
                "failed",
                completedAt: DateTime.UtcNow,
                error: ex.Message);
            throw;
        }
    }

    public async Task<RepositoryReviewResponse?> GetReviewAsync(long reviewRunId, CancellationToken ct = default)
    {
        var run = await store.GetRepositoryReviewRunAsync(reviewRunId);
        return run is null ? null : await BuildReviewResponseAsync(run, ct);
    }

    public async Task<RepositoryReviewResponse?> GetLatestReviewAsync(string repo, CancellationToken ct = default)
    {
        var run = await store.GetLatestRepositoryReviewRunAsync(repo);
        return run is null ? null : await BuildReviewResponseAsync(run, ct);
    }

    private async Task<RepositoryReviewResponse> BuildReviewResponseAsync(RepositoryReviewRunEntity run, CancellationToken ct)
    {
        var findings = await store.GetRepositoryReviewFindingsAsync(run.Id);
        var sections = await store.GetRepositoryReviewProjectSectionsAsync(run.Id);
        var overview = DeserializeOrDefault<StoredRepositoryReviewOverview>(run.OverviewJson)
            ?? new StoredRepositoryReviewOverview("", [], [], [], []);

        var responseFindings = findings.OrderBy(f => f.Ordinal).Select(MapFinding).ToList();
        var findingsByProject = responseFindings
            .Where(f => !string.IsNullOrWhiteSpace(f.ProjectName))
            .GroupBy(f => f.ProjectName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<RepositoryReviewFindingResponse>)g.ToList(),
                StringComparer.OrdinalIgnoreCase);

        var projectSections = sections
            .OrderBy(s => s.ProjectName, StringComparer.OrdinalIgnoreCase)
            .Select(section => new RepositoryReviewProjectSectionResponse(
                section.ProjectName,
                section.Overview,
                DeserializeOrDefault<IReadOnlyList<string>>(section.StrengthsJson) ?? [],
                DeserializeOrDefault<IReadOnlyList<string>>(section.ReviewedAreasJson) ?? [],
                DeserializeOrDefault<IReadOnlyList<string>>(section.SkippedAreasJson) ?? [],
                DeserializeOrDefault<IReadOnlyList<string>>(section.FollowUpsJson) ?? [],
                findingsByProject.GetValueOrDefault(section.ProjectName) ?? [],
                section.ReusedFromBaseline))
            .ToList();

        return new RepositoryReviewResponse(
            MapRun(run),
            overview.Overview,
            responseFindings,
            overview.Strengths,
            overview.ReviewedAreas,
            overview.SkippedAreas,
            overview.FollowUps,
            projectSections);
    }

    private async Task<IReadOnlyList<string>> GetProjectNamesAsync(string repo)
    {
        var analysisProjects = (await store.GetProjectAnalysesAsync(repo))
            .Select(a => a.ProjectName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (analysisProjects.Count > 0)
            return analysisProjects;

        var nodeProjects = (await store.GetNodeCountsByDotnetProjectAsync(repo)).Keys
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return nodeProjects;
    }

    private async Task<RepositoryReviewExecutionPlan> BuildExecutionPlanAsync(
        RepositoryReviewRunEntity run,
        string? repoRoot,
        IReadOnlyList<string> projectNames,
        RepositoryReviewResponse? baselineReview,
        CancellationToken ct)
    {
        if (!string.Equals(run.ReviewMode, "update", StringComparison.OrdinalIgnoreCase))
            return BuildFullReviewPlan(projectNames, run.BaselineCommitSha, run.ReviewedCommitSha, reason: null);

        if (baselineReview is null)
            return BuildFullReviewPlan(projectNames, run.BaselineCommitSha, run.ReviewedCommitSha, "No completed baseline repository review was available.");

        if (string.IsNullOrWhiteSpace(repoRoot))
            return BuildFullReviewPlan(projectNames, run.BaselineCommitSha, run.ReviewedCommitSha, "The repository root could not be resolved for diff collection.");

        if (string.IsNullOrWhiteSpace(run.BaselineCommitSha) || string.IsNullOrWhiteSpace(run.ReviewedCommitSha))
            return BuildFullReviewPlan(projectNames, run.BaselineCommitSha, run.ReviewedCommitSha, "The baseline or current commit SHA was missing.");

        var changedFiles = GetChangedFiles(repoRoot, run.BaselineCommitSha, run.ReviewedCommitSha);
        if (changedFiles is null)
            return BuildFullReviewPlan(projectNames, run.BaselineCommitSha, run.ReviewedCommitSha, "Git diff collection failed.");

        if (changedFiles.Count > MaxChangedFilesForIncrementalReview)
        {
            return BuildFullReviewPlan(
                projectNames,
                run.BaselineCommitSha,
                run.ReviewedCommitSha,
                $"The diff touched {changedFiles.Count} files, which exceeds the incremental review threshold.");
        }

        var relevantChanges = changedFiles
            .Where(change => !change.IsGenerated && !change.IsDocsOnly)
            .ToList();

        var baselineSections = baselineReview.ProjectReviews
            .ToDictionary(section => section.ProjectName, StringComparer.OrdinalIgnoreCase);

        if (relevantChanges.Count == 0)
        {
            return BuildScopedPlan(
                run.BaselineCommitSha,
                run.ReviewedCommitSha,
                changedFiles.Select(c => c.Path).ToList(),
                [],
                projectNames,
                baselineSections,
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, Dictionary<string, IReadOnlyList<ProjectReviewLineSpan>>>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));
        }

        if (relevantChanges.Any(change => change.IsBroadImpact))
        {
            var broadChange = relevantChanges.First(change => change.IsBroadImpact);
            return BuildFullReviewPlan(
                projectNames,
                run.BaselineCommitSha,
                run.ReviewedCommitSha,
                $"Changed file '{broadChange.Path}' has broad repository impact.");
        }

        var ownershipIndex = await BuildProjectOwnershipIndexAsync(run.Repo, ct);
        var changedFilesByProject = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var changedLineSpansByProject = new Dictionary<string, Dictionary<string, IReadOnlyList<ProjectReviewLineSpan>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in relevantChanges)
        {
            var mappedProject = TryMapChangedFileToProject(change.Path, ownershipIndex);
            if (mappedProject is null)
            {
                return BuildFullReviewPlan(
                    projectNames,
                    run.BaselineCommitSha,
                    run.ReviewedCommitSha,
                    $"Changed file '{change.Path}' could not be mapped to a single project.");
            }

            if (!changedFilesByProject.TryGetValue(mappedProject, out var files))
            {
                files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                changedFilesByProject[mappedProject] = files;
            }

            files.Add(change.Path);

            if (!changedLineSpansByProject.TryGetValue(mappedProject, out var spans))
            {
                spans = new Dictionary<string, IReadOnlyList<ProjectReviewLineSpan>>(StringComparer.OrdinalIgnoreCase);
                changedLineSpansByProject[mappedProject] = spans;
            }

            spans[change.Path] = change.ChangedLineSpans;
        }

        var blastRadiusExpansion = await BuildGraphBlastRadiusAsync(
            run.Repo,
            ownershipIndex,
            changedFilesByProject,
            changedLineSpansByProject,
            ct);

        var impactedProjects = blastRadiusExpansion.BlastRadiusFilesByProject.Keys
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (impactedProjects.Count == 0)
        {
            return BuildScopedPlan(
                run.BaselineCommitSha,
                run.ReviewedCommitSha,
                changedFiles.Select(c => c.Path).ToList(),
                [],
                projectNames,
                baselineSections,
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, Dictionary<string, IReadOnlyList<ProjectReviewLineSpan>>>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));
        }

        if (impactedProjects.Count == projectNames.Count ||
            impactedProjects.Count >= Math.Ceiling(projectNames.Count * MaxImpactedProjectRatioForIncrementalReview))
        {
            return BuildFullReviewPlan(
                projectNames,
                run.BaselineCommitSha,
                run.ReviewedCommitSha,
                $"The update impacts {impactedProjects.Count} of {projectNames.Count} projects.");
        }

        return BuildScopedPlan(
            run.BaselineCommitSha,
            run.ReviewedCommitSha,
            changedFiles.Select(c => c.Path).ToList(),
            impactedProjects,
            projectNames,
            baselineSections,
            changedFilesByProject,
            changedLineSpansByProject,
            blastRadiusExpansion.BlastRadiusFilesByProject);
    }

    private async Task<ProjectOwnershipIndex> BuildProjectOwnershipIndexAsync(string repo, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var nodes = await store.GetAllNodesByProjectAsync(repo);
        var metrics = await store.GetFileMetricsAsync(repo);

        var directOwners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var folderOwners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes)
        {
            AddOwnership(directOwners, node.FilePath, node.DotnetProject);
            AddOwnership(folderOwners, GetOwningFolder(node.FilePath), node.DotnetProject);
        }

        foreach (var metric in metrics)
        {
            AddOwnership(directOwners, metric.FilePath, metric.DotnetProject);
            AddOwnership(folderOwners, GetOwningFolder(metric.FilePath), metric.DotnetProject);
        }

        return new ProjectOwnershipIndex(directOwners, folderOwners);
    }

    private async Task<GraphBlastRadiusExpansion> BuildGraphBlastRadiusAsync(
        string repo,
        ProjectOwnershipIndex ownershipIndex,
        IReadOnlyDictionary<string, HashSet<string>> changedFilesByProject,
        IReadOnlyDictionary<string, Dictionary<string, IReadOnlyList<ProjectReviewLineSpan>>> changedLineSpansByProject,
        CancellationToken ct)
    {
        var blastRadiusFilesByProject = changedFilesByProject.ToDictionary(
            entry => entry.Key,
            entry => new HashSet<string>(entry.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        if (changedFilesByProject.Count == 0)
            return new GraphBlastRadiusExpansion(blastRadiusFilesByProject);

        ct.ThrowIfCancellationRequested();

        var nodes = await store.GetAllNodesByProjectAsync(repo);
        if (nodes.Count == 0)
            return new GraphBlastRadiusExpansion(blastRadiusFilesByProject);

        var edges = await store.GetAllEdgesByProjectAsync(repo);
        if (edges.Count == 0)
            return new GraphBlastRadiusExpansion(blastRadiusFilesByProject);

        var nodeById = nodes.ToDictionary(node => node.Id);
        var nodesByFile = nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.FilePath))
            .GroupBy(node => NormalizePath(node.FilePath))
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.OrdinalIgnoreCase);

        var edgesByNodeId = new Dictionary<long, List<EdgeEntity>>();
        foreach (var edge in edges.Where(edge => BlastRadiusEdgeTypes.Contains(edge.Type)))
        {
            AddEdgeLookup(edgesByNodeId, edge.SourceId, edge);
            AddEdgeLookup(edgesByNodeId, edge.TargetId, edge);
        }

        foreach (var (projectName, changedFiles) in changedFilesByProject)
        {
            var seedNodeIds = new HashSet<long>();
            var lineSpanMap = changedLineSpansByProject.TryGetValue(projectName, out var spans)
                ? spans
                : new Dictionary<string, IReadOnlyList<ProjectReviewLineSpan>>(StringComparer.OrdinalIgnoreCase);

            foreach (var filePath in changedFiles)
            {
                if (!nodesByFile.TryGetValue(filePath, out var fileNodes))
                    continue;

                var relevantNodes = SelectSeedNodesForFile(
                    fileNodes,
                    lineSpanMap.TryGetValue(filePath, out var fileSpans) ? fileSpans : []);
                foreach (var node in relevantNodes)
                    seedNodeIds.Add(node.Id);
            }

            if (seedNodeIds.Count == 0)
                continue;

            var visitedNodeIds = ExpandBlastRadiusNodeIds(seedNodeIds, edgesByNodeId, depth: 2);
            foreach (var nodeId in visitedNodeIds)
            {
                if (!nodeById.TryGetValue(nodeId, out var node) || string.IsNullOrWhiteSpace(node.FilePath))
                    continue;

                var relatedFile = NormalizePath(node.FilePath);
                if (string.IsNullOrWhiteSpace(relatedFile) || IsDocsOnlyPath(relatedFile) || IsGeneratedPath(relatedFile))
                    continue;

                var relatedProject = !string.IsNullOrWhiteSpace(node.DotnetProject)
                    ? node.DotnetProject
                    : TryMapChangedFileToProject(relatedFile, ownershipIndex);
                if (relatedProject is null)
                    continue;

                if (!blastRadiusFilesByProject.TryGetValue(relatedProject, out var files))
                {
                    files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    blastRadiusFilesByProject[relatedProject] = files;
                }

                files.Add(relatedFile);
            }
        }

        return new GraphBlastRadiusExpansion(blastRadiusFilesByProject);
    }

    private RepositoryReviewExecutionPlan BuildFullReviewPlan(
        IReadOnlyList<string> projectNames,
        string? baselineCommitSha,
        string? currentCommitSha,
        string? reason)
    {
        var projectPlans = projectNames.ToDictionary(
            projectName => projectName,
            projectName => new ProjectReviewExecutionPlan(
                projectName,
                ReuseBaselineSection: false,
                ReuseReason: null,
                SeedFiles: [],
                BlastRadiusFiles: [],
                ChangedLineSpans: new Dictionary<string, IReadOnlyList<ProjectReviewLineSpan>>(StringComparer.OrdinalIgnoreCase),
                CandidateTests: [],
                ReviewMode: "standard"),
            StringComparer.OrdinalIgnoreCase);

        return new RepositoryReviewExecutionPlan(
            Mode: "full",
            BaselineCommitSha: baselineCommitSha,
            CurrentCommitSha: currentCommitSha,
            UseFullReview: true,
            FullReviewReason: reason,
            ChangedFiles: [],
            ImpactedProjects: projectNames.ToList(),
            ProjectPlans: projectPlans);
    }

    private RepositoryReviewExecutionPlan BuildScopedPlan(
        string? baselineCommitSha,
        string? currentCommitSha,
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<string> impactedProjects,
        IReadOnlyList<string> projectNames,
        IReadOnlyDictionary<string, RepositoryReviewProjectSectionResponse> baselineSections,
        IReadOnlyDictionary<string, HashSet<string>> changedFilesByProject,
        IReadOnlyDictionary<string, Dictionary<string, IReadOnlyList<ProjectReviewLineSpan>>> changedLineSpansByProject,
        IReadOnlyDictionary<string, HashSet<string>> blastRadiusFilesByProject)
    {
        var impactedSet = impactedProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectPlans = new Dictionary<string, ProjectReviewExecutionPlan>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectName in projectNames)
        {
            if (impactedSet.Contains(projectName) || !baselineSections.ContainsKey(projectName))
            {
                var seedFiles = changedFilesByProject.TryGetValue(projectName, out var files)
                    ? files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList()
                    : [];
                var blastRadiusFiles = blastRadiusFilesByProject.TryGetValue(projectName, out var blastRadius)
                    ? blastRadius.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList()
                    : seedFiles;
                var candidateTests = seedFiles.Where(IsLikelyTestFile).ToList();

                projectPlans[projectName] = new ProjectReviewExecutionPlan(
                    projectName,
                    ReuseBaselineSection: false,
                    ReuseReason: null,
                    SeedFiles: seedFiles,
                    BlastRadiusFiles: blastRadiusFiles,
                    ChangedLineSpans: changedLineSpansByProject.TryGetValue(projectName, out var spans)
                        ? spans
                        : new Dictionary<string, IReadOnlyList<ProjectReviewLineSpan>>(StringComparer.OrdinalIgnoreCase),
                    CandidateTests: candidateTests,
                    ReviewMode: "update");
            }
            else
            {
                projectPlans[projectName] = new ProjectReviewExecutionPlan(
                    projectName,
                    ReuseBaselineSection: true,
                    ReuseReason: "No review-relevant changes were mapped to this project.",
                    SeedFiles: [],
                    BlastRadiusFiles: [],
                    ChangedLineSpans: new Dictionary<string, IReadOnlyList<ProjectReviewLineSpan>>(StringComparer.OrdinalIgnoreCase),
                    CandidateTests: [],
                    ReviewMode: "baseline");
            }
        }

        return new RepositoryReviewExecutionPlan(
            Mode: "update",
            BaselineCommitSha: baselineCommitSha,
            CurrentCommitSha: currentCommitSha,
            UseFullReview: false,
            FullReviewReason: null,
            ChangedFiles: changedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
            ImpactedProjects: impactedProjects,
            ProjectPlans: projectPlans);
    }

    private ProjectReviewResponse ReuseBaselineProjectReview(
        string repo,
        RepositoryReviewResponse baselineReview,
        RepositoryReviewProjectSectionResponse section)
    {
        var now = DateTime.UtcNow;
        return new ProjectReviewResponse(
            new ProjectReviewRunResponse(
                0,
                repo,
                section.ProjectName,
                baselineReview.Run.ReviewedCommitSha,
                "completed",
                "baseline",
                baselineReview.Run.PromptVersion,
                baselineReview.Run.ModelUsed,
                now,
                now,
                now,
                null),
            section.Overview,
            section.Findings.Select(finding => new ProjectReviewFindingResponse(
                finding.Severity,
                finding.Category,
                finding.Title,
                finding.Explanation,
                finding.Evidence,
                finding.FilePath,
                finding.LineStart,
                finding.LineEnd,
                finding.SuggestedImprovement,
                finding.Confidence)).ToList(),
            section.Strengths,
            section.ReviewedAreas,
            section.SkippedAreas,
            section.FollowUps);
    }

    private IReadOnlyList<ChangedFilePlanInput>? GetChangedFiles(string repoRoot, string baselineCommitSha, string currentCommitSha)
    {
        try
        {
            var changedLineSpans = GetChangedLineSpans(repoRoot, baselineCommitSha, currentCommitSha);
            if (changedLineSpans is null)
                return null;

            var psi = new ProcessStartInfo(
                "git",
                $"diff --name-status --find-renames {baselineCommitSha} {currentCommitSha}")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                return null;

            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => ParseChangedFile(line, changedLineSpans))
                .Where(change => change is not null)
                .Cast<ChangedFilePlanInput>()
                .GroupBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(change => change.IsDeleted).First())
                .OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to collect changed files for repository review repo path {RepoPath}", repoRoot);
            return null;
        }
    }

    private Dictionary<string, IReadOnlyList<ProjectReviewLineSpan>>? GetChangedLineSpans(
        string repoRoot,
        string baselineCommitSha,
        string currentCommitSha)
    {
        try
        {
            var psi = new ProcessStartInfo(
                "git",
                $"diff --unified=0 --no-color {baselineCommitSha} {currentCommitSha}")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                return null;

            return ParseChangedLineSpans(output);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to collect changed line spans for repository review repo path {RepoPath}", repoRoot);
            return null;
        }
    }

    private static Dictionary<string, IReadOnlyList<ProjectReviewLineSpan>> ParseChangedLineSpans(string diffText)
    {
        var result = new Dictionary<string, List<ProjectReviewLineSpan>>(StringComparer.OrdinalIgnoreCase);
        string? currentPath = null;

        foreach (var rawLine in diffText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                currentPath = line[4..].StartsWith("b/", StringComparison.Ordinal)
                    ? NormalizePath(line[6..])
                    : null;
                continue;
            }

            if (currentPath is null || !line.StartsWith("@@ ", StringComparison.Ordinal))
                continue;

            var match = DiffHunkRegex.Match(line);
            if (!match.Success)
                continue;

            var start = int.Parse(match.Groups["newStart"].Value);
            var length = match.Groups["newCount"].Success ? int.Parse(match.Groups["newCount"].Value) : 1;
            var end = length == 0 ? start : start + Math.Max(0, length - 1);

            if (!result.TryGetValue(currentPath, out var spans))
            {
                spans = [];
                result[currentPath] = spans;
            }

            spans.Add(new ProjectReviewLineSpan(Math.Max(1, start), Math.Max(1, end)));
        }

        return result.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<ProjectReviewLineSpan>)MergeLineSpans(entry.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private ChangedFilePlanInput? ParseChangedFile(
        string line,
        IReadOnlyDictionary<string, IReadOnlyList<ProjectReviewLineSpan>> changedLineSpans)
    {
        var parts = line.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        var status = parts[0].Trim();
        var path = status.StartsWith("R", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3
            ? parts[2]
            : parts[1];
        var normalizedPath = NormalizePath(path);

        return new ChangedFilePlanInput(
            normalizedPath,
            status,
            IsDeleted: status.StartsWith("D", StringComparison.OrdinalIgnoreCase),
            IsDocsOnly: IsDocsOnlyPath(normalizedPath),
            IsGenerated: IsGeneratedPath(normalizedPath),
            IsBroadImpact: IsBroadImpactPath(normalizedPath),
            ChangedLineSpans: changedLineSpans.TryGetValue(normalizedPath, out var spans)
                ? spans
                : []);
    }

    private string? TryMapChangedFileToProject(string filePath, ProjectOwnershipIndex ownershipIndex)
    {
        var normalizedPath = NormalizePath(filePath);
        if (ownershipIndex.DirectOwners.TryGetValue(normalizedPath, out var directOwners) && directOwners.Count == 1)
            return directOwners.First();

        var folder = GetOwningFolder(normalizedPath);
        while (!string.IsNullOrEmpty(folder))
        {
            if (ownershipIndex.FolderOwners.TryGetValue(folder, out var folderOwners) && folderOwners.Count == 1)
                return folderOwners.First();

            var slashIndex = folder.LastIndexOf('/');
            folder = slashIndex >= 0 ? folder[..slashIndex] : "";
        }

        return null;
    }

    private static ProjectReviewBaselineContext? BuildBaselineContext(
        RepositoryReviewProjectSectionResponse? section)
        => section is null
            ? null
            : new ProjectReviewBaselineContext(
                section.Overview,
                section.Strengths,
                section.ReviewedAreas,
                section.FollowUps,
                section.Findings.Take(4)
                    .Select(finding => new ProjectReviewFindingResponse(
                        finding.Severity,
                        finding.Category,
                        finding.Title,
                        finding.Explanation,
                        finding.Evidence,
                        finding.FilePath,
                        finding.LineStart,
                        finding.LineEnd,
                        finding.SuggestedImprovement,
                        finding.Confidence))
                    .ToList());

    private static string BuildProjectUpdateSummary(ProjectReviewExecutionPlan plan)
    {
        var changedFiles = plan.SeedFiles.Count == 0 ? "no directly mapped source files" : string.Join(", ", plan.SeedFiles.Take(4));
        return $"Focus on changed files {changedFiles} and their nearby blast-radius context.";
    }

    private static void AddEdgeLookup(IDictionary<long, List<EdgeEntity>> lookup, long nodeId, EdgeEntity edge)
    {
        if (!lookup.TryGetValue(nodeId, out var edges))
        {
            edges = [];
            lookup[nodeId] = edges;
        }

        edges.Add(edge);
    }

    private static IReadOnlyList<NodeEntity> SelectSeedNodesForFile(
        IReadOnlyList<NodeEntity> fileNodes,
        IReadOnlyList<ProjectReviewLineSpan> changedLineSpans)
    {
        var semanticNodes = fileNodes
            .Where(node => !string.IsNullOrWhiteSpace(node.Label) &&
                           !string.Equals(node.Label, "File", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (changedLineSpans.Count == 0)
            return semanticNodes.Count > 0 ? semanticNodes : fileNodes;

        var overlapping = semanticNodes
            .Where(node => changedLineSpans.Any(span => NodeOverlapsSpan(node, span)))
            .ToList();
        if (overlapping.Count > 0)
            return overlapping;

        return semanticNodes.Count > 0 ? semanticNodes : fileNodes;
    }

    private static bool NodeOverlapsSpan(NodeEntity node, ProjectReviewLineSpan span)
    {
        if (node.StartLine <= 0 || node.EndLine <= 0)
            return true;

        return node.StartLine <= span.EndLine && node.EndLine >= span.StartLine;
    }

    private static HashSet<long> ExpandBlastRadiusNodeIds(
        IReadOnlySet<long> seedNodeIds,
        IReadOnlyDictionary<long, List<EdgeEntity>> edgesByNodeId,
        int depth)
    {
        var visited = new HashSet<long>(seedNodeIds);
        var frontier = new HashSet<long>(seedNodeIds);

        for (var hop = 0; hop < depth && frontier.Count > 0; hop++)
        {
            var next = new HashSet<long>();
            foreach (var nodeId in frontier)
            {
                if (!edgesByNodeId.TryGetValue(nodeId, out var edges))
                    continue;

                foreach (var edge in edges)
                {
                    var otherNodeId = edge.SourceId == nodeId ? edge.TargetId : edge.SourceId;
                    if (visited.Add(otherNodeId))
                        next.Add(otherNodeId);
                }
            }

            frontier = next;
        }

        return visited;
    }

    private static IReadOnlyList<ProjectReviewLineSpan> MergeLineSpans(IReadOnlyList<ProjectReviewLineSpan> spans)
    {
        if (spans.Count == 0)
            return [];

        var ordered = spans
            .Where(span => span.StartLine > 0 && span.EndLine >= span.StartLine)
            .OrderBy(span => span.StartLine)
            .ToList();
        if (ordered.Count == 0)
            return [];

        var merged = new List<ProjectReviewLineSpan> { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var current = ordered[i];
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

    private static void AddOwnership(
        IDictionary<string, HashSet<string>> owners,
        string? key,
        string? projectName)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(projectName))
            return;

        var normalizedKey = NormalizePath(key);
        if (!owners.TryGetValue(normalizedKey, out var values))
        {
            values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            owners[normalizedKey] = values;
        }

        values.Add(projectName);
    }

    private static string GetOwningFolder(string? filePath)
    {
        var normalizedPath = NormalizePath(filePath);
        var slashIndex = normalizedPath.LastIndexOf('/');
        return slashIndex <= 0 ? "" : normalizedPath[..slashIndex];
    }

    private static bool IsDocsOnlyPath(string path)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".rst", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.Contains("/docs/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "readme", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "readme.md", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeneratedPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBroadImpactPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return string.Equals(Path.GetExtension(path), ".sln", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Path.GetExtension(path), ".slnx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "Directory.Build.targets", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "packages.lock.json", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "global.json", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "nuget.config", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "Program.cs", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "Startup.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyTestFile(string path)
        => path.Contains("/test", StringComparison.OrdinalIgnoreCase) ||
           path.Contains("/tests", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? ""
            : path.Replace('\\', '/').TrimStart('/');

    private async Task<RepositoryReviewSummaryModel> SynthesizeRepositoryReviewAsync(
        string repo,
        string mode,
        string? reviewedCommitSha,
        RepositoryReviewResponse? baselineReview,
        RepositoryReviewExecutionPlan executionPlan,
        IReadOnlyList<RepositoryProjectReviewSectionResult> projectSections,
        LlmReviewRuntimeConfig reviewSettings,
        CancellationToken ct)
    {
        var topFindings = projectSections
            .SelectMany(section => section.Review.Findings.Select(f => new
            {
                projectName = section.Review.Run.ProjectName,
                reusedFromBaseline = section.ReusedFromBaseline,
                f.Severity,
                f.Category,
                f.Title,
                f.Explanation,
                f.Evidence,
                f.FilePath,
                f.LineStart,
                f.LineEnd,
                f.SuggestedImprovement,
                f.Confidence
            }))
            .OrderBy(f => SeverityRank(f.Severity))
            .ThenBy(f => f.projectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Cast<object>()
            .ToList();

        var reducedProjectReviews = projectSections
            .Select(section => (object)new
            {
                projectName = section.Review.Run.ProjectName,
                reusedFromBaseline = section.ReusedFromBaseline,
                reuseReason = section.ReuseReason,
                seedFiles = executionPlan.ProjectPlans[section.Review.Run.ProjectName].SeedFiles,
                section.Review.Overview,
                section.Review.Strengths,
                section.Review.ReviewedAreas,
                section.Review.SkippedAreas,
                section.Review.FollowUps,
                findingCount = section.Review.Findings.Count,
                findings = section.Review.Findings.Take(8).ToList()
            })
            .ToList();

        try
        {
            var provider = providerRegistry.GetProvider(reviewSettings.DefaultProvider);
            var prompt = RepositoryReviewSynthesisPromptBuilder.Build(
                repo,
                mode,
                reviewedCommitSha,
                baselineReview,
                executionPlan,
                reducedProjectReviews,
                topFindings);

            var response = await provider.ExecuteAsync(
                new AnalysisPrompt(
                    await GetRepositoryReviewSynthesisSystemPromptAsync(),
                    prompt),
                new AnalysisRequestOptions(
                    Model: string.IsNullOrWhiteSpace(reviewSettings.DefaultModel) ? null : reviewSettings.DefaultModel,
                    MaxTokens: Math.Min(analysisOptions.MaxTokensPerSynthesis, 4_000)),
                ct);

            var model = DeserializeOrThrow<RepositoryReviewSynthesisModel>(response.Text);
            return NormalizeSummary(new RepositoryReviewSummaryModel(
                model.Overview ?? "",
                NormalizeStringList(model.Strengths),
                NormalizeStringList(model.ReviewedAreas),
                NormalizeStringList(model.SkippedAreas),
                NormalizeStringList(model.FollowUps)),
                projectSections);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Repository review synthesis failed, falling back to deterministic summary");
            return BuildDeterministicSummary(repo, mode, reviewedCommitSha, executionPlan, projectSections);
        }
    }

    private Task<string> GetRepositoryReviewSynthesisSystemPromptAsync()
        => AgentPromptExecution.GetEffectivePromptOrDefaultAsync(
            agentPromptService,
            AgentPromptCatalog.RepositoryReviewSynthesisSystemPromptKey,
            RepositoryReviewSynthesisPromptBuilder.SystemPrompt,
            logger,
            "repository review synthesis");

    private Task<LlmReviewRuntimeConfig> GetReviewSettingsAsync(CancellationToken ct) =>
        reviewSettingsResolver is null
            ? Task.FromResult(LlmReviewRuntimeConfig.FromOptions(analysisOptions))
            : reviewSettingsResolver.GetReviewAsync(ct);

    private static RepositoryReviewSummaryModel BuildDeterministicSummary(
        string repo,
        string mode,
        string? reviewedCommitSha,
        RepositoryReviewExecutionPlan executionPlan,
        IReadOnlyList<RepositoryProjectReviewSectionResult> projectSections)
    {
        var findingCount = projectSections.Sum(r => r.Review.Findings.Count);
        var projectCount = projectSections.Count;
        var reusedCount = projectSections.Count(section => section.ReusedFromBaseline);
        var riskyProjects = projectSections
            .Where(r => r.Review.Findings.Count > 0)
            .Select(r => r.Review.Run.ProjectName)
            .ToList();

        string overview;
        if (findingCount == 0 && string.Equals(mode, "update", StringComparison.OrdinalIgnoreCase) && executionPlan.ChangedFiles.Count == 0)
        {
            overview = $"Compared {reviewedCommitSha ?? "the current commit"} against the baseline review for {repo} and found no review-relevant code changes, so the prior project sections were carried forward.";
        }
        else if (findingCount == 0)
        {
            overview = $"Reviewed {projectCount} projects in {repo} and did not identify any evidence-backed findings in the inspected areas.";
        }
        else if (string.Equals(mode, "update", StringComparison.OrdinalIgnoreCase) && !executionPlan.UseFullReview)
        {
            overview = $"Updated the review for {repo} by rerunning {executionPlan.ImpactedProjects.Count} impacted projects and reusing {reusedCount} unchanged sections. The refreshed findings are concentrated in {string.Join(", ", riskyProjects.Take(3))}.";
        }
        else
        {
            overview = $"Reviewed {projectCount} projects in {repo} and found {findingCount} evidence-backed issues across the highest-risk inspected areas. The main concentration of findings is in {string.Join(", ", riskyProjects.Take(3))}.";
        }

        return NormalizeSummary(new RepositoryReviewSummaryModel(
            overview,
            projectSections.SelectMany(r => r.Review.Strengths).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList(),
            projectSections.Select(r => r.Review.Run.ProjectName).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList(),
            projectSections.SelectMany(r => r.Review.SkippedAreas).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList(),
            findingCount == 0
                ? ["Spot-check lower-priority files if you want broader confidence beyond the inspected set."]
                : projectSections.SelectMany(r => r.Review.FollowUps).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList()),
            projectSections);
    }

    private static RepositoryReviewSummaryModel NormalizeSummary(
        RepositoryReviewSummaryModel summary,
        IReadOnlyList<RepositoryProjectReviewSectionResult> projectSections)
    {
        var reviewedAreas = NormalizeStringList(summary.ReviewedAreas).Take(6).ToList();
        if (reviewedAreas.Count == 0)
        {
            reviewedAreas = projectSections
                .Select(r => r.Review.Run.ProjectName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
        }

        return summary with
        {
            Overview = ShortenText(summary.Overview, 360),
            Strengths = NormalizeStringList(summary.Strengths).Take(4).ToList(),
            ReviewedAreas = reviewedAreas,
            SkippedAreas = NormalizeStringList(summary.SkippedAreas).Take(3).ToList(),
            FollowUps = NormalizeStringList(summary.FollowUps).Take(4).ToList()
        };
    }

    private static RepositoryReviewRunResponse MapRun(RepositoryReviewRunEntity run)
        => new(
            run.Id,
            run.Repo,
            run.ReviewedCommitSha,
            run.BaselineReviewRunId,
            run.BaselineCommitSha,
            run.Status,
            run.ReviewMode,
            run.PromptVersion,
            run.ModelUsed,
            run.CreatedAt,
            run.StartedAt,
            run.CompletedAt,
            run.Error);

    private static RepositoryReviewFindingResponse MapFinding(RepositoryReviewFindingEntity finding)
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
            finding.Confidence,
            finding.ProjectName);

    private static int SeverityRank(string? severity) => severity?.Trim().ToLowerInvariant() switch
    {
        "critical" => 0,
        "high" => 1,
        "medium" => 2,
        "low" => 3,
        _ => 4
    };

    private static string ProjectNameSortKey(string? projectName) => projectName ?? "~";

    private static bool IsTerminalStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeMode(string mode)
        => mode.Trim().ToLowerInvariant() switch
        {
            "update" => "update",
            _ => "full"
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
            logger.LogDebug(ex, "Failed to get HEAD commit SHA for repository review repo path {RepoPath}", repoPath);
            return null;
        }
    }

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

    private static string ShortenText(string? value, int maxChars)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? ""
            : string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= maxChars)
            return normalized;

        var truncated = normalized[..Math.Min(maxChars, normalized.Length)].TrimEnd();
        var lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace >= maxChars / 2)
            truncated = truncated[..lastSpace].TrimEnd();
        return truncated + "...";
    }

    private sealed record RepositoryReviewSynthesisModel(
        string? Overview,
        IReadOnlyList<string>? Strengths,
        IReadOnlyList<string>? ReviewedAreas,
        IReadOnlyList<string>? SkippedAreas,
        IReadOnlyList<string>? FollowUps);

    private sealed record RepositoryReviewSummaryModel(
        string Overview,
        IReadOnlyList<string> Strengths,
        IReadOnlyList<string> ReviewedAreas,
        IReadOnlyList<string> SkippedAreas,
        IReadOnlyList<string> FollowUps);

    private sealed record RepositoryReviewExecutionPlan(
        string Mode,
        string? BaselineCommitSha,
        string? CurrentCommitSha,
        bool UseFullReview,
        string? FullReviewReason,
        IReadOnlyList<string> ChangedFiles,
        IReadOnlyList<string> ImpactedProjects,
        IReadOnlyDictionary<string, ProjectReviewExecutionPlan> ProjectPlans);

    private sealed record ProjectReviewExecutionPlan(
        string ProjectName,
        bool ReuseBaselineSection,
        string? ReuseReason,
        IReadOnlyList<string> SeedFiles,
        IReadOnlyList<string> BlastRadiusFiles,
        IReadOnlyDictionary<string, IReadOnlyList<ProjectReviewLineSpan>> ChangedLineSpans,
        IReadOnlyList<string> CandidateTests,
        string ReviewMode);

    private sealed record RepositoryProjectReviewSectionResult(
        ProjectReviewResponse Review,
        bool ReusedFromBaseline,
        string? ReuseReason);

    private sealed record ChangedFilePlanInput(
        string Path,
        string Status,
        bool IsDeleted,
        bool IsDocsOnly,
        bool IsGenerated,
        bool IsBroadImpact,
        IReadOnlyList<ProjectReviewLineSpan> ChangedLineSpans);

    private sealed record ProjectOwnershipIndex(
        IReadOnlyDictionary<string, HashSet<string>> DirectOwners,
        IReadOnlyDictionary<string, HashSet<string>> FolderOwners);

    private sealed record GraphBlastRadiusExpansion(
        IReadOnlyDictionary<string, HashSet<string>> BlastRadiusFilesByProject);

    private sealed record StoredRepositoryReviewOverview(
        string Overview,
        IReadOnlyList<string> Strengths,
        IReadOnlyList<string> ReviewedAreas,
        IReadOnlyList<string> SkippedAreas,
        IReadOnlyList<string> FollowUps);
}
