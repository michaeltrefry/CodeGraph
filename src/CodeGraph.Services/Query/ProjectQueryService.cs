using CodeGraph.Data;
using Microsoft.Extensions.Options;
using CodeGraph.Models;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extensions;
using CodeGraph.Services.Metadata;

namespace CodeGraph.Services.Query;

public class ProjectQueryService(
    IGraphStore store,
    IOptions<RepositorySourceOptions> sourceOptionsAccessor) : IProjectQueryService
{
    private readonly RepositorySourceOptions sourceOptions = sourceOptionsAccessor.Value;
    public async Task<ProjectListResponse> ListAsync(string? search, string? group, int page, int pageSize)
    {
        // Run filtered query and group list in parallel — both at the store layer
        var repoTask = store.SearchRepositoriesAsync(search, group, page, pageSize);
        var groupsTask = store.GetDistinctGroupsAsync();

        await Task.WhenAll(repoTask, groupsTask);

        var result = await repoTask;
        var groups = (await groupsTask).ToList();

        var items = result.Items.Select(MapProjectListItem).ToList();

        return new ProjectListResponse(items, result.TotalCount, page, pageSize, groups);
    }

    public async Task<ProjectDetailResponse?> GetDetailAsync(string name)
    {
        var project = await store.GetRepositoryByName(name);

        if (project is null)
            return null;

        var summary = await store.GetRepositorySummaryAsync(name);
        var analyses = await store.GetProjectAnalysesAsync(name);

        var dotnetProjects = await store.GetNodeCountsByDotnetProjectAsync(name);
        var nodeCounts = await store.GetNodeCountsByLabelForProjectAsync(name);

        var healthSummaries = await store.GetProjectHealthSummariesAsync(name);
        var repoHealth = healthSummaries.FirstOrDefault(h => string.IsNullOrEmpty(h.DotnetProject));

        var crossRepoEdges = await store.FindCrossRepoEdgesAsync(name);

        var inboundCount = crossRepoEdges.Count(e => e.TargetProject == name);
        var outboundCount = crossRepoEdges.Count(e => e.SourceProject == name);
        var inboundProjects = crossRepoEdges
            .Where(e => e.TargetProject == name)
            .Select(e => e.SourceProject)
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        var outboundProjects = crossRepoEdges
            .Where(e => e.SourceProject == name)
            .Select(e => e.TargetProject)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        return new ProjectDetailResponse(
            MapProjectListItem(project),
            ResolveDotnetSupport(project),
            MapSummary(summary),
            analyses.Select(MapAnalysis).ToList(),
            nodeCounts,
            dotnetProjects,
            inboundCount,
            outboundCount,
            inboundProjects,
            outboundProjects,
            MapHealthSummary(repoHealth));
    }

    public async Task<ProjectHealthResponse?> GetHealthAsync(string name)
    {
        var summaries = await store.GetProjectHealthSummariesAsync(name);
        if (summaries.Count == 0)
            return null;

        var repoSummary = summaries.FirstOrDefault(s => string.IsNullOrEmpty(s.DotnetProject));
        var projectSummaries = summaries.Where(s => !string.IsNullOrEmpty(s.DotnetProject)).ToList();
        var hotspots = await store.GetHotspotsAsync(name, 10);
        var analyses = await store.GetProjectHealthAnalysesAsync(name);

        var secSummary = await store.GetProjectSecuritySummaryAsync(name);

        return new ProjectHealthResponse(
            MapHealthSummary(repoSummary),
            projectSummaries.Select(MapHealthSummary).ToList()!,
            hotspots.Select(MapFileMetrics).ToList(),
            analyses.Select(MapHealthAnalysis).ToList(),
            secSummary is not null ? MapSecuritySummary(secSummary) : null);
    }

    public async Task<IReadOnlyList<FileMetrics>> GetMetricsAsync(string name, string? dotnetProject, int top)
    {
        var metrics = await store.GetFileMetricsAsync(name, dotnetProject);
        return metrics
            .OrderByDescending(m => m.RiskScore)
            .Take(top)
            .Select(MapFileMetrics)
            .ToList();
    }

    public async Task<IReadOnlyList<FileMetrics>> GetHotspotsAsync(string name, int top)
    {
        var hotspots = await store.GetHotspotsAsync(name, top);
        return hotspots.Select(MapFileMetrics).ToList();
    }

    public async Task<NodeListResponse> GetNodesAsync(string name, string? label, string? dotnetProject, int page, int pageSize)
    {
        var parsedLabel = label.TryParseEnum<NodeLabel>();

        var nodesTask = store.SearchNodesAsync(name, "%",
            label: parsedLabel,
            limit: pageSize,
            offset: (page - 1) * pageSize,
            dotnetProject: dotnetProject);

        var countTask = store.SearchNodesCountAsync(name, "%",
            label: parsedLabel, dotnetProject: dotnetProject);

        await Task.WhenAll(nodesTask, countTask);

        var items = (await nodesTask).OrderBy(n => n.Name).ToList();
        var total = await countTask;

        return new NodeListResponse(items, total, page, pageSize);
    }

    public async Task<AnalysisBatchResponse?> GetBatchStatusAsync(string name)
    {
        var batch = await store.GetLatestBatchAsync(name);
        return batch is null ? null : MapBatch(batch);
    }

    public async Task<ProjectSecurityResponse?> GetSecurityAsync(string name)
    {
        var summary = await store.GetProjectSecuritySummaryAsync(name);
        if (summary is null) return null;

        var findings = await store.GetSecurityFindingsAsync(name);
        var mapped = findings.Select(f => new SecurityFinding(
            f.Category, f.Severity, f.Title, f.Description,
            f.FilePath, f.LineNumber, f.Package, f.PackageVersion, f.Advisory)).ToList();

        return new ProjectSecurityResponse(
            name, summary.SecurityScore, summary.CriticalCount, summary.HighCount,
            summary.MediumCount, summary.LowCount, mapped, summary.Analysis, summary.ComputedAt);
    }

    public async Task<string?> GetReadmeAsync(string name)
    {
        var filePath = await RepoFileResolver.ResolveAsync(name, "README.md", sourceOptions, store);
        if (filePath is null)
            return null;

        return await File.ReadAllTextAsync(filePath);
    }

    // --- Mapping helpers ---

    internal static ProjectListItem MapProjectListItem(ProjectInfo p) =>
        new(p.Name, p.RepoUrl, p.SourceGroup, p.LocalPath, p.LastCommitSha, p.IndexedAt,
            p.Language, p.Framework, p.IsFoundational, p.Properties);

    internal static DotnetSupportInfo? ResolveDotnetSupport(ProjectInfo p) =>
        DotnetSupportInspector.TryReadStoredSupport(p.Properties) ??
        DotnetSupportInspector.InspectRepository(p.LocalPath);

    internal static ProjectSummaryResponse? MapSummary(ProjectSummary? s) =>
        s is null ? null : new ProjectSummaryResponse(
            s.Project, s.Summary, s.Confidence.ToString(), s.SourceHash,
            s.ModelUsed, s.CreatedAt, s.UpdatedAt);

    internal static ProjectAnalysisResponse MapAnalysis(StoredProjectAnalysis a) =>
        new(a.Repo, a.ProjectName, a.Summary, a.Confidence.ToString(),
            a.Endpoints.Select(e => new EndpointResponse(e.Route, e.HttpMethod, e.Description, e.RequestModel, e.ResponseModel)).ToList(),
            a.Services.Select(s => new ServiceResponse(s.Name, s.Description, s.InterfaceName, s.Lifetime)).ToList(),
            a.ExternalDependencies.ToList(),
            a.DatabaseTables.ToList(),
            a.ModelUsed, a.UpdatedAt);

    internal static ProjectHealthSummary? MapHealthSummary(ProjectHealthSummaryEntity? e) =>
        e is null ? null : new ProjectHealthSummary(
            e.Id, e.Project, e.DotnetProject, e.OverallHealth, e.TotalFiles,
            e.HotspotCount, e.AlertCount, e.TopHotspots, e.ComputedAt);

    internal static FileMetrics MapFileMetrics(FileMetricsEntity e) =>
        new(e.Id, e.Project, e.FilePath, e.DotnetProject,
            e.Changes, e.LinesAdded, e.LinesRemoved, e.AuthorCount, e.LastChangeAt,
            e.ComplexityScore, e.MaxNestingDepth, e.DeepNestingLines, e.FunctionCount, e.LongestFunction,
            e.LintErrors, e.LintWarnings, e.TrustScore,
            e.MaxCouplingStrength, e.CouplingPartners,
            e.TruckFactor, e.TopAuthors,
            e.HealthScore, e.Role, e.RiskScore, e.ComputedAt);

    internal static ProjectHealthAnalysis MapHealthAnalysis(ProjectHealthAnalysisEntity e) =>
        new(e.Id, e.Project, e.DotnetProject, e.Analysis, e.Confidence, e.ModelUsed, e.CreatedAt, e.UpdatedAt);

    internal static AnalysisBatchResponse MapBatch(StoredAnalysisBatch b) =>
        new(b.Id, b.Repo, b.ProviderBatchId, b.ProviderName, b.ExecutionMode, b.IncludeAllSource,
            b.Status, b.RequestCount, b.CompletedCount, b.SubmittedAt, b.CompletedAt);

    internal static ProjectSecuritySummary MapSecuritySummary(ProjectSecuritySummaryEntity e) =>
        new(e.SecurityScore, e.CriticalCount, e.HighCount, e.MediumCount, e.LowCount, e.ComputedAt);
}
