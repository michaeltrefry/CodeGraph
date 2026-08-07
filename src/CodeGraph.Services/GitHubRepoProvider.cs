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
        EnsureConfigured();

        var scopes = BuildDiscoveryScopes();
        var repositories = new List<GitHubRepoDto>();
        var seenRepositoryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scope in scopes)
        {
            var scopedRepositories = await DiscoverScopeAsync(scope, ct);
            foreach (var repository in scopedRepositories)
            {
                var key = repository.Id > 0
                    ? $"id:{repository.Id}"
                    : $"name:{repository.FullName}";
                if (seenRepositoryKeys.Add(key))
                    repositories.Add(repository);
            }
        }

        var allProjects = new List<DiscoveredProject>();
        foreach (var repository in repositories)
        {
            if (repository.Archived)
                continue;

            var fullName = repository.FullName ?? "";
            var lastSlash = fullName.LastIndexOf('/');
            var sourceGroup = lastSlash > 0 ? fullName[..lastSlash] : null;

            var exclusionType = await exclusionService.GetExclusionTypeAsync(repository.Name ?? "", sourceGroup);
            if (exclusionType == "complete")
            {
                logger.LogDebug("Skipping {Project} (excluded: complete)", fullName);
                continue;
            }

            allProjects.Add(new DiscoveredProject(
                repository.Id,
                repository.Name ?? "",
                fullName,
                repository.CloneUrl ?? "",
                repository.DefaultBranch ?? "main",
                repository.UpdatedAt));
        }

        logger.LogInformation(
            "Discovered {Count} projects from {ScopeCount} GitHub scopes",
            allProjects.Count,
            scopes.Count);

        return allProjects;
    }

    public async Task<List<DiscoveredProject>> SearchProjectsAsync(string searchTerm, CancellationToken ct = default)
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

    public async Task<ResolvedRepository> ResolveRepositoryAsync(
        string repoName,
        string? localPath,
        string? repoUrl,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(localPath) && Directory.Exists(localPath))
        {
            if (!string.IsNullOrWhiteSpace(repoUrl))
                throw new InvalidOperationException("GitHub repository resolution cannot combine a local path with a remote URL.");
            logger.LogDebug("Using local path for {Repo}: {Path}", repoName, localPath);
            var canonicalPath = Path.GetFullPath(localPath);
            return new ResolvedRepository(
                canonicalPath,
                $"github-path:{canonicalPath.Replace('\\', '/')}",
                null,
                null);
        }

        repoUrl = await ResolveRepoUrlAsync(repoName, repoUrl, ct);
        if (string.IsNullOrWhiteSpace(repoUrl))
            throw new InvalidOperationException($"Unable to resolve GitHub repository URL for '{repoName}'.");
        var (canonicalIdentity, sourceGroup) = RepositoryIdentity.FromRemote("github", repoName, repoUrl);
        var resolvedPath = await EnsureCachedAsync(repoName, repoUrl, ToCloneUrl, ct);
        return new ResolvedRepository(resolvedPath, canonicalIdentity, repoUrl, sourceGroup);
    }

    public async Task<string> EnsureLocalAsync(string repoName, string? localPath, string? repoUrl, CancellationToken ct = default) =>
        (await ResolveRepositoryAsync(repoName, localPath, repoUrl, ct)).LocalPath;

    internal async Task<string?> ResolveRepoUrlAsync(string repoName, string? repoUrl, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(repoUrl))
            return repoUrl;

        var discovered = await DiscoverProjectsAsync(ct);
        var resolved = ResolveExactMatchUrl(repoName, discovered, allowPartial: true);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            logger.LogInformation("Resolved missing GitHub repo URL for {Repo} via discovery", repoName);
            return resolved;
        }

        logger.LogWarning("Unable to resolve GitHub repo URL for {Repo}", repoName);
        return null;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_github.PersonalAccessToken))
            throw new InvalidOperationException("RepositorySource:GitHub:PersonalAccessToken is not configured.");
    }

    private List<GitHubDiscoveryScope> BuildDiscoveryScopes()
    {
        var users = ParseOwnerList(_github.UserAccounts);
        var organizations = ParseOwnerList(_github.Organizations);
        var hasMultiOwnerConfiguration = users.Count > 0 || organizations.Count > 0;
        var legacyOrganizationOnly = !hasMultiOwnerConfiguration
            && !string.IsNullOrWhiteSpace(_github.Organization);
        var scopes = new List<GitHubDiscoveryScope>();

        if (!legacyOrganizationOnly && (!hasMultiOwnerConfiguration || _github.IncludeAuthenticatedUser))
        {
            scopes.Add(new GitHubDiscoveryScope(
                "authenticated user",
                $"{_github.BaseUrl.TrimEnd('/')}/user/repos"));
        }

        scopes.AddRange(users.Select(user => new GitHubDiscoveryScope(
            $"user '{user}'",
            $"{_github.BaseUrl.TrimEnd('/')}/users/{Uri.EscapeDataString(user)}/repos")));

        if (!string.IsNullOrWhiteSpace(_github.Organization))
            organizations.Insert(0, _github.Organization.Trim());

        scopes.AddRange(organizations.Select(organization => new GitHubDiscoveryScope(
            $"organization '{organization}'",
            $"{_github.BaseUrl.TrimEnd('/')}/orgs/{Uri.EscapeDataString(organization)}/repos")));

        return scopes
            .DistinctBy(scope => scope.RepositoryEndpoint, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<GitHubRepoDto>> DiscoverScopeAsync(
        GitHubDiscoveryScope scope,
        CancellationToken ct)
    {
        var repositories = new List<GitHubRepoDto>();
        const int perPage = 100;

        for (var page = 1; ; page++)
        {
            var separator = scope.RepositoryEndpoint.Contains('?') ? '&' : '?';
            var url = $"{scope.RepositoryEndpoint}{separator}per_page={perPage}&page={page}&sort=updated&direction=desc";
            if (scope.DisplayName == "authenticated user")
                url += "&affiliation=owner,collaborator,organization_member";

            using var request = CreateRequest(url);
            using var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"GitHub repository discovery for {scope.DisplayName} failed with " +
                    $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). Verify the owner name and token access.",
                    null,
                    response.StatusCode);
            }

            var pageRepositories = await response.Content.ReadFromJsonAsync<List<GitHubRepoDto>>(JsonOptions, ct);
            if (pageRepositories is null || pageRepositories.Count == 0)
                break;

            repositories.AddRange(pageRepositories);
            if (pageRepositories.Count < perPage)
                break;
        }

        logger.LogDebug(
            "Discovered {Count} repositories from GitHub {Scope}",
            repositories.Count,
            scope.DisplayName);
        return repositories;
    }

    private HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {_github.PersonalAccessToken}");
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.Add("User-Agent", "CodeGraph");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static List<string> ParseOwnerList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

        await EnsureOriginFetchRefspecAsync(repoPath, ct);
        await RunGitAsync(repoPath, "fetch origin", ct);
        await ResetToFetchedHeadAsync(repoPath, ct);
    }
}

internal sealed record GitHubDiscoveryScope(string DisplayName, string RepositoryEndpoint);

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
