namespace CodeGraph.Services.Configuration;

public enum RepositorySourceProvider
{
    GitLab,
    GitHub,
    Folder
}

public class RepositorySourceOptions
{
    /// <summary>
    /// Which provider to use for repository discovery and cloning.
    /// </summary>
    public RepositorySourceProvider Provider { get; set; } = RepositorySourceProvider.GitLab;

    /// <summary>
    /// Local directory where repos are cloned/cached.
    /// Each repo gets a subdirectory named after the repo (e.g. {ReposCachePath}/TC.OrdersApi).
    /// Used by GitLab and GitHub providers.
    /// </summary>
    public string ReposCachePath { get; set; } = "";

    /// <summary>
    /// Group full paths to exclude from discovery (case-insensitive).
    /// e.g. ["ansible", "terraform", "old-svn-projects"]
    /// </summary>
    public List<string> ExcludedGroups { get; set; } = [];

    /// <summary>GitLab-specific settings.</summary>
    public GitLabSourceOptions GitLab { get; set; } = new();

    /// <summary>GitHub-specific settings.</summary>
    public GitHubSourceOptions GitHub { get; set; } = new();

    /// <summary>Folder-based provider settings.</summary>
    public FolderSourceOptions Folder { get; set; } = new();
}

public class GitLabSourceOptions
{
    /// <summary>
    /// Base URL for GitLab API (e.g. "https://gitlab.example.com").
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Private token for GitLab API authentication.
    /// </summary>
    public string PrivateToken { get; set; } = "";
}

public class GitHubSourceOptions
{
    /// <summary>
    /// Base URL for GitHub API. Defaults to "https://api.github.com" for github.com.
    /// Set to "https://github.example.com/api/v3" for GitHub Enterprise.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.github.com";

    /// <summary>
    /// Personal access token (classic) or fine-grained PAT for GitHub API.
    /// </summary>
    public string PersonalAccessToken { get; set; } = "";

    /// <summary>
    /// GitHub organization to discover repos from. If empty, discovers from the authenticated user.
    /// </summary>
    public string Organization { get; set; } = "";
}

public class FolderSourceOptions
{
    /// <summary>
    /// Root directory containing repository directories.
    /// Each immediate subdirectory that contains a .git folder is treated as a repo.
    /// </summary>
    public string RootPath { get; set; } = "";
}
