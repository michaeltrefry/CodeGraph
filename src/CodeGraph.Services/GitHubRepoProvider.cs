using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services;

public class GitHubRepoProvider(
    IOptions<RepositorySourceOptions> sourceOptionsAccessor,
    HttpClient httpClient,
    IExclusionService exclusionService,
    ILogger<GitHubRepoProvider> logger)
    : RepoProviderBase(sourceOptionsAccessor.Value.ReposCachePath, logger), IRepoProvider
{
    private readonly GitHubSourceOptions _github = sourceOptionsAccessor.Value.GitHub;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<List<DiscoveredProject>> DiscoverProjectsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_github.PersonalAccessToken))
            throw new InvalidOperationException("RepositorySource:GitHub:PersonalAccessToken is not configured.");

        var allProjects = new List<DiscoveredProject>();
        var page = 1;
        const int perPage = 100;

        while (true)
        {
            var url = !string.IsNullOrWhiteSpace(_github.Organization)
                ? $"{_github.BaseUrl.TrimEnd('/')}/orgs/{_github.Organization}/repos?per_page={perPage}&page={page}&sort=updated&direction=desc"
                : $"{_github.BaseUrl.TrimEnd('/')}/user/repos?per_page={perPage}&page={page}&sort=updated&direction=desc&affiliation=owner,collaborator,organization_member";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"Bearer {_github.PersonalAccessToken}");
            request.Headers.Add("Accept", "application/vnd.github+json");
            request.Headers.Add("User-Agent", "CodeGraph");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var repos = await response.Content.ReadFromJsonAsync<List<GitHubRepoDto>>(JsonOptions, ct);
            if (repos is null || repos.Count == 0)
                break;

            foreach (var r in repos)
            {
                if (r.Archived) continue;

                var fullName = r.FullName ?? "";
                var lastSlash = fullName.LastIndexOf('/');
                var sourceGroup = lastSlash > 0 ? fullName[..lastSlash] : null;

                var exclusionType = await exclusionService.GetExclusionTypeAsync(r.Name ?? "", sourceGroup);
                if (exclusionType == "complete")
                {
                    logger.LogDebug("Skipping {Project} (excluded: complete)", fullName);
                    continue;
                }

                allProjects.Add(new DiscoveredProject(
                    r.Id,
                    r.Name ?? "",
                    fullName,
                    r.CloneUrl ?? "",
                    r.DefaultBranch ?? "main",
                    r.UpdatedAt));
            }

            if (repos.Count < perPage)
                break;

            page++;
        }

        logger.LogInformation("Discovered {Count} projects from GitHub", allProjects.Count);

        return allProjects;
    }

    public async Task<List<DiscoveredProject>> SearchProjectsAsync(string searchTerm, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_github.PersonalAccessToken))
            throw new InvalidOperationException("RepositorySource:GitHub:PersonalAccessToken is not configured.");

        if (string.IsNullOrWhiteSpace(_github.Organization))
        {
            var discovered = await DiscoverProjectsAsync(ct);
            var normalizedSearchTerm = NormalizeRepoName(searchTerm);
            return discovered
                .Where(p => p.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
                    || p.PathWithNamespace.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
                    || NormalizeRepoName(p.Name).Equals(normalizedSearchTerm, StringComparison.Ordinal)
                    || NormalizeRepoName(p.PathWithNamespace).Contains(normalizedSearchTerm, StringComparison.Ordinal))
                .ToList();
        }

        var qualifier = !string.IsNullOrWhiteSpace(_github.Organization)
            ? $"org:{_github.Organization}"
            : "user:@me";

        var url = $"{_github.BaseUrl.TrimEnd('/')}/search/repositories?q={Uri.EscapeDataString(searchTerm)}+{qualifier}&per_page=100&sort=updated";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {_github.PersonalAccessToken}");
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.Add("User-Agent", "CodeGraph");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var searchResult = await response.Content.ReadFromJsonAsync<GitHubSearchResult>(JsonOptions, ct);
        if (searchResult?.Items is null)
            return [];

        var results = new List<DiscoveredProject>();
        foreach (var r in searchResult.Items)
        {
            if (r.Archived) continue;

            var fullName = r.FullName ?? "";
            var lastSlash = fullName.LastIndexOf('/');
            var sourceGroup = lastSlash > 0 ? fullName[..lastSlash] : null;

            var exclusionType = await exclusionService.GetExclusionTypeAsync(r.Name ?? "", sourceGroup);
            if (exclusionType == "complete") continue;

            results.Add(new DiscoveredProject(
                r.Id,
                r.Name ?? "",
                fullName,
                r.CloneUrl ?? "",
                r.DefaultBranch ?? "main",
                r.UpdatedAt));
        }

        return results;
    }

    public async Task<string> EnsureLocalAsync(string repoName, string? localPath, string? repoUrl, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(localPath) && Directory.Exists(localPath))
        {
            logger.LogDebug("Using local path for {Repo}: {Path}", repoName, localPath);
            return localPath;
        }

        repoUrl = await ResolveRepoUrlAsync(repoName, repoUrl, ct);
        return await EnsureCachedAsync(repoName, repoUrl, ToCloneUrl, ct);
    }

    internal async Task<string?> ResolveRepoUrlAsync(string repoName, string? repoUrl, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(repoUrl))
            return repoUrl;

        var discovered = await TrySearchProjectsAsync(repoName, ct);
        var resolved = ResolveExactMatchUrl(repoName, discovered, allowPartial: false);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            logger.LogInformation("Resolved missing GitHub repo URL for {Repo} via search", repoName);
            return resolved;
        }

        discovered = await DiscoverProjectsAsync(ct);
        resolved = ResolveExactMatchUrl(repoName, discovered, allowPartial: true);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            logger.LogInformation("Resolved missing GitHub repo URL for {Repo} via discovery", repoName);
            return resolved;
        }

        logger.LogWarning("Unable to resolve GitHub repo URL for {Repo}", repoName);
        return null;
    }

    private async Task<List<DiscoveredProject>> TrySearchProjectsAsync(string repoName, CancellationToken ct)
    {
        try
        {
            return await SearchProjectsAsync(repoName, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            logger.LogWarning(
                "GitHub search returned {StatusCode} for {Repo}; falling back to repository discovery",
                ex.StatusCode, repoName);
            return [];
        }
    }

    private static string? ResolveExactMatchUrl(string repoName, IEnumerable<DiscoveredProject> projects, bool allowPartial)
    {
        var normalizedRepoName = NormalizeRepoName(repoName);
        var exactMatches = FindDistinctUrls(projects,
            p => p.Name.Equals(repoName, StringComparison.OrdinalIgnoreCase));
        if (exactMatches.Count > 0)
            return ResolveSingleUrl(repoName, exactMatches);

        var normalizedMatches = FindDistinctUrls(projects,
            p => NormalizeRepoName(p.Name).Equals(normalizedRepoName, StringComparison.Ordinal));
        if (normalizedMatches.Count > 0)
            return ResolveSingleUrl(repoName, normalizedMatches);

        if (!allowPartial)
            return null;

        var partialMatches = FindDistinctUrls(projects,
            p =>
            {
                var normalizedName = NormalizeRepoName(p.Name);
                var normalizedPath = NormalizeRepoName(p.PathWithNamespace);
                return normalizedName.Contains(normalizedRepoName, StringComparison.Ordinal)
                    || normalizedRepoName.Contains(normalizedName, StringComparison.Ordinal)
                    || normalizedPath.Contains(normalizedRepoName, StringComparison.Ordinal);
            });
        if (partialMatches.Count > 0)
            return ResolveSingleUrl(repoName, partialMatches);

        return null;
    }

    private static string ResolveSingleUrl(string repoName, List<string> matches)
    {
        return matches.Count switch
        {
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Multiple remote URLs found for repository '{repoName}'.")
        };
    }

    private static List<string> FindDistinctUrls(IEnumerable<DiscoveredProject> projects,
        Func<DiscoveredProject, bool> predicate)
    {
        return projects
            .Where(predicate)
            .Select(p => p.HttpUrlToRepo)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeRepoName(string repoName) =>
        Regex.Replace(repoName, "[^A-Za-z0-9]+", "").ToLowerInvariant();

    private string ToCloneUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(_github.PersonalAccessToken))
            return url;

        // Embed PAT in HTTPS URL: https://x-access-token:{token}@github.com/owner/repo.git
        var uri = new Uri(url);
        var path = uri.AbsolutePath.TrimStart('/');
        if (!path.EndsWith(".git"))
            path += ".git";

        return $"https://x-access-token:{_github.PersonalAccessToken}@{uri.Host}/{path}";
    }

    protected override async Task FetchAsync(string repoPath, CancellationToken ct)
    {
        // Ensure HTTPS URL with token for fetch
        if (!string.IsNullOrWhiteSpace(_github.PersonalAccessToken))
        {
            var currentUrl = (await RunGitOutputAsync(repoPath, "remote get-url origin", ct)).Trim();
            if (!currentUrl.Contains("x-access-token"))
            {
                var httpsUrl = ToCloneUrl(currentUrl);
                await RunGitAsync(repoPath, $"remote set-url origin \"{httpsUrl}\"", ct);
            }
        }

        await RunGitAsync(repoPath, "fetch origin", ct);
        await ResetToFetchedHeadAsync(repoPath, ct);
    }
}

internal class GitHubRepoDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }

    [JsonPropertyName("clone_url")]
    public string? CloneUrl { get; set; }

    [JsonPropertyName("default_branch")]
    public string? DefaultBranch { get; set; }

    [JsonPropertyName("archived")]
    public bool Archived { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

internal class GitHubSearchResult
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("items")]
    public List<GitHubRepoDto>? Items { get; set; }
}
