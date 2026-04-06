using System.Net.Http.Json;
using System.Text.Json;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services;

public class GitLabRepoProvider(
    RepositorySourceOptions sourceOptions,
    HttpClient httpClient,
    IExclusionService exclusionService,
    ILogger<GitLabRepoProvider> logger)
    : RepoProviderBase(sourceOptions.ReposCachePath, logger), IRepoProvider
{
    private readonly GitLabSourceOptions _gitLab = sourceOptions.GitLab;

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

        return await EnsureCachedAsync(repoName, repoUrl, ToCloneUrl, ct);
    }

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
        await RunGitAsync(repoPath, "reset --hard origin/HEAD", ct);
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
