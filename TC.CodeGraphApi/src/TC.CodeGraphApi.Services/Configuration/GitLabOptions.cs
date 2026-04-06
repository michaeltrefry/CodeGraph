namespace TC.CodeGraphApi.Services.Configuration;

public class GitLabOptions
{
    /// <summary>
    /// Base URL for GitLab API (e.g. "https://gitlab.example.com").
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Private token for GitLab API authentication.
    /// </summary>
    public string PrivateToken { get; set; } = "";

    /// <summary>
    /// Local directory where repos are cloned/cached.
    /// Each repo gets a subdirectory named after the repo (e.g. {ReposCachePath}/TC.OrdersApi).
    /// </summary>
    public string ReposCachePath { get; set; } = "";

    /// <summary>
    /// Group full paths to exclude from discovery (case-insensitive).
    /// e.g. ["ansible", "terraform", "old-svn-projects"]
    /// </summary>
    public List<string> ExcludedGroups { get; set; } = [];
}
