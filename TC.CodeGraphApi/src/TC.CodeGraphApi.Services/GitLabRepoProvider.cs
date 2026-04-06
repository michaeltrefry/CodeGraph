using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TC.CodeGraphApi.Services.Configuration;

namespace TC.CodeGraphApi.Services;

public class GitLabRepoProvider(
    GitLabOptions options,
    HttpClient httpClient,
    IExclusionService exclusionService,
    ILogger<GitLabRepoProvider> logger)
    : IRepoProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<List<DiscoveredProject>> DiscoverProjectsAsync(CancellationToken ct = default)
    {
        var url = $"{options.BaseUrl.TrimEnd('/')}/api/v4/projects?archived=false&order_by=last_activity_at&";
        return await RequestProjects(url, ct);
    }

    public async Task<List<DiscoveredProject>> SearchProjectsAsync(string searchTerm, CancellationToken ct = default)
    {
        var url = $"{options.BaseUrl.TrimEnd('/')}/api/v4/search?scope=projects&search={searchTerm}&order_by=created_at&";
        return await RequestProjects(url, ct);
    }

    private async Task<List<DiscoveredProject>> RequestProjects(string baseUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.PrivateToken))
            throw new InvalidOperationException("GitLab:PrivateToken is not configured.");

        var allProjects = new List<DiscoveredProject>();
        var page = 1;
        const int perPage = 25;

        
        while (true)
        {
            var url = $"{baseUrl}sort=desc&per_page={perPage}&page={page}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("PRIVATE-TOKEN", options.PrivateToken);
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
                var gitLabGroup = lastSlash > 0 ? namespacePath[..lastSlash] : null;

                var exclusionType = await exclusionService.GetExclusionTypeAsync(p.Name ?? "", gitLabGroup);
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

    public async Task<string> EnsureLocalAsync(string repoName, string? localPath, string? gitLabUrl, CancellationToken ct = default)
    {
        // If a local path is provided and exists, use it directly
        if (!string.IsNullOrWhiteSpace(localPath) && Directory.Exists(localPath))
        {
            logger.LogDebug("Using local path for {Repo}: {Path}", repoName, localPath);
            return localPath;
        }

        
        if (string.IsNullOrWhiteSpace(options.ReposCachePath))
            throw new InvalidOperationException(
                "GitLab:ReposCachePath is not configured. Set CodeGraph:GitLab:ReposCachePath in appsettings.");

        var cachedPath = Path.Combine(options.ReposCachePath, repoName);

        // If the repo is already cached locally, fetch latest
        if (Directory.Exists(Path.Combine(cachedPath, ".git")))
        {
            logger.LogInformation("Fetching latest for {Repo} in {Path}", repoName, cachedPath);
            await GitFetchAsync(cachedPath, ct);
            return cachedPath;
        }

        // Clone from GitLab
        if (string.IsNullOrWhiteSpace(gitLabUrl))
            throw new InvalidOperationException(
                $"No local path, cached repo, or GitLab URL available for '{repoName}'.");

        var cloneUrl = ToCloneUrl(gitLabUrl);
        logger.LogInformation("Cloning {Repo} from {Url} into {Path}", repoName, cloneUrl, cachedPath);
        await GitCloneAsync(cloneUrl, cachedPath, ct);

        return cachedPath;
    }

    private async Task GitCloneAsync(string sshUrl, string targetPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        // No --branch: clone whatever the remote HEAD points to (main or master)
        await RunGitAsync(null, $"clone --single-branch \"{sshUrl}\" \"{targetPath}\"", ct);
    }

    private async Task GitFetchAsync(string repoPath, CancellationToken ct)
    {
        // If using token auth, ensure the remote URL is HTTPS (may have been cloned via SSH previously)
        if (!string.IsNullOrWhiteSpace(options.PrivateToken))
        {
            var currentUrl = (await RunGitOutputAsync(repoPath, "remote get-url origin", ct)).Trim();
            if (currentUrl.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            {
                var httpsUrl = ToHttpsUrlWithToken(currentUrl, options.PrivateToken);
                await RunGitAsync(repoPath, $"remote set-url origin \"{httpsUrl}\"", ct);
            }
        }

        await RunGitAsync(repoPath, "fetch origin", ct);
        // Reset to whatever the remote default branch is
        await RunGitAsync(repoPath, "reset --hard origin/HEAD", ct);
    }

    private async Task<string> RunGitOutputAsync(string? workingDir, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {arguments}");

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return stdout;
    }

    private async Task RunGitAsync(string? workingDir, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {arguments}");

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            logger.LogError("git {Args} failed (exit {Code}): {StdErr}", arguments, proc.ExitCode, stderr);
            throw new InvalidOperationException($"git {arguments} failed with exit code {proc.ExitCode}: {stderr}");
        }

        if (!string.IsNullOrWhiteSpace(stderr))
            logger.LogDebug("git {Args} stderr: {StdErr}", arguments, stderr);
    }

    /// <summary>
    /// Returns the clone URL for the given GitLab repo.
    /// When a PrivateToken is configured, returns an HTTPS URL with the token embedded
    /// (no SSH key required). Otherwise falls back to SSH format.
    /// </summary>
    internal string ToCloneUrl(string url)
    {
        if (!string.IsNullOrWhiteSpace(options.PrivateToken))
            return ToHttpsUrlWithToken(url, options.PrivateToken);

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
        // git@gitlab.tcdevops.com:group/repo.git → https://gitlab.tcdevops.com/group/repo.git
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
