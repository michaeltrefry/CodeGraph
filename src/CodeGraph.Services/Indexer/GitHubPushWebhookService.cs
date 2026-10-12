using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeGraph.Services.Indexer;

public enum GitHubWebhookOutcome
{
    /// <summary>The signature is missing or invalid, or no webhook secret is configured.</summary>
    Unauthorized,
    /// <summary>A valid delivery that does not refresh an indexed default branch.</summary>
    Ignored,
    /// <summary>The repository's incremental sync was queued (or already queued for this head SHA).</summary>
    Queued
}

public sealed record GitHubWebhookResult(
    GitHubWebhookOutcome Outcome,
    string Message,
    IndexerAcceptedResponse? Accepted = null);

/// <summary>
/// Turns a signed GitHub <c>push</c> delivery to an indexed repository's default branch into that
/// repository's incremental sync (SkipIfUpToDate), so the default-branch index no longer waits for
/// the scheduled sync. Deliveries are idempotent by repository + head SHA through the durable
/// indexer submission key.
/// </summary>
public sealed class GitHubPushWebhookService(
    IOptions<RepositorySourceOptions> repositorySourceOptions,
    IGraphStore graphStore,
    IExclusionService exclusionService,
    IIndexerOperationsService indexerOperations,
    ILogger<GitHubPushWebhookService> logger)
{
    public const string RequestedBy = "github-webhook";
    private const string SignaturePrefix = "sha256=";

    public async Task<GitHubWebhookResult> HandleAsync(
        string? eventName,
        string? signatureHeader,
        byte[] body,
        CancellationToken ct = default)
    {
        var secret = repositorySourceOptions.Value.GitHub.WebhookSecret;
        if (string.IsNullOrEmpty(secret))
        {
            logger.LogWarning("Refused a GitHub webhook delivery: no webhook secret is configured.");
            return new GitHubWebhookResult(GitHubWebhookOutcome.Unauthorized, "Webhook secret is not configured.");
        }

        if (!IsValidSignature(secret, signatureHeader, body))
        {
            logger.LogWarning("Refused a GitHub webhook delivery with a missing or invalid signature.");
            return new GitHubWebhookResult(GitHubWebhookOutcome.Unauthorized, "Missing or invalid signature.");
        }

        if (!string.Equals(eventName, "push", StringComparison.OrdinalIgnoreCase))
            return Ignored($"Event '{eventName}' is not handled.");

        PushPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PushPayload>(body);
        }
        catch (JsonException)
        {
            return Ignored("Payload is not a push event.");
        }

        var repository = payload?.Repository;
        if (payload is null || repository is null || string.IsNullOrWhiteSpace(payload.Ref))
            return Ignored("Payload is not a push event.");

        if (string.IsNullOrWhiteSpace(repository.DefaultBranch)
            || !string.Equals(payload.Ref, $"refs/heads/{repository.DefaultBranch}", StringComparison.Ordinal))
            return Ignored($"Ref '{payload.Ref}' is not the default branch.");

        if (payload.Deleted || string.IsNullOrWhiteSpace(payload.After) || payload.After.All(c => c == '0'))
            return Ignored("Push deleted the branch.");

        var project = await FindProjectAsync(repository);
        if (project is null)
            return Ignored($"Repository '{repository.FullName}' is not indexed.");

        var exclusionType = await exclusionService.GetExclusionTypeAsync(project.Name, project.SourceGroup);
        if (exclusionType == "complete")
            return Ignored($"Repository '{project.Name}' is excluded.");

        var sha = payload.After.Trim().ToLowerInvariant();
        IndexerAcceptedResponse accepted;
        try
        {
            accepted = await indexerOperations.StartProcessRepositoriesAsync(
                RequestedBy,
                new ProcessRequest
                {
                    Repos = [project.Name],
                    ShouldIndex = true,
                    ShouldAnalyze = exclusionType != "no_analysis",
                    SkipIfUpToDate = true
                },
                $"github-push:{project.Name}:{sha}",
                ct);
        }
        catch (IndexerSubmissionConflictException)
        {
            return Ignored($"A sync for {project.Name}@{sha} was already submitted.");
        }

        logger.LogInformation(
            "GitHub push to {Repo}@{Sha} {Disposition} indexer run {RunId}.",
            project.Name, sha, accepted.Duplicate ? "matched existing" : "queued", accepted.RunId);
        return new GitHubWebhookResult(
            GitHubWebhookOutcome.Queued,
            accepted.Duplicate
                ? $"A sync for {project.Name}@{sha} is already queued."
                : $"Queued incremental sync for {project.Name}@{sha}.",
            accepted);
    }

    internal static bool IsValidSignature(string secret, string? signatureHeader, byte[] body)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader)
            || !signatureHeader.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signatureHeader.AsSpan(SignaturePrefix.Length).Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private async Task<ProjectInfo?> FindProjectAsync(PushRepository repository)
    {
        var payloadUrls = new[] { repository.CloneUrl, repository.HtmlUrl }
            .Select(NormalizeRepoUrl)
            .Where(url => url is not null)
            .ToHashSet(StringComparer.Ordinal);

        var projects = await graphStore.ListRepositoriesAsync();
        return projects.FirstOrDefault(p =>
                   NormalizeRepoUrl(p.RepoUrl) is { } url && payloadUrls.Contains(url))
               ?? projects.FirstOrDefault(p =>
                   !string.IsNullOrWhiteSpace(p.SourceGroup)
                   && string.Equals($"{p.SourceGroup}/{p.Name}", repository.FullName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>host/owner/repo, lower-cased, without credentials, trailing slash or ".git".</summary>
    private static string? NormalizeRepoUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        return $"{uri.Host}{path}".ToLowerInvariant();
    }

    private static GitHubWebhookResult Ignored(string message) => new(GitHubWebhookOutcome.Ignored, message);

    private sealed record PushPayload(
        [property: JsonPropertyName("ref")] string? Ref,
        [property: JsonPropertyName("after")] string? After,
        [property: JsonPropertyName("deleted")] bool Deleted,
        [property: JsonPropertyName("repository")] PushRepository? Repository);

    private sealed record PushRepository(
        [property: JsonPropertyName("full_name")] string? FullName,
        [property: JsonPropertyName("default_branch")] string? DefaultBranch,
        [property: JsonPropertyName("clone_url")] string? CloneUrl,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);
}
