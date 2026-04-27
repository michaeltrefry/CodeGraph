using CodeGraph.Data;
using Microsoft.Extensions.Options;
using CodeGraph.Models;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extensions;
using CodeGraph.Services.Metadata;
using System.Text.Json;

namespace CodeGraph.Services.Query;

public class ProjectQueryService(
    IGraphStore store,
    IOptions<RepositorySourceOptions> sourceOptionsAccessor) : IProjectQueryService
{
    private readonly RepositorySourceOptions sourceOptions = sourceOptionsAccessor.Value;
    public async Task<ProjectListResponse> ListAsync(string? search, string? group, int page, int pageSize)
    {
        var result = await store.SearchRepositoriesAsync(search, group, page, pageSize);
        var groups = (await store.GetDistinctGroupsAsync()).ToList();

        var items = result.Items.Select(MapProjectListItem).ToList();

        return new ProjectListResponse(items, result.TotalCount, page, pageSize, groups);
    }

    public async Task<SchemaListResponse> ListSchemasAsync(string? search, string? server, string? database, int page, int pageSize)
    {
        var repositories = await store.ListRepositoriesAsync();
        var schemas = repositories
            .Where(IsDatabaseSchemaProject)
            .Select(project => new SchemaProject(
                project,
                GetStringProperty(project.Properties, "serverName") ?? project.SourceGroup ?? "",
                GetStringProperty(project.Properties, "databaseName") ?? GetDatabaseNameFromProject(project.Name)))
            .Where(x => !string.IsNullOrWhiteSpace(x.ServerName) && !string.IsNullOrWhiteSpace(x.DatabaseName))
            .ToList();

        var filtered = schemas.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(server))
            filtered = filtered.Where(x => x.ServerName.Equals(server, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(database))
            filtered = filtered.Where(x => x.DatabaseName.Equals(database, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(x =>
                x.Project.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                x.ServerName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                x.DatabaseName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var filteredList = filtered
            .OrderBy(x => x.ServerName)
            .ThenBy(x => x.DatabaseName)
            .ToList();

        var countsByProject = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in filteredList)
            countsByProject[schema.Project.Name] = await store.GetNodeCountsByLabelForProjectAsync(schema.Project.Name);

        var totalTables = countsByProject.Values.Sum(counts => GetLabelCount(counts, NodeLabel.Table));
        var totalViews = countsByProject.Values.Sum(counts => GetLabelCount(counts, NodeLabel.View));
        var totalProcedures = countsByProject.Values.Sum(counts => GetLabelCount(counts, NodeLabel.StoredProcedure));

        var items = filteredList
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x =>
            {
                countsByProject.TryGetValue(x.Project.Name, out var counts);

                return new SchemaListItem(
                    x.Project.Name,
                    x.ServerName,
                    x.DatabaseName,
                    GetLabelCount(counts, NodeLabel.Table),
                    GetLabelCount(counts, NodeLabel.View),
                    GetLabelCount(counts, NodeLabel.StoredProcedure),
                    x.Project.IndexedAt,
                    x.Project.Language,
                    x.Project.Framework,
                    x.Project.Properties);
            })
            .ToList();

        var serverOptions = schemas
            .Select(x => x.ServerName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var databaseOptions = schemas
            .Where(x => string.IsNullOrWhiteSpace(server) || x.ServerName.Equals(server, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.DatabaseName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SchemaListResponse(
            items,
            filteredList.Count,
            totalTables,
            totalViews,
            totalProcedures,
            page,
            pageSize,
            serverOptions,
            databaseOptions);
    }

    public async Task<SchemaCatalogResponse?> GetSchemaCatalogAsync(string name)
    {
        var project = await store.GetRepositoryByName(name);
        if (project is null || !IsDatabaseSchemaProject(project))
            return null;

        var serverName = GetStringProperty(project.Properties, "serverName") ?? project.SourceGroup ?? "";
        var databaseName = GetStringProperty(project.Properties, "databaseName") ?? GetDatabaseNameFromProject(project.Name);

        var tables = await store.FindNodesByLabelAsync(name, NodeLabel.Table);
        var views = await store.FindNodesByLabelAsync(name, NodeLabel.View);
        var procedures = await store.FindNodesByLabelAsync(name, NodeLabel.StoredProcedure);
        var allObjects = tables.Concat(views).Concat(procedures).ToList();
        var nodesById = allObjects.ToDictionary(node => node.Id);

        var tableResponses = new List<SchemaObjectResponse>();
        foreach (var table in tables.OrderBy(node => node.Name))
            tableResponses.Add(await MapSchemaObjectAsync(table, nodesById));

        var viewResponses = new List<SchemaObjectResponse>();
        foreach (var view in views.OrderBy(node => node.Name))
            viewResponses.Add(await MapSchemaObjectAsync(view, nodesById));

        var procedureResponses = procedures
            .OrderBy(node => node.Name)
            .Select(MapSchemaProcedure)
            .ToList();

        return new SchemaCatalogResponse(name, serverName, databaseName, tableResponses, viewResponses, procedureResponses);
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
        var dotnetSupport = ResolveDotnetSupport(project);

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
            dotnetSupport,
            MapSummary(summary),
            analyses.Select(MapAnalysis).ToList(),
            nodeCounts,
            dotnetProjects,
            inboundCount,
            outboundCount,
            inboundProjects,
            outboundProjects,
            MapHealthSummary(repoHealth, dotnetSupport));
    }

    public async Task<ProjectHealthResponse?> GetHealthAsync(string name)
    {
        var project = await store.GetRepositoryByName(name);
        if (project is null)
            return null;

        var summaries = await store.GetProjectHealthSummariesAsync(name);
        var dotnetSupport = ResolveDotnetSupport(project);
        var secSummary = await store.GetProjectSecuritySummaryAsync(name);

        if (summaries.Count == 0)
        {
            if (dotnetSupport is null && secSummary is null)
                return null;

            return new ProjectHealthResponse(
                null,
                [],
                [],
                [],
                secSummary is not null ? MapSecuritySummary(secSummary) : null,
                dotnetSupport,
                null);
        }

        var repoSummary = summaries.FirstOrDefault(s => string.IsNullOrEmpty(s.DotnetProject));
        var projectSummaries = summaries.Where(s => !string.IsNullOrEmpty(s.DotnetProject)).ToList();
        var hotspots = await store.GetHotspotsAsync(name, 10);
        var analyses = await store.GetProjectHealthAnalysesAsync(name);

        return new ProjectHealthResponse(
            MapHealthSummary(repoSummary, dotnetSupport),
            projectSummaries.Select(summary => MapHealthSummary(summary)).ToList()!,
            hotspots.Select(MapFileMetrics).ToList(),
            analyses.Select(MapHealthAnalysis).ToList(),
            secSummary is not null ? MapSecuritySummary(secSummary) : null,
            dotnetSupport,
            MapRepositoryVitalitySummary(repoSummary));
    }

    public async Task<IReadOnlyList<FileMetrics>> GetMetricsAsync(string name, string? dotnetProject, int top)
    {
        var metrics = await store.GetFileMetricsAsync(name, dotnetProject);
        return metrics
            .OrderByDescending(m => m.ConcernScore)
            .ThenByDescending(m => m.RiskScore)
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

    internal static ProjectHealthSummary? MapHealthSummary(ProjectHealthSummaryEntity? e, DotnetSupportInfo? dotnetSupport = null)
    {
        if (e is null)
            return null;

        var baseOverallHealth = e.OverallHealth;
        var scorePenalty = dotnetSupport is not null && string.IsNullOrEmpty(e.DotnetProject)
            ? DotnetSupportHealthPolicy.GetPenalty(dotnetSupport)
            : 0;
        var adjustedHealth = scorePenalty > 0
            ? DotnetSupportHealthPolicy.ApplyPenalty(baseOverallHealth, dotnetSupport)
            : baseOverallHealth;

        return new ProjectHealthSummary(
            e.Id, e.Project, e.DotnetProject, adjustedHealth, e.TotalFiles,
            e.HotspotCount, e.AlertCount, e.TopHotspots, e.ComputedAt,
            baseOverallHealth, scorePenalty, ParseHistoryMaturity(e.HistoryMaturity));
    }

    internal static FileMetrics MapFileMetrics(FileMetricsEntity e) =>
        new(e.Id, e.Project, e.FilePath, e.DotnetProject,
            e.Changes, e.LinesAdded, e.LinesRemoved, e.AuthorCount, e.LastChangeAt,
            e.ComplexityScore, e.MaxNestingDepth, e.DeepNestingLines, e.FunctionCount, e.LongestFunction,
            e.LintErrors, e.LintWarnings, e.TrustScore,
            e.MaxCouplingStrength, e.CouplingPartners,
            e.TruckFactor, e.TopAuthors,
            e.HealthScore, e.Role, e.RiskScore, e.ComputedAt,
            e.ConcernScore, e.Churn30d, e.Churn90d, e.Churn365d,
            e.BugFixCommits90d, e.BugFixCommits365d, e.BugFixRatio365d,
            e.BugFixWeightedTouches365d, e.RecurringChurnScore);

    internal static RepositoryVitalitySummary? MapRepositoryVitalitySummary(ProjectHealthSummaryEntity? e)
    {
        if (e is null)
            return null;

        var hasMeaningfulData = !string.IsNullOrWhiteSpace(e.HistoryMaturity) ||
            e.HasSufficientHistoryForTrends ||
            !string.IsNullOrWhiteSpace(e.ActivityStatus) ||
            !string.IsNullOrWhiteSpace(e.FirefightingStatus) ||
            !string.IsNullOrWhiteSpace(e.MonthlyCommitCounts) ||
            e.VelocityLast6Months > 0 ||
            e.VelocityPrior6Months > 0 ||
            e.DormantMonths12m > 0 ||
            e.MaxInactiveStreakMonths > 0 ||
            e.FirefightingCommits90d > 0 ||
            e.FirefightingCommits365d > 0 ||
            e.FirefightingRate90d > 0 ||
            e.FirefightingRate365d > 0;

        if (!hasMeaningfulData)
            return null;

        return new RepositoryVitalitySummary(
            ParseHistoryMaturity(e.HistoryMaturity),
            e.HasSufficientHistoryForTrends,
            e.ActivityStatus,
            e.FirefightingStatus,
            ParseMonthlyCommitPoints(e.MonthlyCommitCounts),
            e.VelocityLast6Months,
            e.VelocityPrior6Months,
            e.VelocityChangePercent,
            e.DormantMonths12m,
            e.MaxInactiveStreakMonths,
            e.FirefightingCommits90d,
            e.FirefightingCommits365d,
            e.FirefightingRate90d,
            e.FirefightingRate365d);
    }

    internal static HistoryMaturity? ParseHistoryMaturity(string? value)
    {
        return Enum.TryParse<HistoryMaturity>(value, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    internal static IReadOnlyList<MonthlyCommitPoint> ParseMonthlyCommitPoints(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<MonthlyCommitPoint>>(value) ?? [];
        }
        catch
        {
            return [];
        }
    }

    internal static ProjectHealthAnalysis MapHealthAnalysis(ProjectHealthAnalysisEntity e) =>
        new(e.Id, e.Project, e.DotnetProject, e.Analysis, e.Confidence, e.ModelUsed, e.CreatedAt, e.UpdatedAt);

    internal static AnalysisBatchResponse MapBatch(StoredAnalysisBatch b) =>
        new(b.Id, b.Repo, b.ProviderBatchId, b.ProviderName, b.ExecutionMode, b.IncludeAllSource,
            b.Status, b.RequestCount, b.CompletedCount, b.SubmittedAt, b.CompletedAt);

    internal static ProjectSecuritySummary MapSecuritySummary(ProjectSecuritySummaryEntity e) =>
        new(e.SecurityScore, e.CriticalCount, e.HighCount, e.MediumCount, e.LowCount, e.ComputedAt);

    private async Task<SchemaObjectResponse> MapSchemaObjectAsync(
        GraphNode node,
        IReadOnlyDictionary<long, GraphNode> nodesById)
    {
        var columns = GetPropertyObjects(node.Properties, "columns")
            .Select((properties, index) => MapSchemaColumn(node, properties, index + 1))
            .ToList();

        var edges = await store.FindEdgesBySourceAsync(node.Id);
        var indexes = edges
            .Where(edge => edge.Type == EdgeType.DEFINES &&
                GetStringProperty(edge.Properties, "relationship")?.Equals("index", StringComparison.OrdinalIgnoreCase) == true)
            .Select(MapSchemaIndex)
            .ToList();

        var foreignKeys = edges
            .Where(edge => edge.Type is EdgeType.FOREIGN_KEY or EdgeType.QUERIES &&
                GetStringProperty(edge.Properties, "relationship")?.Equals("foreign_key", StringComparison.OrdinalIgnoreCase) == true)
            .Select(edge => MapSchemaForeignKey(node, edge, nodesById))
            .ToList();

        var primaryKeyColumns = columns.Where(column => column.IsPrimaryKey).Select(column => column.Name).ToList();
        var constraints = GetPropertyObjects(node.Properties, "constraints")
            .Select(constraint => new SchemaConstraintResponse(
                GetStringProperty(constraint, "name") ?? "constraint",
                GetStringProperty(constraint, "constraintType") ??
                    GetStringProperty(constraint, "constraint_type") ??
                    "UNKNOWN",
                GetStringListProperty(constraint, "columns"),
                GetStringProperty(constraint, "referencedTable") ??
                    GetStringProperty(constraint, "referenced_table"),
                GetStringListPropertyOrNull(constraint, "referencedColumns") ??
                    GetStringListPropertyOrNull(constraint, "referenced_columns"),
                GetStringProperty(constraint, "checkClause") ??
                    GetStringProperty(constraint, "check_clause")))
            .Where(constraint => !constraint.ConstraintType.Equals("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
            .OrderBy(constraint => constraint.Name)
            .ToList();

        if (constraints.Count == 0 && primaryKeyColumns.Count > 0)
        {
            constraints.Add(new SchemaConstraintResponse(
                "PRIMARY",
                "PRIMARY KEY",
                primaryKeyColumns,
                null,
                null,
                null));
        }

        return new SchemaObjectResponse(
            node.Id,
            node.Name,
            node.QualifiedName,
            node.Label.ToString(),
            GetStringProperty(node.Properties, "comment"),
            primaryKeyColumns,
            indexes,
            constraints,
            foreignKeys,
            columns);
    }

    private static SchemaColumnResponse MapSchemaColumn(GraphNode owner, Dictionary<string, object> properties, int ordinal)
    {
        var name = GetStringProperty(properties, "name") ?? $"column_{ordinal}";
        return new SchemaColumnResponse(
            ordinal,
            name,
            $"{owner.QualifiedName}.{name}",
            ordinal,
            GetStringProperty(properties, "dataType") ?? GetStringProperty(properties, "type") ?? "",
            GetBoolProperty(properties, "nullable") ?? true,
            GetBoolProperty(properties, "isPrimaryKey") ?? GetBoolProperty(properties, "is_primary_key") ?? false,
            GetStringProperty(properties, "default"),
            GetStringProperty(properties, "key"),
            GetStringProperty(properties, "extra"),
            GetStringProperty(properties, "comment"));
    }

    private static SchemaIndexResponse MapSchemaIndex(GraphEdge edge)
    {
        var columns = SplitCsv(GetStringProperty(edge.Properties, "columns"));
        return new SchemaIndexResponse(
            GetStringProperty(edge.Properties, "indexName") ?? GetStringProperty(edge.Properties, "index_name") ?? "unnamed",
            GetBoolProperty(edge.Properties, "isUnique") ?? GetBoolProperty(edge.Properties, "is_unique") ?? false,
            GetStringProperty(edge.Properties, "indexType") ?? GetStringProperty(edge.Properties, "index_type"),
            columns);
    }

    private static SchemaForeignKeyResponse MapSchemaForeignKey(
        GraphNode node,
        GraphEdge edge,
        IReadOnlyDictionary<long, GraphNode> nodesById)
    {
        nodesById.TryGetValue(edge.TargetId, out var target);
        var referencedTable = target?.Name ??
            GetStringProperty(edge.Properties, "referencedTable") ??
            GetStringProperty(edge.Properties, "referenced_table") ??
            "";

        var columns = SplitCsv(GetStringProperty(edge.Properties, "columns"));
        if (columns.Count == 0)
        {
            var column = GetStringProperty(edge.Properties, "column");
            if (!string.IsNullOrWhiteSpace(column))
                columns = [column];
        }

        var referencedColumns = SplitCsv(GetStringProperty(edge.Properties, "referencedColumns") ??
            GetStringProperty(edge.Properties, "referenced_columns"));
        if (referencedColumns.Count == 0)
        {
            var referencedColumn = GetStringProperty(edge.Properties, "referencedColumn") ??
                GetStringProperty(edge.Properties, "referenced_column");
            if (!string.IsNullOrWhiteSpace(referencedColumn))
                referencedColumns = [referencedColumn];
        }

        return new SchemaForeignKeyResponse(
            GetStringProperty(edge.Properties, "name") ?? $"FK_{node.Name}_{referencedTable}".TrimEnd('_'),
            columns,
            referencedTable,
            referencedColumns);
    }

    private static SchemaProcedureResponse MapSchemaProcedure(GraphNode node)
    {
        var parameters = GetPropertyObjects(node.Properties, "parameters")
            .Select((properties, index) => new SchemaParameterResponse(
                GetStringProperty(properties, "name") ?? $"parameter_{index + 1}",
                index + 1,
                GetBoolProperty(properties, "isOutput") ?? GetBoolProperty(properties, "is_output") ?? false ? "OUT" : "IN",
                GetStringProperty(properties, "dataType") ?? GetStringProperty(properties, "type") ?? "",
                GetBoolProperty(properties, "nullable") ?? true))
            .ToList();

        return new SchemaProcedureResponse(
            node.Id,
            node.Name,
            node.QualifiedName,
            GetStringProperty(node.Properties, "routineType") ?? GetStringProperty(node.Properties, "routine_type") ?? "PROCEDURE",
            GetStringProperty(node.Properties, "comment"),
            parameters);
    }

    private static bool IsDatabaseSchemaProject(ProjectInfo project) =>
        project.Name.StartsWith("db:", StringComparison.OrdinalIgnoreCase) ||
        GetStringProperty(project.Properties, "serverName") is not null ||
        GetStringProperty(project.Properties, "databaseName") is not null;

    private static string GetDatabaseNameFromProject(string projectName)
    {
        var name = projectName.StartsWith("db:", StringComparison.OrdinalIgnoreCase)
            ? projectName[3..]
            : projectName;
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    private static int GetLabelCount(Dictionary<string, int>? counts, NodeLabel label) =>
        counts is not null && counts.TryGetValue(label.ToString(), out var value) ? value : 0;

    private static IReadOnlyList<Dictionary<string, object>> GetPropertyObjects(
        Dictionary<string, object>? properties,
        string key)
    {
        var value = GetPropertyValue(properties, key);
        if (value is null)
            return [];

        if (value is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
                return [];

            return element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => item.EnumerateObject()
                    .ToDictionary(property => property.Name, property => (object)property.Value.Clone(), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        if (value is IEnumerable<Dictionary<string, object>> dictionaries)
            return dictionaries.ToList();

        if (value is IEnumerable<object> objects)
        {
            return objects
                .Select(item => item switch
                {
                    Dictionary<string, object> dictionary => dictionary,
                    JsonElement { ValueKind: JsonValueKind.Object } element => element.EnumerateObject()
                        .ToDictionary(property => property.Name, property => (object)property.Value.Clone(), StringComparer.OrdinalIgnoreCase),
                    _ => null
                })
                .OfType<Dictionary<string, object>>()
                .ToList();
        }

        return [];
    }

    private static string? GetStringProperty(Dictionary<string, object>? properties, string key)
    {
        var value = GetPropertyValue(properties, key);
        return value switch
        {
            null => null,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Number } element => element.ToString(),
            JsonElement { ValueKind: JsonValueKind.True } => "true",
            JsonElement { ValueKind: JsonValueKind.False } => "false",
            _ => value.ToString()
        };
    }

    private static bool? GetBoolProperty(Dictionary<string, object>? properties, string key)
    {
        var value = GetPropertyValue(properties, key);
        return value switch
        {
            null => null,
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } element when bool.TryParse(element.GetString(), out var b) => b,
            string s when bool.TryParse(s, out var b) => b,
            _ => null
        };
    }

    private static IReadOnlyList<string> GetStringListProperty(Dictionary<string, object>? properties, string key)
    {
        var value = GetPropertyValue(properties, key);
        if (value is null)
            return [];

        if (value is string text)
            return SplitCsv(text);

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
                return SplitCsv(element.GetString());

            if (element.ValueKind == JsonValueKind.Array)
            {
                return element.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToList();
            }

            return [];
        }

        if (value is IEnumerable<string> strings)
            return strings.Where(item => !string.IsNullOrWhiteSpace(item)).ToList();

        if (value is IEnumerable<object> objects)
        {
            return objects
                .Select(item => item switch
                {
                    string textItem => textItem,
                    JsonElement { ValueKind: JsonValueKind.String } elementItem => elementItem.GetString(),
                    JsonElement elementItem => elementItem.ToString(),
                    _ => item.ToString()
                })
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList();
        }

        return [];
    }

    private static IReadOnlyList<string>? GetStringListPropertyOrNull(Dictionary<string, object>? properties, string key)
    {
        var value = GetPropertyValue(properties, key);
        return value is null ? null : GetStringListProperty(properties, key);
    }

    private static object? GetPropertyValue(Dictionary<string, object>? properties, string key)
    {
        if (properties is null)
            return null;

        foreach (var pair in properties)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    private static IReadOnlyList<string> SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record SchemaProject(ProjectInfo Project, string ServerName, string DatabaseName);
}
