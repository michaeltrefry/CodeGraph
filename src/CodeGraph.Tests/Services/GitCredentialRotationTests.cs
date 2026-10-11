using System.Text;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Overlays;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// The repository source token must never be persisted in a checkout's origin URL: it is passed
/// per git command, so rotating the token takes effect on the next fetch.
/// </summary>
public sealed class GitCredentialRotationTests : IDisposable
{
    private const string OldToken = "ghp_OLDexpiredTOKEN0000";
    private const string NewToken = "ghp_NEWrotatedTOKEN1111";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-cred-{Guid.NewGuid():N}");

    public GitCredentialRotationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("https://github.com/acme/repo", "https://github.com/acme/repo.git")]
    [InlineData("https://github.com/acme/repo.git", "https://github.com/acme/repo.git")]
    [InlineData("https://x-access-token:" + OldToken + "@github.com/acme/repo.git", "https://github.com/acme/repo.git")]
    public void GitHubCloneAndOriginUrl_NeverContainToken(string input, string expected)
    {
        var provider = CreateGitHubProvider(NewToken);

        provider.ToCloneUrl(input).ShouldBe(expected);
    }

    [Fact]
    public void GitHubCloneUrl_WithoutToken_KeepsPublicUrl()
    {
        var provider = CreateGitHubProvider(personalAccessToken: "");

        provider.ToCloneUrl("https://github.com/acme/public-repo.git").ShouldBe("https://github.com/acme/public-repo.git");
        var psi = provider.CreateGitStartInfo(_root, "fetch origin", "https://github.com/acme/public-repo.git");
        psi.Environment.ContainsKey("GIT_CONFIG_COUNT").ShouldBeFalse();
    }

    [Fact]
    public void GitLabOriginUrl_DropsEmbeddedToken()
    {
        GitLabRepoProvider.ToHttpsUrl("https://oauth2:" + OldToken + "@gitlab.example.com/group/repo.git")
            .ShouldBe("https://gitlab.example.com/group/repo.git");
        GitLabRepoProvider.ToHttpsUrl("git@gitlab.example.com:group/repo.git")
            .ShouldBe("https://gitlab.example.com/group/repo.git");
    }

    [Fact]
    public void GitCommand_PassesCurrentTokenThroughEnvironment_NotArguments()
    {
        var provider = CreateGitHubProvider(NewToken);

        var psi = provider.CreateGitStartInfo(_root, "fetch origin", "https://github.com/acme/repo.git");

        psi.Arguments.ShouldNotContain(NewToken);
        psi.Environment["GIT_TERMINAL_PROMPT"].ShouldBe("0");
        psi.Environment["GIT_CONFIG_COUNT"].ShouldBe("1");
        psi.Environment["GIT_CONFIG_KEY_0"].ShouldBe("http.https://github.com/.extraheader");
        psi.Environment["GIT_CONFIG_VALUE_0"].ShouldBe(
            "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + NewToken)));
    }

    [Fact]
    public void Redact_RemovesTokenItsHeaderAndUrlUserInfo()
    {
        var credential = GitHttpCredential.ForGitHub(NewToken)!;
        var header = credential.EnvironmentFor("https://github.com/acme/repo.git")["GIT_CONFIG_VALUE_0"];

        var redacted = credential.Redact(
            $"remote: Invalid username or token. {NewToken} {header} 'https://x-access-token:{OldToken}@github.com/acme/repo.git/'");

        redacted.ShouldNotContain(NewToken);
        redacted.ShouldNotContain(OldToken);
        redacted.ShouldNotContain(header["Authorization: Basic ".Length..]);
        redacted.ShouldContain("https://***@github.com/acme/repo.git/");
    }

    [Fact]
    public async Task FailedGitCommand_DoesNotLeakTokenInException()
    {
        var repo = InitRepository("redact");
        var provider = new TestGitHubProvider(CreateOptions(NewToken));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => provider.RunGitPublicAsync(
            repo,
            $"remote set-url no-such-remote \"https://x-access-token:{NewToken}@github.com/acme/repo.git\""));

        ex.Message.ShouldNotContain(NewToken);
        ex.Message.ShouldContain("https://***@github.com/acme/repo.git");
    }

    [Fact]
    public async Task Fetch_RewritesTokenizedOriginOfExistingCheckout_ThenFetches()
    {
        var remote = CreateRemoteWithCommit();
        var checkout = InitRepository("checkout");
        const string cleanUrl = "https://127.0.0.1:9/acme/repo.git";
        // An origin persisted by the old code, with a now-expired token baked in.
        GitFixture.Run(checkout, "remote", "add", "origin", $"https://x-access-token:{OldToken}@127.0.0.1:9/acme/repo.git");
        // Only the token-free URL is routed to the local remote; the tokenized one points at a
        // closed port, so fetching it fails.
        GitFixture.Run(checkout, "config", $"url.{remote}.insteadOf", cleanUrl);

        var provider = new TestGitHubProvider(CreateOptions(NewToken));
        await provider.FetchPublicAsync(checkout);

        GitFixture.Run(checkout, "config", "--get", "remote.origin.url").ShouldBe(cleanUrl);
        File.ReadAllText(Path.Combine(checkout, ".git", "config")).ShouldNotContain(OldToken);
        File.ReadAllText(Path.Combine(checkout, ".git", "config")).ShouldNotContain(NewToken);
        GitFixture.Run(checkout, "rev-parse", "HEAD").ShouldBe(GitFixture.Run(remote, "rev-parse", "main"));
    }

    [Fact]
    public async Task OverlayLsRemote_UsesTokenFreeOriginUrlWithCurrentToken_AndRedactsErrors()
    {
        var git = new ScriptedOverlayGit(
            Options.Create(CreateOptions(NewToken).Value),
            originUrl: $"https://x-access-token:{OldToken}@github.com/acme/repo.git",
            lsRemoteStdErr: $"fatal: Authentication failed for 'https://x-access-token:{OldToken}@github.com/acme/repo.git/' {NewToken}");

        var ex = await Should.ThrowAsync<BranchOverlayTransientException>(
            () => git.ResolveRemoteRefAsync(_root, "feature/x", CancellationToken.None));

        var lsRemote = git.Calls.Single(call => call.Args[0] == "ls-remote");
        lsRemote.Args[1].ShouldBe("https://github.com/acme/repo.git");
        lsRemote.Environment.ShouldNotBeNull();
        lsRemote.Environment!["GIT_CONFIG_VALUE_0"].ShouldBe(
            "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + NewToken)));
        ex.Message.ShouldNotContain(OldToken);
        ex.Message.ShouldNotContain(NewToken);
    }

    private string CreateRemoteWithCommit()
    {
        var seed = InitRepository("seed");
        GitFixture.Write(seed, "README.md", "hello");
        GitFixture.Run(seed, "add", "README.md");
        GitFixture.Run(seed, "commit", "-m", "initial");
        var remote = Path.Combine(_root, "remote.git");
        GitFixture.Run(_root, "clone", "--bare", seed, remote);
        return remote;
    }

    private string InitRepository(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        GitFixture.Run(path, "init", "-b", "main");
        return path;
    }

    private static IOptions<RepositorySourceOptions> CreateOptions(string personalAccessToken) =>
        Options.Create(new RepositorySourceOptions
        {
            Provider = RepositorySourceProvider.GitHub,
            ReposCachePath = Path.Combine(Path.GetTempPath(), $"codegraph-cache-{Guid.NewGuid():N}"),
            GitHub = new GitHubSourceOptions
            {
                BaseUrl = "https://api.github.com",
                PersonalAccessToken = personalAccessToken
            }
        });

    private static GitHubRepoProvider CreateGitHubProvider(string personalAccessToken) =>
        new TestGitHubProvider(CreateOptions(personalAccessToken));

    private sealed class TestGitHubProvider(IOptions<RepositorySourceOptions> options)
        : GitHubRepoProvider(options, new HttpClient(), null!, NullLogger<GitHubRepoProvider>.Instance)
    {
        public Task FetchPublicAsync(string repoPath) => FetchAsync(repoPath, CancellationToken.None);

        public Task RunGitPublicAsync(string repoPath, string arguments) =>
            RunGitAsync(repoPath, arguments, CancellationToken.None);
    }

    private sealed record GitCall(IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Environment);

    private sealed class ScriptedOverlayGit(
        IOptions<RepositorySourceOptions> options,
        string originUrl,
        string lsRemoteStdErr) : BranchOverlayGit(options)
    {
        public List<GitCall> Calls { get; } = [];

        protected override Task<GitResult> RunAsync(
            string repoPath,
            IReadOnlyList<string> args,
            CancellationToken ct,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add(new GitCall(args, environment));
            return Task.FromResult(args switch
            {
                ["remote", "get-url", "origin"] => new GitResult(0, Encoding.UTF8.GetBytes(originUrl + "\n"), ""),
                ["ls-remote", ..] => new GitResult(128, [], lsRemoteStdErr),
                _ => new GitResult(1, [], "")
            });
        }
    }
}
