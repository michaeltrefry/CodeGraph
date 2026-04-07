using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services;

public class GitLabRepoProvider(
    IOptions<RepositorySourceOptions> sourceOptionsAccessor,
    HttpClient httpClient,
    IExclusionService exclusionService,
    ILogger<GitLabRepoProvider> logger)
    : RepoProviderBase(sourceOptionsAccessor.Value.ReposCachePath, logger), IRepoProvider
{
    private readonly GitLabSourceOptions _gitLab = sourceOptionsAccessor.Value.GitLab;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<List<DiscoveredProject>> DiscoverProjectsAsync(CancellationToken ct = default)
    {
        var url = $"{_gitLab.BaseUrl.TrimEnd('/')}/api/v4/projects?archived=false&order_by=last_activity_at&";
        return await RequestProjects(url, ct);
    }

    public async Task<List<DiscoveredProject>> SearchProjectsAsync(string searchTerm, CancellationToken ct = default)
    {
        var url = $"{_gitLab.BaseUrl.TrimEnd('/')}/api/v4/search?scope=projects&search={searchTerm}&order_by=created_at&";
        return await RequestProjects(url, ct);
    }

    private async Task<List<DiscoveredProject>> RequestProjects(string baseUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_gitLab.PrivateToken))
            throw new InvalidOperationException("RepositorySource:GitLab:PrivateToken is not configured.");

        var allProjects = new List<DiscoveredProject>();
        var page = 1;
        const int perPage = 25;

        while (true)
        {
            var url = $"{baseUrl}sort=desc&per_page={perPage}&page={page}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("PRIVATE-TOKEN", _gitLab.PrivateToken);
            request.Headers.Add("Accept", "application/json");
            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var projects = await response.Content.ReadFromJsonAsync<List<GitLabProjectDto>>(JsonOptions, ct);
            if (projects is null || projects.Count == 0)
                break;

            foreach (var p in projects)
            {
                var namespacePath = p.PathWithNamespace ?? "";
                var lastSlash = namespacePath.LastIndexOf('/');
                var sourceGroup = lastSlash > 0 ? namespacePath[..lastSlash] : null;

                var exclusionType = await exclusionService.GetExclusionTypeAsync(p.Name ?? "", sourceGroup);
                if (exclusionType == "complete")
                {
                    logger.LogDebug("Skipping {Project} (excluded: complete)", p.PathWithNamespace);
                    continue;
                }

                allProjects.Add(new DiscoveredProject(
                    p.Id,
                    p.Name ?? "",
                    p.PathWithNamespace ?? "",
                    p.HttpUrlToRepo ?? "",
                    p.DefaultBranch ?? "main",
                    p.LastActivityAt));
            }

            // Check for next page
            if (!response.Headers.TryGetValues("x-next-page", out var nextPageValues)
                || !int.TryParse(nextPageValues.FirstOrDefault(), out var nextPage)
                || nextPage <= page)
                break;

            page = nextPage;
        }

        logger.LogInformation("Discovered {Count} projects from GitLab", allProjects.Count);

        return allProjects;
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
            logger.LogInformation("Resolved missing GitLab repo URL for {Repo} via search", repoName);
            return resolved;
        }

        discovered = await DiscoverProjectsAsync(ct);
        resolved = ResolveExactMatchUrl(repoName, discovered, allowPartial: true);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            logger.LogInformation("Resolved missing GitLab repo URL for {Repo} via discovery", repoName);
            return resolved;
        }

        logger.LogWarning("Unable to resolve GitLab repo URL for {Repo}", repoName);
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
                "GitLab search returned {StatusCode} for {Repo}; falling back to repository discovery",
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

    protected override async Task FetchAsync(string repoPath, CancellationToken ct)
    {
        // If using token auth, ensure the remote URL is HTTPS (may have been cloned via SSH previously)
        if (!string.IsNullOrWhiteSpace(_gitLab.PrivateToken))
        {
            var currentUrl = (await RunGitOutputAsync(repoPath, "remote get-url origin", ct)).Trim();
            if (currentUrl.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            {
                var httpsUrl = ToHttpsUrlWithToken(currentUrl, _gitLab.PrivateToken);
                await RunGitAsync(repoPath, $"remote set-url origin \"{httpsUrl}\"", ct);
            }
        }

        await RunGitAsync(repoPath, "fetch origin", ct);
        await ResetToFetchedHeadAsync(repoPath, ct);
    }

    internal string ToCloneUrl(string url)
    {
        if (!string.IsNullOrWhiteSpace(_gitLab.PrivateToken))
            return ToHttpsUrlWithToken(url, _gitLab.PrivateToken);

        return ToSshUrl(url);
    }

    internal static string ToHttpsUrlWithToken(string url, string token)
    {
        var uri = new Uri(url.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
            ? SshToHttps(url)
            : url);

        var path = uri.AbsolutePath.TrimStart('/');
        if (!path.EndsWith(".git"))
            path += ".git";

        return $"https://oauth2:{token}@{uri.Host}/{path}";
    }

    internal static string ToSshUrl(string url)
    {
        if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            return url;

        var uri = new Uri(url);
        var path = uri.AbsolutePath.TrimStart('/');
        if (!path.EndsWith(".git"))
            path += ".git";

        return $"git@{uri.Host}:{path}";
    }

    private static string SshToHttps(string sshUrl)
    {
        var withoutPrefix = sshUrl["git@".Length..];
        var colonIdx = withoutPrefix.IndexOf(':');
        var host = withoutPrefix[..colonIdx];
        var path = withoutPrefix[(colonIdx + 1)..];
        return $"https://{host}/{path}";
    }
}

internal class GitLabProjectDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? PathWithNamespace { get; set; }
    public string? HttpUrlToRepo { get; set; }
    public string? DefaultBranch { get; set; }
    public DateTime LastActivityAt { get; set; }
}
