using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeGraph.Data;
using CodeGraph.Models;

namespace CodeGraph.Tests.Extractors;

/// <summary>
/// Complete in-memory IGraphStore implementation for unit testing.
/// </summary>
public class InMemoryGraphStore : IGraphStore, IExclusionStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryIndexingLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private long _nextId = 1;
    private readonly List<GraphNode> _nodes = new();
    private readonly List<GraphEdge> _edges = new();
    private readonly List<CrossRepoEdge> _crossEdges = new();
    private readonly List<RepositoryInfo> _repositories = new();
    private readonly Dictionary<string, Dictionary<string, string>> _fileHashes = new();
    private readonly Dictionary<string, Dictionary<string, string>> _fileExtractionVersions = new();
    private readonly Dictionary<string, RepositorySummary> _summaries = new();
    private readonly List<ProjectDiagnosticEntity> _projectDiagnostics = new();
    private readonly List<ProjectReviewRunEntity> _projectReviewRuns = new();
    private readonly List<ProjectReviewFindingEntity> _projectReviewFindings = new();
    private readonly List<RepositoryReviewRunEntity> _repositoryReviewRuns = new();
    private readonly List<RepositoryReviewFindingEntity> _repositoryReviewFindings = new();
    private readonly List<RepositoryReviewProjectSectionEntity> _repositoryReviewProjectSections = new();
    private long _nextReviewRunId = 1;
    private long _nextReviewFindingId = 1;
    private long _nextRepositoryReviewRunId = 1;
    private long _nextRepositoryReviewFindingId = 1;
    private long _nextRepositoryReviewProjectSectionId = 1;
    public Exception? ReplacementFailure { get; set; }
    public Exception? IncrementalReplacementFailure { get; set; }
    public Exception? IndexingLockFailure { get; set; }

    public async Task<IAsyncDisposable> AcquireRepositoryIndexingLockAsync(
        string repository,
        CancellationToken ct = default)
    {
        if (IndexingLockFailure is not null)
            throw IndexingLockFailure;
        var repositoryLock = RepositoryIndexingLocks.GetOrAdd(repository, _ => new SemaphoreSlim(1, 1));
        await repositoryLock.WaitAsync(ct);
        return new InMemoryIndexingLock(repositoryLock);
    }

    private sealed class InMemoryIndexingLock(SemaphoreSlim repositoryLock) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            repositoryLock.Release();
            return ValueTask.CompletedTask;
        }
    }

    public IReadOnlyList<GraphNode> Nodes => _nodes;
    public IReadOnlyList<GraphEdge> Edges => _edges;

    /// <summary>Incremental file-slice writes made so far.</summary>
    public int ReplaceRepositoryFilesCallCount { get; private set; }

    /// <summary>The replaced file paths of every non-empty <see cref="ReplaceRepositoryFilesAsync"/> call.</summary>
    public List<IReadOnlyList<string>> ReplacedFileSlices { get; } = [];

    /// <summary>Stored file hash records of a repository (source files and tracked manifests).</summary>
    public IReadOnlyDictionary<string, string> FileHashesOf(string repository) =>
        _fileHashes.TryGetValue(repository, out var hashes) ? hashes : new Dictionary<string, string>();

    /// <summary>Calls of the batched lookups, by method name.</summary>
    public Dictionary<string, int> LookupCallCounts { get; } = new(StringComparer.Ordinal);

    private void CountLookup(string method) =>
        LookupCallCounts[method] = LookupCallCounts.GetValueOrDefault(method) + 1;

    public Task<IReadOnlyList<GraphNode>> FindNodesByFilesAsync(
        string repository, IReadOnlyCollection<string> filePaths, CancellationToken ct = default)
    {
        CountLookup(nameof(FindNodesByFilesAsync));
        var paths = filePaths.Select(path => path.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<GraphNode>>(_nodes.Where(node =>
                node.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase)
                && node.FilePath is not null
                && paths.Contains(node.FilePath.Replace('\\', '/')))
            .ToList());
    }

    public Task<IReadOnlyList<string>> GetNodeFilePathsAsync(string repository, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_nodes
            .Where(node => node.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase)
                           && !string.IsNullOrEmpty(node.FilePath))
            .Select(node => node.FilePath!)
            .Distinct(StringComparer.Ordinal)
            .ToList());

    public Task<IReadOnlyList<GraphNode>> FindNodesByNamesAsync(
        string repository, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        CountLookup(nameof(FindNodesByNamesAsync));
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<GraphNode>>(_nodes.Where(node =>
                node.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) && wanted.Contains(node.Name))
            .ToList());
    }

    public Task<IReadOnlyList<StoredEdge>> FindEdgesByPropertyAsync(
        string repository,
        IReadOnlyCollection<EdgeType> types,
        string propertyKey,
        IReadOnlyCollection<string> values,
        CancellationToken ct = default)
    {
        CountLookup(nameof(FindEdgesByPropertyAsync));
        var wanted = values.ToHashSet(StringComparer.Ordinal);
        var nodeById = _nodes.ToDictionary(node => node.Id);
        return Task.FromResult<IReadOnlyList<StoredEdge>>(_edges
            .Where(edge => edge.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase)
                           && types.Contains(edge.Type)
                           && edge.Properties?.GetValueOrDefault(propertyKey)?.ToString() is { } value
                           && wanted.Contains(value))
            .Select(edge => new StoredEdge(
                nodeById[edge.SourceId].QualifiedName, nodeById[edge.SourceId].FilePath,
                nodeById[edge.TargetId].QualifiedName, nodeById[edge.TargetId].FilePath,
                edge.Type, new Dictionary<string, object>(edge.Properties!)))
            .ToList());
    }
    public IReadOnlyList<CrossRepoEdge> CrossEdges => _crossEdges;
    public IReadOnlyDictionary<string, RepositorySummary> Summaries => _summaries;
    public int SchemaSearchQueryCount { get; private set; }
    public int SchemaPageEnrichmentCount { get; private set; }

    public long AddNode(GraphNode node)
    {
        var withId = node with { Id = _nextId++ };
        _nodes.Add(withId);
        return withId.Id;
    }

    public void AddEdge(GraphEdge edge) => _edges.Add(edge);

    public void AddRepository(string name, bool isFoundational = false) =>
        _repositories.Add(new RepositoryInfo(name, null, null, null, null, null, null, null, isFoundational, null));

    public void AddRepository(RepositoryInfo repository) => _repositories.Add(repository);

    // ── IGraphStore implementation ──────────────────────────────────────

    public virtual Task<IReadOnlyList<GraphNode>> FindAllNodesByLabelAsync(NodeLabel label, int limit = 50000) =>
        Task.FromResult<IReadOnlyList<GraphNode>>(
            _nodes.Where(n => n.Label == label).Take(limit).ToList());

    public Task<Dictionary<NodeLabel, int>> GetNodeCountsByLabelAsync() =>
        Task.FromResult(_nodes.GroupBy(n => n.Label).ToDictionary(g => g.Key, g => g.Count()));

    public virtual Task<IReadOnlyList<GraphEdge>> FindAllEdgesByTypeAsync(EdgeType type) =>
        Task.FromResult<IReadOnlyList<GraphEdge>>(
            _edges.Where(e => e.Type == type).ToList());

    public virtual async IAsyncEnumerable<LinkNodeRow> StreamLinkNodesAsync(
        LinkNodeQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var node in _nodes.ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (query.Labels.Contains(node.Label) &&
                CrossRepoLinkProjection.Matches(node.Properties, query.PropertyEquals))
            {
                await Task.Yield();
                yield return CrossRepoLinkProjection.ToRow(node, query);
            }
        }
    }

    public virtual async IAsyncEnumerable<LinkEdgeRow> StreamLinkEdgesAsync(
        LinkEdgeQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var nodesById = _nodes.ToDictionary(node => node.Id);
        foreach (var edge in _edges.ToList())
        {
            ct.ThrowIfCancellationRequested();
            var target = nodesById.GetValueOrDefault(edge.TargetId);
            if (query.Types.Contains(edge.Type) &&
                nodesById.TryGetValue(edge.SourceId, out var source) &&
                CrossRepoLinkProjection.TargetMatches(target, query))
            {
                await Task.Yield();
                yield return CrossRepoLinkProjection.ToRow(edge, source, target, query);
            }
        }
    }

    public Task<Dictionary<EdgeType, int>> GetEdgeCountsByTypeAsync() =>
        Task.FromResult(_edges.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count()));

    public Task<IReadOnlyList<NodeInDegree>> GetTopInDegreeNodesAsync(
        string repository,
        IReadOnlyCollection<EdgeType> edgeTypes,
        IReadOnlyCollection<NodeLabel>? targetLabels,
        int minInDegree,
        int limit)
    {
        var nodesById = _nodes.ToDictionary(node => node.Id);
        return Task.FromResult<IReadOnlyList<NodeInDegree>>(
            _edges.Where(e => e.Repository == repository && edgeTypes.Contains(e.Type))
                .Where(e => nodesById.ContainsKey(e.TargetId))
                .Where(e => targetLabels is not { Count: > 0 } || targetLabels.Contains(nodesById[e.TargetId].Label))
                .GroupBy(e => e.TargetId)
                .Where(g => g.Count() >= minInDegree)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .Take(limit)
                .Select(g => new NodeInDegree(nodesById[g.Key], g.Count()))
                .ToList());
    }

    public Task<IReadOnlyList<string>> FindRepositoriesWithNoCrossRepoEdgesAsync()
    {
        var withEdges = _crossEdges
            .SelectMany(e => new[] { e.SourceRepository, e.TargetRepository })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<string>>(
            _repositories.Select(p => p.Name).Where(n => !withEdges.Contains(n)).ToList());
    }

    public Task InsertCrossRepoEdgeBatchAsync(IReadOnlyList<CrossRepoEdge> edges, CancellationToken ct = default)
    {
        _crossEdges.AddRange(edges);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync() =>
        Task.FromResult<IReadOnlyList<RepositoryInfo>>(_repositories);

    public Task<RepositorySearchResult> SearchRepositoriesAsync(string? search = null, string? group = null,
        int page = 1, int pageSize = 25)
    {
        IEnumerable<RepositoryInfo> filtered = _repositories;
        if (!string.IsNullOrWhiteSpace(group))
            filtered = filtered.Where(p => string.Equals(p.SourceGroup, group, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(search))
            filtered = filtered.Where(p => IsRepositorySearchMatch(p.Name, search));
        var list = filtered.ToList();
        var items = list.OrderBy(p => p.Name).Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new RepositorySearchResult(items, list.Count));
    }

    public Task<SchemaRepositorySearchResult> SearchSchemaRepositoriesAsync(
        string? search = null,
        string? server = null,
        string? database = null,
        int page = 1,
        int pageSize = 25)
    {
        SchemaSearchQueryCount++;
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var schemas = _repositories
            .Where(IsDatabaseSchemaRepository)
            .Select(repository => new
            {
                Repository = repository,
                ServerName = GetStringProperty(repository.Properties, "serverName") ?? repository.SourceGroup ?? "",
                DatabaseName = GetStringProperty(repository.Properties, "databaseName") ?? GetDatabaseNameFromRepository(repository.Name)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.ServerName) && !string.IsNullOrWhiteSpace(x.DatabaseName))
            .ToList();

        var filtered = schemas.Where(x =>
            (string.IsNullOrWhiteSpace(server) || x.ServerName.Equals(server, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(database) || x.DatabaseName.Equals(database, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(search) ||
                x.Repository.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                x.ServerName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                x.DatabaseName.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.ServerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ServerName, StringComparer.Ordinal)
            .ThenBy(x => x.DatabaseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DatabaseName, StringComparer.Ordinal)
            .ThenBy(x => x.Repository.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Repository.Name, StringComparer.Ordinal)
            .ToList();

        var filteredRepositories = filtered.Select(x => x.Repository.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filteredNodes = _nodes.Where(node => filteredRepositories.Contains(node.Repository)).ToList();
        var pageItems = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x =>
            {
                SchemaPageEnrichmentCount++;
                var repositoryNodes = filteredNodes.Where(node => node.Repository.Equals(x.Repository.Name, StringComparison.OrdinalIgnoreCase));
                return new SchemaRepositoryItem(
                    x.Repository,
                    x.ServerName,
                    x.DatabaseName,
                    repositoryNodes.Count(node => node.Label == NodeLabel.Table),
                    repositoryNodes.Count(node => node.Label == NodeLabel.View),
                    repositoryNodes.Count(node => node.Label == NodeLabel.StoredProcedure));
            })
            .ToList();

        var serverOptions = schemas
            .GroupBy(x => x.ServerName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Select(x => x.ServerName).Order(StringComparer.Ordinal).First())
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToList();
        var databaseOptions = schemas
            .Where(x => string.IsNullOrWhiteSpace(server) || x.ServerName.Equals(server, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.DatabaseName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Select(x => x.DatabaseName).Order(StringComparer.Ordinal).First())
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult(new SchemaRepositorySearchResult(
            pageItems,
            filtered.Count,
            filteredNodes.Count(node => node.Label == NodeLabel.Table),
            filteredNodes.Count(node => node.Label == NodeLabel.View),
            filteredNodes.Count(node => node.Label == NodeLabel.StoredProcedure),
            serverOptions,
            databaseOptions));
    }

    private static bool IsDatabaseSchemaRepository(RepositoryInfo repository) =>
        repository.Name.StartsWith("db:", StringComparison.OrdinalIgnoreCase) ||
        GetStringProperty(repository.Properties, "serverName") is not null ||
        GetStringProperty(repository.Properties, "databaseName") is not null;

    private static string GetDatabaseNameFromRepository(string repositoryName)
    {
        var name = repositoryName.StartsWith("db:", StringComparison.OrdinalIgnoreCase)
            ? repositoryName[3..]
            : repositoryName;
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    private static string? GetStringProperty(Dictionary<string, object>? properties, string key)
    {
        if (properties is null || !properties.TryGetValue(key, out var value))
            return null;

        return value switch
        {
            null => null,
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    private static bool IsRepositorySearchMatch(string name, string search)
    {
        var trimmed = search.Trim();
        var pattern = Regex.Escape(trimmed)
            .Replace("\\*", ".*")
            .Replace("%", ".*");

        if (!trimmed.Contains('*') && !trimmed.Contains('%'))
            pattern = $".*{pattern}.*";

        return Regex.IsMatch(name, $"^{pattern}$", RegexOptions.IgnoreCase);
    }

    public Task<IReadOnlyList<string>> GetDistinctGroupsAsync()
    {
        var groups = _repositories
            .Select(p => p.SourceGroup)
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(groups!);
    }

    public Task<RepositoryInfo?> GetRepositoryByName(string name)
    {
        return Task.FromResult(_repositories.FirstOrDefault(x => x.Name == name));
    }

    public Task UpdateRepositoryCommitShaAsync(string name, string? commitSha)
    {
        var existing = _repositories.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return Task.CompletedTask;

        _repositories.Remove(existing);
        _repositories.Add(existing with { LastCommitSha = commitSha });
        return Task.CompletedTask;
    }

    public virtual Task<IReadOnlyList<TraversalEntry>> TraverseAsync(long startNodeId,
        TraceDirection direction, int maxDepth,
        EdgeType[]? edgeFilter = null, double minConfidence = 0)
    {
        var node = _nodes.FirstOrDefault(n => n.Id == startNodeId);
        if (node is null)
            return Task.FromResult<IReadOnlyList<TraversalEntry>>(Array.Empty<TraversalEntry>());

        return Task.FromResult<IReadOnlyList<TraversalEntry>>(
            new[] { new TraversalEntry(node, 0, EdgeType.CALLS, null, null) });
    }

    public Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceAsync(long sourceId, EdgeType? type = null) =>
        Task.FromResult<IReadOnlyList<GraphEdge>>(
            _edges.Where(e => e.SourceId == sourceId && (type == null || e.Type == type)).ToList());

    public Task<IReadOnlyList<GraphEdge>> FindEdgesBySourceBatchAsync(IReadOnlyList<long> sourceIds, EdgeType[]? types = null)
    {
        var idSet = sourceIds.ToHashSet();
        var query = _edges.Where(edge => idSet.Contains(edge.SourceId));
        if (types is { Length: > 0 })
            query = query.Where(edge => types.Contains(edge.Type));
        return Task.FromResult<IReadOnlyList<GraphEdge>>(query.ToList());
    }

    public Task<GraphNode?> FindNodeByIdAsync(long id) =>
        Task.FromResult(_nodes.FirstOrDefault(n => n.Id == id));

    public virtual Task<Dictionary<long, GraphNode>> FindNodesByIdBatchAsync(IReadOnlyList<long> ids)
    {
        var idSet = ids.ToHashSet();
        var result = _nodes.Where(n => idSet.Contains(n.Id))
            .ToDictionary(n => n.Id);
        return Task.FromResult(result);
    }

    public virtual Task<Dictionary<string, int>> GetNodeCountsByLabelForRepositoryAsync(string repository) =>
        Task.FromResult(
            _nodes.Where(n => n.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))
                .GroupBy(n => n.Label.ToString())
                .ToDictionary(g => g.Key, g => g.Count()));

    public Task<Dictionary<string, Dictionary<string, int>>> GetNodeCountsByDotnetProjectAsync(string repository)
    {
        var result = _nodes
            .Where(n => n.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) && n.DotnetProject is not null)
            .GroupBy(n => n.DotnetProject!)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(n => n.Label.ToString()).ToDictionary(lg => lg.Key, lg => lg.Count()));
        return Task.FromResult(result);
    }

    // ── Repository operations ──────────────────────────────────────────────

    public Task UpsertRepositoryAsync(RepositoryEntity repository)
    {
        var existing = _repositories.FirstOrDefault(p => p.Name.Equals(repository.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            _repositories.Remove(existing);
        _repositories.Add(new RepositoryInfo(repository.Name, repository.RepoUrl, repository.SourceGroup,
            repository.LocalPath, repository.LastCommitSha, repository.IndexedAt, repository.Language, repository.Framework,
            repository.IsFoundational,
            string.IsNullOrWhiteSpace(repository.Properties)
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, object>>(repository.Properties)));
        return Task.CompletedTask;
    }

    public Task EnsureRepositoryAsync(RepositoryEntity repository)
    {
        var index = _repositories.FindIndex(p => p.Name.Equals(repository.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return UpsertRepositoryAsync(repository);

        var existing = _repositories[index];
        _repositories[index] = existing with
        {
            RepoUrl = repository.RepoUrl ?? existing.RepoUrl,
            SourceGroup = repository.SourceGroup ?? existing.SourceGroup,
            LocalPath = repository.LocalPath ?? existing.LocalPath,
            IsFoundational = repository.IsFoundational
        };
        return Task.CompletedTask;
    }

    public Task DeleteRepositoryAsync(string repository)
    {
        _repositories.RemoveAll(p => p.Name.Equals(repository, StringComparison.OrdinalIgnoreCase));
        _nodes.RemoveAll(n => n.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase));
        _edges.RemoveAll(e =>
        {
            var source = _nodes.FirstOrDefault(n => n.Id == e.SourceId);
            var target = _nodes.FirstOrDefault(n => n.Id == e.TargetId);
            return (source?.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (target?.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) ?? false);
        });
        _crossEdges.RemoveAll(e =>
            e.SourceRepository.Equals(repository, StringComparison.OrdinalIgnoreCase) ||
            e.TargetRepository.Equals(repository, StringComparison.OrdinalIgnoreCase));
        _fileHashes.Remove(repository);
        _fileExtractionVersions.Remove(repository);
        _summaries.Remove(repository);
        return Task.CompletedTask;
    }

    // ── Node operations ─────────────────────────────────────────────────

    public Task<long> UpsertNodeAsync(GraphNode node)
    {
        var existing = _nodes.FirstOrDefault(n =>
            n.Repository.Equals(node.Repository, StringComparison.OrdinalIgnoreCase) &&
            n.QualifiedName.Equals(node.QualifiedName, StringComparison.Ordinal));

        if (existing != null)
        {
            _nodes.Remove(existing);
            var updated = node with { Id = existing.Id };
            _nodes.Add(updated);
            return Task.FromResult(existing.Id);
        }

        var withId = node with { Id = _nextId++ };
        _nodes.Add(withId);
        return Task.FromResult(withId.Id);
    }

    public async Task<Dictionary<string, long>> UpsertNodeBatchAsync(IReadOnlyList<GraphNode> nodes, CancellationToken ct = default)
    {
        var result = new Dictionary<string, long>();
        foreach (var node in nodes)
        {
            var id = await UpsertNodeAsync(node);
            result[GraphNodeKey.Create(node.Repository, node.QualifiedName)] = id;
        }
        return result;
    }

    public virtual Task<GraphNode?> FindNodeByQualifiedNameAsync(string repository, string qualifiedName) =>
        Task.FromResult(_nodes.FirstOrDefault(n =>
            n.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) &&
            n.QualifiedName.Equals(qualifiedName, StringComparison.Ordinal)));

    public Task<IReadOnlyList<GraphNode>> FindNodesByQualifiedNamesAsync(
        string repository,
        IReadOnlyCollection<string> qualifiedNames)
    {
        var names = qualifiedNames.ToHashSet(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<GraphNode>>(
            _nodes.Where(n =>
                n.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) &&
                names.Contains(n.QualifiedName)).ToList());
    }

    public virtual Task<IReadOnlyList<GraphNode>> FindNodesByNameAsync(string repository, string name, int limit = 1000) =>
        Task.FromResult<IReadOnlyList<GraphNode>>(
            _nodes.Where(n =>
                n.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) &&
                n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList());

    public virtual Task<IReadOnlyList<GraphNode>> FindNodesByLabelAsync(string repository, NodeLabel label, int limit = 10000) =>
        Task.FromResult<IReadOnlyList<GraphNode>>(
            _nodes.Where(n =>
                n.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) &&
                n.Label == label).Take(limit).ToList());

    public Task<IReadOnlyList<GraphNode>> FindNodesByFileAsync(string repository, string filePath, int limit = 5000) =>
        Task.FromResult<IReadOnlyList<GraphNode>>(
            _nodes.Where(n =>
                n.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) &&
                n.FilePath != null &&
                n.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList());

    public virtual Task<IReadOnlyList<GraphNode>> SearchNodesAsync(string? repository, string namePattern,
        NodeLabel? label = null, string? filePattern = null, int limit = 50, int offset = 0,
        string? dotnetProject = null)
    {
        var query = _nodes.AsEnumerable();

        if (!string.IsNullOrEmpty(repository))
            query = query.Where(n => n.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(namePattern))
            query = query.Where(n => MatchesLike(n.Name, namePattern));

        if (label.HasValue)
            query = query.Where(n => n.Label == label.Value);

        if (!string.IsNullOrEmpty(filePattern))
            query = query.Where(n => n.FilePath != null &&
                n.FilePath.Contains(filePattern, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(dotnetProject))
            query = query.Where(n => n.DotnetProject == dotnetProject);

        return Task.FromResult<IReadOnlyList<GraphNode>>(
            query.Skip(offset).Take(limit).ToList());
    }

    // SQL LIKE '%pattern%' with the store's '%' wildcard (other characters match literally).
    private static bool MatchesLike(string value, string pattern)
    {
        var position = 0;
        foreach (var segment in pattern.Split('%', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = value.IndexOf(segment, position, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;
            position = index + segment.Length;
        }

        return true;
    }

    public virtual Task<int> SearchNodesCountAsync(string? repository, string namePattern,
        NodeLabel? label = null, string? filePattern = null, string? dotnetProject = null)
    {
        var query = _nodes.AsEnumerable();
        if (!string.IsNullOrEmpty(repository))
            query = query.Where(n => n.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(namePattern))
            query = query.Where(n => MatchesLike(n.Name, namePattern));
        if (label.HasValue)
            query = query.Where(n => n.Label == label.Value);
        if (!string.IsNullOrEmpty(filePattern))
            query = query.Where(n => n.FilePath != null &&
                n.FilePath.Contains(filePattern, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(dotnetProject))
            query = query.Where(n => n.DotnetProject == dotnetProject);
        return Task.FromResult(query.Count());
    }

    // ── Edge operations ─────────────────────────────────────────────────

    public Task InsertEdgeAsync(GraphEdge edge)
    {
        _edges.Add(edge);
        return Task.CompletedTask;
    }

    public Task InsertEdgeBatchAsync(IReadOnlyList<GraphEdge> edges, CancellationToken ct = default)
    {
        _edges.AddRange(edges);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetAsync(long targetId, EdgeType? type = null) =>
        Task.FromResult<IReadOnlyList<GraphEdge>>(
            _edges.Where(e => e.TargetId == targetId && (type == null || e.Type == type)).ToList());

    public Task<IReadOnlyList<GraphEdge>> FindEdgesByTargetBatchAsync(IReadOnlyList<long> targetIds, EdgeType[]? types = null)
    {
        var idSet = targetIds.ToHashSet();
        var query = _edges.Where(e => idSet.Contains(e.TargetId));
        if (types is { Length: > 0 })
            query = query.Where(e => types.Contains(e.Type));
        return Task.FromResult<IReadOnlyList<GraphEdge>>(query.ToList());
    }

    // ── Cross-repo edges ────────────────────────────────────────────────

    public Task InsertCrossRepoEdgeAsync(CrossRepoEdge edge)
    {
        _crossEdges.Add(edge);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CrossRepoEdge>> FindCrossRepoEdgesAsync(string repository, EdgeType? type = null) =>
        Task.FromResult<IReadOnlyList<CrossRepoEdge>>(
            _crossEdges.Where(e =>
                (e.SourceRepository.Equals(repository, StringComparison.OrdinalIgnoreCase) ||
                 e.TargetRepository.Equals(repository, StringComparison.OrdinalIgnoreCase)) &&
                (type == null || e.Type == type)).ToList());

    public Task<IReadOnlyList<CrossRepoEdge>> GetAllCrossRepoEdgesAsync() =>
        Task.FromResult<IReadOnlyList<CrossRepoEdge>>(_crossEdges.ToList());

    public Task SetDoNotTrustAsync(long nodeId, bool doNotTrust)
    {
        var idx = _nodes.FindIndex(n => n.Id == nodeId);
        if (idx >= 0)
            _nodes[idx] = _nodes[idx] with { DoNotTrust = doNotTrust };
        return Task.CompletedTask;
    }

    // ── Bulk operations ─────────────────────────────────────────────────

    public Task DeleteNodesByFileAsync(string repository, string filePath)
    {
        var toDelete = _nodes.Where(n =>
            n.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) &&
            n.FilePath != null &&
            n.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)).ToList();

        var nodeIds = toDelete.Select(n => n.Id).ToHashSet();
        _nodes.RemoveAll(n => nodeIds.Contains(n.Id));
        _edges.RemoveAll(e => nodeIds.Contains(e.SourceId) || nodeIds.Contains(e.TargetId));
        return Task.CompletedTask;
    }

    public Task DeleteNodesByRepositoryAsync(string repository)
    {
        var nodeIds = _nodes.Where(n =>
            n.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Id)
            .ToHashSet();

        _nodes.RemoveAll(n => nodeIds.Contains(n.Id));
        _edges.RemoveAll(e => nodeIds.Contains(e.SourceId) || nodeIds.Contains(e.TargetId));
        return Task.CompletedTask;
    }

    public Task<int> ReplaceRepositoryGraphAsync(
        string repository,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, string> fileHashes,
        RepositoryEntity repositoryEntity,
        SyncStateEntity? syncState,
        IReadOnlyDictionary<string, string>? extractionVersions = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (ReplacementFailure is not null)
            throw ReplacementFailure;

        var nextId = _nextId;
        var replacementNodes = nodes
            .Select(node => node with { Id = nextId++ })
            .ToList();
        var qnToId = replacementNodes.ToDictionary(
            node => GraphNodeKey.Create(repository, node.QualifiedName),
            node => node.Id,
            StringComparer.Ordinal);
        var replacementEdges = edges
            .Where(edge =>
                qnToId.ContainsKey(GraphNodeKey.Create(repository, edge.SourceQN)) &&
                qnToId.ContainsKey(GraphNodeKey.Create(repository, edge.TargetQN)))
            .Select(edge => new GraphEdge
            {
                Repository = repository,
                SourceId = qnToId[GraphNodeKey.Create(repository, edge.SourceQN)],
                TargetId = qnToId[GraphNodeKey.Create(repository, edge.TargetQN)],
                Type = edge.Type,
                Properties = edge.Properties ?? []
            })
            .ToList();

        var oldNodeIds = _nodes
            .Where(node => node.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))
            .Select(node => node.Id)
            .ToHashSet();
        _nodeAnalyses.Keys.Where(oldNodeIds.Contains).ToList()
            .ForEach(nodeId => _nodeAnalyses.Remove(nodeId));
        _crossEdges.RemoveAll(edge =>
            edge.SourceRepository.Equals(repository, StringComparison.OrdinalIgnoreCase) ||
            edge.TargetRepository.Equals(repository, StringComparison.OrdinalIgnoreCase));
        _edges.RemoveAll(edge =>
            edge.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) ||
            oldNodeIds.Contains(edge.SourceId) ||
            oldNodeIds.Contains(edge.TargetId));
        _nodes.RemoveAll(node => oldNodeIds.Contains(node.Id));

        _nodes.AddRange(replacementNodes);
        _edges.AddRange(replacementEdges);
        _fileHashes[repository] = new Dictionary<string, string>(fileHashes, StringComparer.OrdinalIgnoreCase);
        _fileExtractionVersions[repository] = fileHashes.Keys
            .Where(path => extractionVersions?.ContainsKey(path) == true)
            .ToDictionary(path => path, path => extractionVersions![path], StringComparer.OrdinalIgnoreCase);
        _ = UpsertRepositoryAsync(repositoryEntity);
        if (syncState is not null)
        {
            if (_syncStates.TryGetValue(repository, out var previous))
                CopyVitalsRecord(previous, syncState);
            _syncStates[repository] = syncState;
        }
        _nextId = nextId;

        return Task.FromResult(replacementEdges.Count);
    }

    public Task<int> ReplaceRepositoryFilesAsync(
        string repository,
        IReadOnlyList<string> filePaths,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<PendingEdge> edges,
        IReadOnlyDictionary<string, string> fileHashes,
        IReadOnlyDictionary<string, string>? extractionVersions = null,
        GraphSliceRetractions? retractions = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (filePaths.Count == 0) return Task.FromResult(0);
        if (IncrementalReplacementFailure is not null)
            throw IncrementalReplacementFailure;
        ReplaceRepositoryFilesCallCount++;
        ReplacedFileSlices.Add(filePaths.ToList());

        var repositoryIdsByQn = _nodes
            .Where(node => node.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))
            .GroupBy(node => node.QualifiedName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);
        var retractedKeys = (retractions?.Edges ?? [])
            .Where(edge => repositoryIdsByQn.ContainsKey(edge.SourceQN) && repositoryIdsByQn.ContainsKey(edge.TargetQN))
            .Select(edge => (repositoryIdsByQn[edge.SourceQN], repositoryIdsByQn[edge.TargetQN], edge.Type))
            .ToHashSet();
        bool IsRetracted(GraphEdge edge) => retractedKeys.Contains((edge.SourceId, edge.TargetId, edge.Type));

        var paths = filePaths
            .SelectMany(path => new[] { path.Replace('\\', '/'), path.Replace('/', '\\') })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldNodes = _nodes.Where(node =>
                node.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) &&
                paths.Contains(node.FilePath))
            .ToList();
        var oldIds = oldNodes.Select(node => node.Id).ToHashSet();
        var nodeById = _nodes.ToDictionary(node => node.Id);
        var preservedIncoming = _edges
            .Where(edge => oldIds.Contains(edge.TargetId) && !oldIds.Contains(edge.SourceId) && !IsRetracted(edge))
            .Select(edge => new PendingEdge(
                nodeById[edge.SourceId].QualifiedName,
                nodeById[edge.TargetId].QualifiedName,
                edge.Type,
                edge.Properties))
            .ToList();

        var stagedNodes = _nodes.Where(node => !oldIds.Contains(node.Id)).ToList();
        var stagedEdges = _edges
            .Where(edge => !oldIds.Contains(edge.SourceId) && !oldIds.Contains(edge.TargetId) && !IsRetracted(edge))
            .ToList();
        var stagedCrossEdges = _crossEdges
            .Where(edge => !oldIds.Contains(edge.SourceNodeId) && !oldIds.Contains(edge.TargetNodeId))
            .ToList();
        var stagedAnalyses = _nodeAnalyses
            .Where(entry => !oldIds.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        var stagedHashes = _fileHashes.TryGetValue(repository, out var existingHashes)
            ? new Dictionary<string, string>(existingHashes, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stagedVersions = _fileExtractionVersions.TryGetValue(repository, out var existingVersions)
            ? new Dictionary<string, string>(existingVersions, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            stagedHashes.Remove(path);
            stagedVersions.Remove(path);
        }

        var nextId = _nextId;
        var qnToId = stagedNodes
            .Where(node => node.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(node => node.QualifiedName, node => node.Id, StringComparer.Ordinal);
        foreach (var node in nodes.DistinctBy(node => node.QualifiedName, StringComparer.Ordinal))
        {
            if (qnToId.ContainsKey(node.QualifiedName))
                continue;
            var stored = node with { Id = nextId++, Repository = repository };
            stagedNodes.Add(stored);
            qnToId[stored.QualifiedName] = stored.Id;
        }

        var pendingByKey = new Dictionary<string, PendingEdge>(StringComparer.Ordinal);
        foreach (var edge in preservedIncoming)
            pendingByKey[$"{edge.SourceQN}\u001f{edge.TargetQN}\u001f{edge.Type}"] = edge;
        foreach (var edge in edges)
            pendingByKey[$"{edge.SourceQN}\u001f{edge.TargetQN}\u001f{edge.Type}"] = edge;

        var resolved = pendingByKey.Values
            .Where(edge => qnToId.ContainsKey(edge.SourceQN) && qnToId.ContainsKey(edge.TargetQN))
            .Select(edge => new GraphEdge
            {
                Repository = repository,
                SourceId = qnToId[edge.SourceQN],
                TargetId = qnToId[edge.TargetQN],
                Type = edge.Type,
                Properties = edge.Properties ?? []
            })
            .ToList();
        foreach (var edge in resolved)
        {
            stagedEdges.RemoveAll(existing => existing.SourceId == edge.SourceId &&
                                               existing.TargetId == edge.TargetId &&
                                               existing.Type == edge.Type);
            stagedEdges.Add(edge);
        }

        if (retractions is { OrphanCandidateQNs.Count: > 0 })
        {
            var candidates = retractions.OrphanCandidateQNs.ToHashSet(StringComparer.Ordinal);
            var connected = stagedEdges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId }).ToHashSet();
            stagedNodes.RemoveAll(node =>
                node.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase)
                && candidates.Contains(node.QualifiedName)
                && string.IsNullOrEmpty(node.FilePath)
                && !connected.Contains(node.Id));
        }

        foreach (var hash in fileHashes)
        {
            stagedHashes[hash.Key] = hash.Value;
            if (extractionVersions?.TryGetValue(hash.Key, out var version) == true)
                stagedVersions[hash.Key] = version;
        }

        _nodes.Clear();
        _nodes.AddRange(stagedNodes);
        _edges.Clear();
        _edges.AddRange(stagedEdges);
        _crossEdges.Clear();
        _crossEdges.AddRange(stagedCrossEdges);
        _nodeAnalyses.Clear();
        foreach (var analysis in stagedAnalyses)
            _nodeAnalyses[analysis.Key] = analysis.Value;
        _fileHashes[repository] = stagedHashes;
        _fileExtractionVersions[repository] = stagedVersions;
        _nextId = nextId;

        return Task.FromResult(resolved.Count);
    }

    public Task DeleteFilesFromRepositoryGraphAsync(
        string repository,
        IReadOnlyList<string> filePaths,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (filePaths.Count == 0) return Task.CompletedTask;

        var paths = filePaths
            .SelectMany(path => new[]
            {
                path.Replace('\\', '/'),
                path.Replace('/', '\\')
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nodeIds = _nodes
            .Where(node =>
                node.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) &&
                paths.Contains(node.FilePath))
            .Select(node => node.Id)
            .ToHashSet();

        _nodeAnalyses.Keys.Where(nodeIds.Contains).ToList()
            .ForEach(nodeId => _nodeAnalyses.Remove(nodeId));
        _crossEdges.RemoveAll(edge => nodeIds.Contains(edge.SourceNodeId) || nodeIds.Contains(edge.TargetNodeId));
        _edges.RemoveAll(edge => nodeIds.Contains(edge.SourceId) || nodeIds.Contains(edge.TargetId));
        _nodes.RemoveAll(node => nodeIds.Contains(node.Id));

        if (_fileHashes.TryGetValue(repository, out var hashes))
        {
            foreach (var path in paths)
                hashes.Remove(path);
        }

        return Task.CompletedTask;
    }

    // ── File hashes ─────────────────────────────────────────────────────

    public Task<Dictionary<string, string>> GetFileHashesAsync(string repository)
    {
        if (_fileHashes.TryGetValue(repository, out var hashes))
            return Task.FromResult(new Dictionary<string, string>(hashes));
        return Task.FromResult(new Dictionary<string, string>());
    }

    /// <summary>Simulates file_hashes rows written before extraction versioning existed.</summary>
    public void ClearFileExtractionVersions(string repository) => _fileExtractionVersions.Remove(repository);

    public Task<Dictionary<string, FileHashRecord>> GetFileHashRecordsAsync(string repository)
    {
        var result = new Dictionary<string, FileHashRecord>(StringComparer.OrdinalIgnoreCase);
        if (!_fileHashes.TryGetValue(repository, out var hashes))
            return Task.FromResult(result);
        _fileExtractionVersions.TryGetValue(repository, out var versions);
        foreach (var hash in hashes)
            result[hash.Key] = new FileHashRecord(hash.Value, versions?.GetValueOrDefault(hash.Key));
        return Task.FromResult(result);
    }

    public Task UpsertFileHashBatchAsync(string repository, Dictionary<string, string> hashes, CancellationToken ct = default)
    {
        if (!_fileHashes.ContainsKey(repository))
            _fileHashes[repository] = new Dictionary<string, string>();

        foreach (var kvp in hashes)
            _fileHashes[repository][kvp.Key] = kvp.Value;

        return Task.CompletedTask;
    }

    public Task DeleteFileHashesAsync(string repository, IReadOnlyList<string> relPaths)
    {
        if (_fileHashes.TryGetValue(repository, out var hashes))
        {
            foreach (var path in relPaths)
                hashes.Remove(path);
        }
        return Task.CompletedTask;
    }

    // ── Summaries ───────────────────────────────────────────────────────

    public Task UpsertRepositorySummaryAsync(string repository, string summary,
        ConfidenceLevel confidence, string sourceHash, string? modelUsed = null)
    {
        _summaries[repository] = new RepositorySummary(repository, summary, confidence, sourceHash, modelUsed, DateTime.UtcNow, DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public Task<RepositorySummary?> GetRepositorySummaryAsync(string repository)
    {
        _summaries.TryGetValue(repository, out var summary);
        return Task.FromResult(summary);
    }

    // ── Per-repository analyses ─────────────────────────────────────────────

    private readonly Dictionary<(string, string), StoredProjectAnalysis> _projectAnalyses = new();

    public Task UpsertProjectAnalysisAsync(string repo, StoredProjectAnalysis analysis)
    {
        _projectAnalyses[(repo, analysis.ProjectName)] = analysis;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredProjectAnalysis>> GetProjectAnalysesAsync(string repo)
    {
        var results = _projectAnalyses
            .Where(kv => kv.Key.Item1 == repo)
            .Select(kv => kv.Value)
            .ToList();
        return Task.FromResult<IReadOnlyList<StoredProjectAnalysis>>(results);
    }

    // ── Migrations ──────────────────────────────────────────────────────

    public Task ApplyMigrationsAsync(string migrationsPath)
    {
        // No-op for in-memory store
        return Task.CompletedTask;
    }

    // ── Sync state ──────────────────────────────────────────────────────

    private readonly Dictionary<string, SyncStateEntity> _syncStates = new();

    public Task<SyncStateEntity?> GetSyncStateAsync(string repository) =>
        Task.FromResult(_syncStates.GetValueOrDefault(repository));

    public Task<IReadOnlyDictionary<string, SyncStateEntity>> GetSyncStatesAsync(IReadOnlyList<string> repositories)
    {
        var result = new Dictionary<string, SyncStateEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in repositories)
        {
            if (_syncStates.TryGetValue(repository, out var state))
                result[repository] = state;
        }

        return Task.FromResult<IReadOnlyDictionary<string, SyncStateEntity>>(result);
    }

    public Task SetVitalsVersionAsync(string repository, int vitalsVersion)
    {
        if (_syncStates.TryGetValue(repository, out var state))
        {
            state.VitalsVersion = vitalsVersion;
            state.VitalsFailedCommitSha = null;
            state.VitalsFailedVersion = null;
            state.VitalsConsecutiveFailures = 0;
        }

        return Task.CompletedTask;
    }

    public Task<int> RecordVitalsFailureAsync(string repository, string? commitSha, int vitalsVersion)
    {
        if (!_syncStates.TryGetValue(repository, out var state))
            return Task.FromResult(0);

        state.VitalsConsecutiveFailures =
            state.VitalsFailedCommitSha == commitSha && state.VitalsFailedVersion == vitalsVersion
                ? state.VitalsConsecutiveFailures + 1
                : 1;
        state.VitalsFailedCommitSha = commitSha;
        state.VitalsFailedVersion = vitalsVersion;
        return Task.FromResult(state.VitalsConsecutiveFailures);
    }

    /// <summary>Like the stores, sync-state writes never change the vitals record.</summary>
    private static void CopyVitalsRecord(SyncStateEntity from, SyncStateEntity to)
    {
        to.VitalsVersion = from.VitalsVersion;
        to.VitalsFailedCommitSha = from.VitalsFailedCommitSha;
        to.VitalsFailedVersion = from.VitalsFailedVersion;
        to.VitalsConsecutiveFailures = from.VitalsConsecutiveFailures;
    }

    public Task UpsertSyncStateAsync(SyncStateEntity state)
    {
        // Mirrors the MariaDB store: a null fingerprint keeps the stored one.
        // Mirrors the stores: an upsert never changes the vitals version.
        if (_syncStates.TryGetValue(state.Repository, out var existing))
        {
            state.ExtractionFingerprint ??= existing.ExtractionFingerprint;
            CopyVitalsRecord(existing, state);
        }
        else
        {
            CopyVitalsRecord(new SyncStateEntity(), state);
        }

        _syncStates[state.Repository] = state;
        return Task.CompletedTask;
    }

    // ── Graph context for batch analysis ────────────────────────────────

    public Task<IReadOnlyList<NodeEntity>> GetClassNodesWithEdgesAsync(string repository) =>
        Task.FromResult<IReadOnlyList<NodeEntity>>(
            _nodes
                .Where(n => n.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase)
                    && n.Label is NodeLabel.Class or NodeLabel.Interface)
                .Select(MapNodeEntity)
                .ToList());

    public Task<IReadOnlyList<NodeEntity>> GetChildNodesAsync(long parentNodeId) =>
        Task.FromResult<IReadOnlyList<NodeEntity>>(
            _edges
                .Where(e => e.SourceId == parentNodeId &&
                    e.Type is EdgeType.DEFINES or EdgeType.DEFINES_METHOD)
                .Join(_nodes,
                    e => e.TargetId,
                    n => n.Id,
                    (_, n) => MapNodeEntity(n))
                .ToList());

    public Task<IReadOnlyList<EdgeEntity>> GetOutboundEdgesAsync(long nodeId) =>
        Task.FromResult<IReadOnlyList<EdgeEntity>>(
            _edges.Where(e => e.SourceId == nodeId).Select(MapEdgeEntity).ToList());

    public Task<IReadOnlyList<EdgeEntity>> GetInboundEdgesAsync(long nodeId) =>
        Task.FromResult<IReadOnlyList<EdgeEntity>>(
            _edges.Where(e => e.TargetId == nodeId).Select(MapEdgeEntity).ToList());

    public Task<IReadOnlyList<NodeEntity>> GetAllNodesByRepositoryAsync(string repository) =>
        Task.FromResult<IReadOnlyList<NodeEntity>>(
            _nodes
                .Where(n => n.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))
                .Select(MapNodeEntity)
                .ToList());

    public Task<IReadOnlyList<EdgeEntity>> GetAllEdgesByRepositoryAsync(string repository) =>
        Task.FromResult<IReadOnlyList<EdgeEntity>>(
            _edges
                .Where(e => e.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))
                .Select(MapEdgeEntity)
                .ToList());

    public Task<IReadOnlyList<EdgeEntity>> GetEdgesForNodesAsync(IReadOnlyList<long> nodeIds) =>
        Task.FromResult<IReadOnlyList<EdgeEntity>>(
            _edges
                .Where(e => nodeIds.Contains(e.SourceId) || nodeIds.Contains(e.TargetId))
                .Select(MapEdgeEntity)
                .ToList());

    // ── Analysis batch tracking ──────────────────────────────────────────

    private readonly List<AnalysisBatchEntity> _batches = new();
    private readonly List<AnalysisBatchRequestEntity> _batchRequests = new();
    private long _nextBatchId = 1;

    public Task<long> CreateAnalysisBatchAsync(AnalysisBatchEntity batch)
    {
        batch.Error = AnalysisBatchErrorDetails.Limit(batch.Error);
        batch.Id = _nextBatchId++;
        _batches.Add(batch);
        return Task.FromResult(batch.Id);
    }

    public virtual Task CreateBatchRequestsAsync(IEnumerable<AnalysisBatchRequestEntity> requests)
    {
        _batchRequests.AddRange(requests);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredAnalysisBatch>> GetPendingBatchesAsync(string? repo = null) =>
        Task.FromResult<IReadOnlyList<StoredAnalysisBatch>>(
            _batches
                .Where(b => b.Status == "submitted" && (repo is null || b.Repo == repo))
                .Select(b => new StoredAnalysisBatch(b.Id, b.Repo, b.ProviderBatchId, b.ProviderName, b.ExecutionMode, b.IncludeAllSource, b.Status,
                    b.RequestCount, b.CompletedCount, b.SubmittedAt, b.CompletedAt, b.Error))
                .ToList());

    public Task<StoredAnalysisBatch?> GetLatestBatchAsync(string repo)
    {
        var batch = _batches
            .Where(b => b.Repo == repo)
            .OrderByDescending(b => b.SubmittedAt)
            .FirstOrDefault();
        return Task.FromResult(batch is null
            ? null
            : new StoredAnalysisBatch(batch.Id, batch.Repo, batch.ProviderBatchId, batch.ProviderName, batch.ExecutionMode, batch.IncludeAllSource, batch.Status,
                batch.RequestCount, batch.CompletedCount, batch.SubmittedAt, batch.CompletedAt, batch.Error));
    }

    public virtual Task<StoredAnalysisBatch?> GetBatchByProviderBatchIdAsync(string providerBatchId)
    {
        var batch = _batches
            .Where(b => string.Equals(b.ProviderBatchId, providerBatchId, StringComparison.Ordinal))
            .OrderByDescending(b => b.SubmittedAt)
            .FirstOrDefault();
        return Task.FromResult(batch is null
            ? null
            : new StoredAnalysisBatch(batch.Id, batch.Repo, batch.ProviderBatchId, batch.ProviderName, batch.ExecutionMode, batch.IncludeAllSource, batch.Status,
                batch.RequestCount, batch.CompletedCount, batch.SubmittedAt, batch.CompletedAt, batch.Error));
    }

    public Task UpdateBatchStatusAsync(long batchId, string status, int completedCount, DateTime? completedAt, string? error = null)
    {
        error = AnalysisBatchErrorDetails.Limit(error);
        var batch = _batches.FirstOrDefault(b => b.Id == batchId);
        if (batch is not null && batch.Status is not ("failed" or "completed"))
        {
            batch.Status = status;
            batch.CompletedCount = completedCount;
            batch.CompletedAt = completedAt;
            batch.Error = error;
        }
        return Task.CompletedTask;
    }

    public Task UpdateBatchRequestStateAsync(long batchId, string customId, string status, int attemptCount,
        string? responseText, string? modelUsed, DateTime? completedAt)
    {
        var req = _batchRequests.FirstOrDefault(r => r.BatchId == batchId && r.CustomId == customId);
        if (req is not null)
        {
            req.Status = status;
            req.AttemptCount = attemptCount;
            req.ResponseText = responseText;
            req.ModelUsed = modelUsed;
            req.CompletedAt = completedAt;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AnalysisBatchRequestEntity>> GetBatchRequestsAsync(long batchId)
    {
        IReadOnlyList<AnalysisBatchRequestEntity> result = _batchRequests
            .Where(r => r.BatchId == batchId)
            .OrderBy(r => r.Sequence)
            .ThenBy(r => r.CustomId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(result);
    }

    private static NodeEntity MapNodeEntity(GraphNode node) => new()
    {
        Id = node.Id,
        Repository = node.Repository,
        DotnetProject = node.DotnetProject,
        Label = node.Label.ToString(),
        Name = node.Name,
        QualifiedName = node.QualifiedName,
        FilePath = node.FilePath,
        StartLine = node.StartLine,
        EndLine = node.EndLine,
        Properties = node.Properties.Count == 0 ? null : JsonSerializer.Serialize(node.Properties),
        DoNotTrust = node.DoNotTrust
    };

    private static EdgeEntity MapEdgeEntity(GraphEdge edge) => new()
    {
        Id = edge.Id,
        Repository = edge.Repository,
        SourceId = edge.SourceId,
        TargetId = edge.TargetId,
        Type = edge.Type.ToString(),
        Properties = edge.Properties.Count == 0 ? null : JsonSerializer.Serialize(edge.Properties)
    };

    // ── Node analysis results ────────────────────────────────────────────

    private readonly Dictionary<long, NodeAnalysisEntity> _nodeAnalyses = new();

    public Task UpsertNodeAnalysisAsync(NodeAnalysisEntity analysis)
    {
        _nodeAnalyses[analysis.NodeId] = analysis;
        return Task.CompletedTask;
    }

    public Task<StoredNodeAnalysis?> GetNodeAnalysisAsync(long nodeId)
    {
        if (!_nodeAnalyses.TryGetValue(nodeId, out var e))
            return Task.FromResult<StoredNodeAnalysis?>(null);
        return Task.FromResult<StoredNodeAnalysis?>(
            new StoredNodeAnalysis(e.NodeId, e.Description, e.Confidence, e.ModelUsed, e.CreatedAt, e.UpdatedAt));
    }

    public Task<Dictionary<long, StoredNodeAnalysis>> GetNodeAnalysesBatchAsync(IReadOnlyList<long> nodeIds)
    {
        var result = new Dictionary<long, StoredNodeAnalysis>();
        foreach (var id in nodeIds)
        {
            if (_nodeAnalyses.TryGetValue(id, out var e))
                result[id] = new StoredNodeAnalysis(e.NodeId, e.Description, e.Confidence, e.ModelUsed, e.CreatedAt, e.UpdatedAt);
        }
        return Task.FromResult(result);
    }

    // ── File metrics (vitals) ────────────────────────────────────────────

    private readonly List<FileMetricsEntity> _fileMetrics = new();
    private readonly List<ProjectHealthSummaryEntity> _healthSummaries = new();

    public Task UpsertFileMetricsBatchAsync(string repository, IReadOnlyList<FileMetricsEntity> metrics)
    {
        _fileMetrics.RemoveAll(m => m.Repository == repository);
        _fileMetrics.AddRange(metrics);
        return Task.CompletedTask;
    }

    public virtual Task<IReadOnlyList<FileMetricsEntity>> GetFileMetricsAsync(string repository, string? dotnetProject = null)
    {
        var results = _fileMetrics
            .Where(m => m.Repository == repository && (dotnetProject is null || m.DotnetProject == dotnetProject))
            .ToList();
        return Task.FromResult<IReadOnlyList<FileMetricsEntity>>(results);
    }

    public Task<FileMetricsEntity?> GetFileMetricAsync(string repository, string filePath) =>
        Task.FromResult(_fileMetrics.FirstOrDefault(m =>
            m.Repository == repository && string.Equals(m.FilePath, filePath, StringComparison.Ordinal)));

    public Task<IReadOnlyList<FileMetricsEntity>> GetHotspotsAsync(string repository, int top = 10) =>
        Task.FromResult<IReadOnlyList<FileMetricsEntity>>(
            _fileMetrics.Where(m => m.Repository == repository)
                .OrderByDescending(m => m.ConcernScore)
                .ThenByDescending(m => m.RiskScore)
                .Take(top)
                .ToList());

    public Task DeleteFileMetricsAsync(string repository)
    {
        _fileMetrics.RemoveAll(m => m.Repository == repository);
        return Task.CompletedTask;
    }

    public Task UpsertProjectHealthSummaryAsync(ProjectHealthSummaryEntity summary)
    {
        _healthSummaries.RemoveAll(s =>
            s.Repository == summary.Repository && s.DotnetProject == summary.DotnetProject);
        _healthSummaries.Add(summary);
        return Task.CompletedTask;
    }

    public virtual Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetProjectHealthSummariesAsync(string repository) =>
        Task.FromResult<IReadOnlyList<ProjectHealthSummaryEntity>>(
            _healthSummaries.Where(s => s.Repository == repository).ToList());

    public Task<IReadOnlyList<ProjectHealthSummaryEntity>> GetAllRepoHealthSummariesAsync() =>
        Task.FromResult<IReadOnlyList<ProjectHealthSummaryEntity>>(
            _healthSummaries.Where(s => string.IsNullOrEmpty(s.DotnetProject)).OrderBy(s => s.OverallHealth).ToList());

    // ── Repository health analyses (Claude-generated) ───────────────────────

    private readonly List<ProjectHealthAnalysisEntity> _healthAnalyses = new();

    public virtual Task UpsertProjectHealthAnalysisAsync(ProjectHealthAnalysisEntity analysis)
    {
        _healthAnalyses.RemoveAll(a =>
            a.Repository == analysis.Repository && a.DotnetProject == analysis.DotnetProject);
        _healthAnalyses.Add(analysis);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectHealthAnalysisEntity>> GetProjectHealthAnalysesAsync(string repository) =>
        Task.FromResult<IReadOnlyList<ProjectHealthAnalysisEntity>>(
            _healthAnalyses.Where(a => a.Repository == repository).ToList());

    // ── Security findings ──────────────────────────────────────────────

    private readonly List<SecurityFindingEntity> _securityFindings = new();
    private readonly Dictionary<string, RepositorySecuritySummaryEntity> _securitySummaries = new();

    public Task DeleteSecurityFindingsAsync(string repository)
    {
        _securityFindings.RemoveAll(f => f.Repository == repository);
        return Task.CompletedTask;
    }

    public Task DeleteProjectDiagnosticsAsync(string repository)
    {
        _projectDiagnostics.RemoveAll(d => d.Repository == repository);
        return Task.CompletedTask;
    }

    public Task UpsertProjectDiagnosticsBatchAsync(string repository, IReadOnlyList<ProjectDiagnosticEntity> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
            diagnostic.Repository = repository;

        var incomingKeys = diagnostics.Select(d => d.DiagnosticKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _projectDiagnostics.RemoveAll(d =>
            d.Repository == repository && incomingKeys.Contains(d.DiagnosticKey));
        _projectDiagnostics.AddRange(diagnostics);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectDiagnosticEntity>> GetProjectDiagnosticsAsync(string repository, string? dotnetProject = null)
    {
        var results = _projectDiagnostics
            .Where(d => d.Repository == repository && (dotnetProject is null || d.DotnetProject == dotnetProject))
            .OrderBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.LineStart ?? 0)
            .ThenBy(d => d.DiagnosticId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<ProjectDiagnosticEntity>>(results);
    }

    public Task<long> CreateProjectReviewRunAsync(ProjectReviewRunEntity run)
    {
        run.Id = _nextReviewRunId++;
        _projectReviewRuns.Add(run);
        return Task.FromResult(run.Id);
    }

    public Task UpdateProjectReviewRunStatusAsync(long reviewRunId, string status, string? overviewJson = null,
        DateTime? completedAt = null, string? error = null)
    {
        var run = _projectReviewRuns.FirstOrDefault(r => r.Id == reviewRunId);
        if (run is null)
            return Task.CompletedTask;

        run.Status = status;
        if (overviewJson is not null)
            run.OverviewJson = overviewJson;
        if (completedAt.HasValue)
            run.CompletedAt = completedAt;
        if (status is "running" or "completed" or "failed")
            run.StartedAt ??= DateTime.UtcNow;
        run.Error = error;
        return Task.CompletedTask;
    }

    public Task UpsertProjectReviewFindingsAsync(long reviewRunId, IReadOnlyList<ProjectReviewFindingEntity> findings)
    {
        _projectReviewFindings.RemoveAll(f => f.ReviewRunId == reviewRunId);

        foreach (var finding in findings)
        {
            finding.Id = _nextReviewFindingId++;
            finding.ReviewRunId = reviewRunId;
        }

        _projectReviewFindings.AddRange(findings);
        return Task.CompletedTask;
    }

    public Task<ProjectReviewRunEntity?> GetLatestProjectReviewRunAsync(string repository, string projectName)
    {
        var run = _projectReviewRuns
            .Where(r => r.Repository == repository && r.ProjectName == projectName)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefault();
        return Task.FromResult(run);
    }

    public Task<ProjectReviewRunEntity?> GetProjectReviewRunAsync(long reviewRunId)
    {
        var run = _projectReviewRuns.FirstOrDefault(r => r.Id == reviewRunId);
        return Task.FromResult(run);
    }

    public Task<IReadOnlyList<ProjectReviewFindingEntity>> GetProjectReviewFindingsAsync(long reviewRunId) =>
        Task.FromResult<IReadOnlyList<ProjectReviewFindingEntity>>(
            _projectReviewFindings.Where(f => f.ReviewRunId == reviewRunId)
                .OrderBy(f => f.Ordinal)
                .ThenBy(f => f.Id)
                .ToList());

    public Task<long> CreateRepositoryReviewRunAsync(RepositoryReviewRunEntity run)
    {
        run.Id = _nextRepositoryReviewRunId++;
        _repositoryReviewRuns.Add(run);
        return Task.FromResult(run.Id);
    }

    public Task UpdateRepositoryReviewRunStatusAsync(long reviewRunId, string status, string? overviewJson = null,
        DateTime? completedAt = null, string? error = null)
    {
        var run = _repositoryReviewRuns.FirstOrDefault(r => r.Id == reviewRunId);
        if (run is null)
            return Task.CompletedTask;

        run.Status = status;
        if (overviewJson is not null)
            run.OverviewJson = overviewJson;
        if (completedAt.HasValue)
            run.CompletedAt = completedAt;
        if (status is "running" or "completed" or "failed")
            run.StartedAt ??= DateTime.UtcNow;
        run.Error = error;
        return Task.CompletedTask;
    }

    public Task UpsertRepositoryReviewFindingsAsync(long reviewRunId, IReadOnlyList<RepositoryReviewFindingEntity> findings)
    {
        _repositoryReviewFindings.RemoveAll(f => f.ReviewRunId == reviewRunId);

        foreach (var finding in findings)
        {
            finding.Id = _nextRepositoryReviewFindingId++;
            finding.ReviewRunId = reviewRunId;
        }

        _repositoryReviewFindings.AddRange(findings);
        return Task.CompletedTask;
    }

    public Task UpsertRepositoryReviewProjectSectionsAsync(long reviewRunId,
        IReadOnlyList<RepositoryReviewProjectSectionEntity> sections)
    {
        _repositoryReviewProjectSections.RemoveAll(s => s.ReviewRunId == reviewRunId);

        foreach (var section in sections)
        {
            section.Id = _nextRepositoryReviewProjectSectionId++;
            section.ReviewRunId = reviewRunId;
        }

        _repositoryReviewProjectSections.AddRange(sections);
        return Task.CompletedTask;
    }

    public Task<RepositoryReviewRunEntity?> GetLatestRepositoryReviewRunAsync(string repo)
    {
        var run = _repositoryReviewRuns
            .Where(r => r.Repo == repo)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefault();
        return Task.FromResult(run);
    }

    public Task<RepositoryReviewRunEntity?> GetRepositoryReviewRunAsync(long reviewRunId)
    {
        var run = _repositoryReviewRuns.FirstOrDefault(r => r.Id == reviewRunId);
        return Task.FromResult(run);
    }

    public Task<IReadOnlyList<RepositoryReviewRunEntity>> GetRepositoryReviewRunsByStatusAsync(IReadOnlyList<string> statuses)
    {
        var set = new HashSet<string>(
            statuses.Where(status => !string.IsNullOrWhiteSpace(status)).Select(status => status.Trim()),
            StringComparer.OrdinalIgnoreCase);

        return Task.FromResult<IReadOnlyList<RepositoryReviewRunEntity>>(
            _repositoryReviewRuns.Where(r => set.Contains(r.Status))
                .OrderByDescending(r => r.CreatedAt)
                .ThenByDescending(r => r.Id)
                .ToList());
    }

    public Task<IReadOnlyList<RepositoryReviewFindingEntity>> GetRepositoryReviewFindingsAsync(long reviewRunId) =>
        Task.FromResult<IReadOnlyList<RepositoryReviewFindingEntity>>(
            _repositoryReviewFindings.Where(f => f.ReviewRunId == reviewRunId)
                .OrderBy(f => f.Ordinal)
                .ThenBy(f => f.Id)
                .ToList());

    public Task<IReadOnlyList<RepositoryReviewProjectSectionEntity>> GetRepositoryReviewProjectSectionsAsync(
        long reviewRunId) =>
        Task.FromResult<IReadOnlyList<RepositoryReviewProjectSectionEntity>>(
            _repositoryReviewProjectSections.Where(s => s.ReviewRunId == reviewRunId)
                .OrderBy(s => s.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Id)
                .ToList());

    public Task UpsertSecurityFindingsBatchAsync(string repository, IReadOnlyList<SecurityFindingEntity> findings)
    {
        _securityFindings.AddRange(findings);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SecurityFindingEntity>> GetSecurityFindingsAsync(string repository) =>
        Task.FromResult<IReadOnlyList<SecurityFindingEntity>>(
            _securityFindings.Where(f => f.Repository == repository).ToList());

    public Task UpsertRepositorySecuritySummaryAsync(RepositorySecuritySummaryEntity summary)
    {
        _securitySummaries[summary.Repository] = summary;
        return Task.CompletedTask;
    }

    public Task<RepositorySecuritySummaryEntity?> GetRepositorySecuritySummaryAsync(string repository) =>
        Task.FromResult(_securitySummaries.GetValueOrDefault(repository));

    // ── Cleanup operations ───────────────────────────────────────────────

    public Task DeleteSyncStateAsync(string repository)
    {
        _syncStates.Remove(repository);
        return Task.CompletedTask;
    }

    public Task DeleteAllEdgesForRepositoryAsync(string repository)
    {
        _edges.RemoveAll(e =>
        {
            var sourceNode = _nodes.FirstOrDefault(n => n.Id == e.SourceId);
            return sourceNode?.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase) == true;
        });
        return Task.CompletedTask;
    }

    public Task DeleteCrossRepoEdgesForRepositoryAsync(string repository)
    {
        _crossEdges.RemoveAll(e =>
            e.SourceRepository.Equals(repository, StringComparison.OrdinalIgnoreCase) ||
            e.TargetRepository.Equals(repository, StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }

    public Task DeleteAnalysisDataForRepositoryAsync(string repository)
    {
        _batches.RemoveAll(b => b.Repo == repository);
        _batchRequests.RemoveAll(r => _batches.All(b => b.Id != r.BatchId));
        _projectAnalyses.Keys.Where(k => k.Item1 == repository).ToList()
            .ForEach(k => _projectAnalyses.Remove(k));
        _summaries.Remove(repository);
        _healthSummaries.RemoveAll(s => s.Repository == repository);
        _healthAnalyses.RemoveAll(a => a.Repository == repository);
        _projectDiagnostics.RemoveAll(d => d.Repository == repository);
        var reviewRunIds = _projectReviewRuns.Where(r => r.Repository == repository).Select(r => r.Id).ToHashSet();
        _projectReviewRuns.RemoveAll(r => r.Repository == repository);
        _projectReviewFindings.RemoveAll(f => reviewRunIds.Contains(f.ReviewRunId));
        _securityFindings.RemoveAll(f => f.Repository == repository);
        _securitySummaries.Remove(repository);
        return Task.CompletedTask;
    }

    // ── Clusters (community detection) ────────────────────────────────

    private readonly List<RepoCluster> _repoClusters = new();

    public Task ReplaceRepoClustersAsync(IReadOnlyList<RepoCluster> clusters)
    {
        _repoClusters.Clear();
        _repoClusters.AddRange(clusters);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RepoCluster>> GetRepoClustersAsync(int level = 0) =>
        Task.FromResult<IReadOnlyList<RepoCluster>>(
            _repoClusters.Where(c => c.Level == level).OrderBy(c => c.ClusterId).ThenBy(c => c.RepositoryName).ToList());

    public Task<IReadOnlyList<RepoCluster>> GetRepoClusterMembersAsync(int clusterId, int level = 0) =>
        Task.FromResult<IReadOnlyList<RepoCluster>>(
            _repoClusters.Where(c => c.ClusterId == clusterId && c.Level == level)
                .OrderByDescending(c => c.BetweennessCentrality).ToList());

    // ── Exclusion rules ──────────────────────────────────────────────

    private readonly List<ExclusionRuleEntity> _exclusionRules = new();
    private long _nextExclusionId = 1;

    public Task<IReadOnlyList<ExclusionRuleEntity>> ListExclusionRulesAsync() =>
        Task.FromResult<IReadOnlyList<ExclusionRuleEntity>>(_exclusionRules.ToList());

    public Task<ExclusionRuleEntity?> GetExclusionRuleAsync(long id) =>
        Task.FromResult(_exclusionRules.FirstOrDefault(r => r.Id == id));

    public Task<ExclusionRuleEntity> CreateExclusionRuleAsync(ExclusionRuleEntity rule)
    {
        rule.Id = _nextExclusionId++;
        _exclusionRules.Add(rule);
        return Task.FromResult(rule);
    }

    public Task<ExclusionRuleEntity?> UpdateExclusionRuleAsync(long id, string exclusionType, string? reason)
    {
        var rule = _exclusionRules.FirstOrDefault(r => r.Id == id);
        if (rule is null) return Task.FromResult<ExclusionRuleEntity?>(null);
        rule.ExclusionType = exclusionType;
        rule.Reason = reason;
        rule.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult<ExclusionRuleEntity?>(rule);
    }

    public Task<bool> DeleteExclusionRuleAsync(long id)
    {
        var removed = _exclusionRules.RemoveAll(r => r.Id == id);
        return Task.FromResult(removed > 0);
    }

    public Task<HashSet<string>> GetSecretFilePathsAsync(string repository)
    {
        var paths = _securityFindings
            .Where(f => f.Repository == repository && f.Category == "secret" && f.FilePath is not null)
            .Select(f => f.FilePath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(paths);
    }
}
