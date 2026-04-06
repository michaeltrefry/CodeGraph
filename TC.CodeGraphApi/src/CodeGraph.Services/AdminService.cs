using System.Text.RegularExpressions;
using CodeGraph.Data;
using CodeGraph.Models.Messages;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Messaging;
using CodeGraph.Services.Pipeline;

namespace CodeGraph.Services;

public class AdminService(
    IMessageBus messageBus,
    IBatchAnalysisService batchService,
    IRepoProvider repoProvider,
    IGraphStore graphStore,
    IExclusionService exclusionService,
    CrossRepoLinker crossRepoLinker,
    ICommunityDetectionService communityDetection) : IAdminService
{

    public async Task<ProcessReposResponse> ProcessRepositoriesAsync(ProcessRequest request)
    {
        var published = new List<string>();

        foreach (var entry in request.Repos)
        {
            var parts = entry.Split("::", 2);
            var name = parts[0].Trim();
            var explicitPath = parts.Length == 2 ? parts[1].Trim() : null;

            // Check exclusion rules
            var exclusionType = await exclusionService.GetExclusionTypeAsync(name, null);
            if (exclusionType == "complete")
                continue;

            var message = new ProcessRepository
            {
                Name             = name,
                ShouldIndex      = request.ShouldIndex,
                ShouldAnalyze    = exclusionType == "no_analysis" ? false : request.ShouldAnalyze,
                SkipIfUpToDate   = request.SkipIfUpToDate,
                IncludeAllSource = request.IncludeAllSource
            };

            if (!string.IsNullOrWhiteSpace(explicitPath) && explicitPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(explicitPath, UriKind.Absolute, out _))
                    throw new ArgumentException($"Invalid URL for repo '{name}': {explicitPath}");
                message.GitLabUrl = explicitPath;
            }
            else if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                message.Path = explicitPath;
            }

            await messageBus.PublishAsync(message);
            published.Add(name);
        }

        return new ProcessReposResponse(published, published.Count);
    }

    public async Task<ProcessReposResponse> ReIndexAllAsync()
    {
        var published = new List<string>();
        var projects = await graphStore.ListRepositoriesAsync();
        foreach (var project in projects)
        {
            var message = new ProcessRepository
            {
                Name             = project.Name,
                Path             = project.LocalPath,
                GitLabUrl        = project.RepoUrl,
                ShouldIndex      = true,
                ShouldAnalyze    = false,
                SkipIfUpToDate   = false
            };
            await messageBus.PublishAsync(message);
            published.Add(project.Name);
        }
        return new ProcessReposResponse(published, published.Count);
    }

    public async Task LinkAsync(CancellationToken ct)
    {
        await crossRepoLinker.LinkAsync(ct);
    }

    public async Task DetectCommunitiesAsync(CancellationToken ct)
    {
        await communityDetection.DetectCommunitiesAsync(ct);
    }

    public async Task LinkAndDetectAsync(CancellationToken ct)
    {
        await crossRepoLinker.LinkAsync(ct);
        await communityDetection.DetectCommunitiesAsync(ct);
    }

    public async Task ProcessBatchAnalysisAsync(string? repo)
    {
        await batchService.ProcessCompletedBatchesAsync(repo);
    }

    public async Task<DiscoverResponse> DiscoverAsync(DiscoverRequest? request)
    {
        var allDiscovered = string.IsNullOrWhiteSpace(request?.NamePattern)
            ? await repoProvider.DiscoverProjectsAsync()
            : await repoProvider.SearchProjectsAsync(request.NamePattern);

        if (allDiscovered.Count == 0)
            return new DiscoverResponse(0, 0, 0, 0, 0, []);

        var discovered = allDiscovered;
        if (!string.IsNullOrWhiteSpace(request?.NamePattern))
        {
            var regex = new Regex(request.NamePattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
            discovered = allDiscovered.Where(p => regex.IsMatch(p.Name)).ToList();

            if (discovered.Count == 0)
                return new DiscoverResponse(allDiscovered.Count, 0, 0, 0, 0, []);
        }

        var shouldIndex = request?.ShouldIndex ?? true;
        var shouldAnalyze = request?.ShouldAnalyze ?? true;
        var skipIfUpToDate = request?.SkipIfUpToDate ?? true;
        var includeAllSource = request?.IncludeAllSource ?? false;
        var limit = request?.Limit;

        HashSet<string>? alreadySynced = null;
        if (skipIfUpToDate && limit.HasValue)
        {
            var allProjects = await graphStore.ListRepositoriesAsync();
            alreadySynced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in allProjects)
            {
                var sync = await graphStore.GetSyncStateAsync(p.Name);
                if (sync?.LastCommitSha is not null)
                    alreadySynced.Add(p.Name);
            }
        }

        var published = new List<string>();
        var skippedNames = new List<string>();
        int newCount = 0;

        foreach (var project in discovered)
        {
            var likelySynced = alreadySynced?.Contains(project.Name) == true;

            if (!likelySynced && limit.HasValue && newCount >= limit.Value)
            {
                skippedNames.Add(project.Name);
                continue;
            }

            // Extract group from PathWithNamespace (e.g. "group/subgroup/project" → "group/subgroup")
            var lastSlash = project.PathWithNamespace.LastIndexOf('/');
            var gitLabGroup = lastSlash > 0 ? project.PathWithNamespace[..lastSlash] : null;

            // Check exclusion rules
            var exclusionType = await exclusionService.GetExclusionTypeAsync(project.Name, gitLabGroup);
            if (exclusionType == "complete")
                continue;

            var effectiveShouldAnalyze = exclusionType == "no_analysis" ? false : shouldAnalyze;

            await messageBus.PublishAsync(new ProcessRepository
            {
                Name = project.Name,
                GitLabUrl = project.HttpUrlToRepo,
                GitLabGroup = gitLabGroup,
                ShouldIndex = shouldIndex,
                ShouldAnalyze = effectiveShouldAnalyze,
                SkipIfUpToDate = skipIfUpToDate,
                IncludeAllSource = includeAllSource
            });
            published.Add(project.Name);

            if (!likelySynced)
                newCount++;
        }

        return new DiscoverResponse(allDiscovered.Count, discovered.Count, published.Count, newCount, skippedNames.Count, published);
    }
}
