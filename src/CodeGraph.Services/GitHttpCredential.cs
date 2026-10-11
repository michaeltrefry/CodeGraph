using System.Text;
using System.Text.RegularExpressions;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Services;

/// <summary>
/// HTTPS credential for git remotes, supplied to each git command instead of being stored in the
/// checkout. The token is passed as an <c>http.&lt;url&gt;.extraheader</c> through the
/// <c>GIT_CONFIG_COUNT</c>/<c>GIT_CONFIG_KEY_n</c>/<c>GIT_CONFIG_VALUE_n</c> environment, so it is
/// never written to <c>.git/config</c> and never appears in the git argument list. Because nothing
/// is persisted, a rotated token takes effect on the next git command.
/// </summary>
public sealed class GitHttpCredential
{
    private readonly string _secret;
    private readonly string _basicValue;

    private GitHttpCredential(string username, string secret)
    {
        _secret = secret;
        _basicValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{secret}"));
    }

    /// <summary>Returns null when <paramref name="secret"/> is blank (anonymous access).</summary>
    public static GitHttpCredential? Create(string username, string? secret) =>
        string.IsNullOrWhiteSpace(secret) ? null : new GitHttpCredential(username, secret.Trim());

    public static GitHttpCredential? ForGitHub(string? personalAccessToken) =>
        Create("x-access-token", personalAccessToken);

    public static GitHttpCredential? ForGitLab(string? privateToken) =>
        Create("oauth2", privateToken);

    /// <summary>The credential of the configured repository source provider, if any.</summary>
    public static GitHttpCredential? FromOptions(RepositorySourceOptions options) => options.Provider switch
    {
        RepositorySourceProvider.GitHub => ForGitHub(options.GitHub.PersonalAccessToken),
        RepositorySourceProvider.Folder => null,
        _ => ForGitLab(options.GitLab.PrivateToken)
    };

    /// <summary>
    /// Environment variables that make git send the credential to the host of
    /// <paramref name="remoteUrl"/> only. Empty for non-HTTP(S) remotes (SSH, local paths).
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentFor(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)
            || !Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return new Dictionary<string, string>();
        }

        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = $"http.{uri.Scheme}://{uri.Authority}/.extraheader",
            ["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {_basicValue}"
        };
    }

    /// <summary>Removes this credential and any URL user-info from <paramref name="text"/>.</summary>
    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return GitRemoteUrl.RedactUserInfo(text
            .Replace(_basicValue, "***", StringComparison.Ordinal)
            .Replace(_secret, "***", StringComparison.Ordinal));
    }

    public static string Redact(string text, GitHttpCredential? credential) =>
        credential?.Redact(text) ?? GitRemoteUrl.RedactUserInfo(text);

    /// <summary>
    /// What to name as the remote in a fetch/ls-remote/push against <c>origin</c>, whose
    /// configured URL is <paramref name="originUrl"/>. Without a credential, or for a non-HTTP
    /// origin, this is <c>origin</c> itself with no extra environment. With one it is origin's URL
    /// without user-info plus the environment carrying the credential, so a token still embedded
    /// in an old checkout's origin is never used.
    /// </summary>
    public static GitRemoteTarget ForOrigin(GitHttpCredential? credential, string? originUrl)
    {
        if (credential is null || string.IsNullOrWhiteSpace(originUrl))
            return new GitRemoteTarget("origin", null);

        var url = GitRemoteUrl.StripUserInfo(originUrl.Trim());
        var environment = credential.EnvironmentFor(url);
        return environment.Count == 0
            ? new GitRemoteTarget("origin", null)
            : new GitRemoteTarget(url, environment);
    }
}

/// <summary>A git remote argument and the environment to run the command with.</summary>
public sealed record GitRemoteTarget(string Remote, IReadOnlyDictionary<string, string>? Environment);

public static partial class GitRemoteUrl
{
    [GeneratedRegex(@"^(?<scheme>https?://)[^/@]*@", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingUserInfo();

    [GeneratedRegex(@"(?<scheme>https?://)[^/\s@'""]+@", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedUserInfo();

    /// <summary>
    /// <c>https://user:token@host/path</c> becomes <c>https://host/path</c>. Other URLs (SSH, local
    /// paths) are returned unchanged.
    /// </summary>
    public static string StripUserInfo(string url) =>
        string.IsNullOrEmpty(url) ? url : LeadingUserInfo().Replace(url, "${scheme}");

    /// <summary>Replaces user-info in every HTTP(S) URL inside <paramref name="text"/> with <c>***</c>.</summary>
    public static string RedactUserInfo(string text) =>
        string.IsNullOrEmpty(text) ? text : EmbeddedUserInfo().Replace(text, "${scheme}***@");
}
