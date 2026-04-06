using System.Net.Http.Json;
using System.Text.Json;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services;

public class GitHubRepoProvider(
    RepositorySourceOptions sourceOptions,
    HttpClient httpClient,
    IExclusionService exclusionService,
    ILogger<GitHubRepoProvider> logger)
    : RepoProviderBase(sourceOptions.ReposCachePath, logger), IRepoProvider
{
    private readonly GitHubSourceOptions _github = sourceOptions.GitHub;

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
                : $"{_github.BaseUrl.TrimEnd('/')}/user/repos?per_page={perPage}&page={page}&sort=updated&direction=desc&affiliation=owner";

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

        return await EnsureCachedAsync(repoName, repoUrl, ToCloneUrl, ct);
    }

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
        await RunGitAsync(repoPath, "reset --hard origin/HEAD", ct);
    }
}

internal class GitHubRepoDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? FullName { get; set; }
    public string? CloneUrl { get; set; }
    public string? DefaultBranch { get; set; }
    public bool Archived { get; set; }
    public DateTime UpdatedAt { get; set; }
}

internal class GitHubSearchResult
{
    public int TotalCount { get; set; }
    public List<GitHubRepoDto>? Items { get; set; }
}
