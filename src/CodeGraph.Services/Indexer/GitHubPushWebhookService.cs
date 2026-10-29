using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Models.Work;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Work;
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
    Queued,
    /// <summary>A valid delivery recorded as work evidence (and not queued for indexing).</summary>
    Recorded
}

public sealed record GitHubWebhookResult(
    GitHubWebhookOutcome Outcome,
    string Message,
    IndexerAcceptedResponse? Accepted = null,
    GitHubEvidenceResult? Evidence = null);

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
    ILogger<GitHubPushWebhookService> logger,
    IGitHubWorkEvidenceService? workEvidence = null)
{
    public const string RequestedBy = "github-webhook";
    private const string SignaturePrefix = "sha256=";

    // The events that can be work evidence (sc-27870).
    private static readonly HashSet<string> EvidenceEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "push", "pull_request", "pull_request_review", "pull_request_review_comment", "issue_comment"
    };

    public async Task<GitHubWebhookResult> HandleAsync(
        string? eventName,
        string? signatureHeader,
        byte[] body,
        CancellationToken ct = default,
        string? deliveryId = null)
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

        // Indexing first and unchanged; work evidence is added on top of its answer.
        var indexing = await HandleIndexingAsync(eventName, body, ct);
        if (workEvidence is null || !EvidenceEvents.Contains(eventName ?? ""))
            return indexing;

        var evidence = await workEvidence.HandleAsync(eventName, body, deliveryId, await RepositoryNamesAsync(body), ct);
        if (evidence.Items.Count == 0)
            return indexing;

        var summary = $"Recorded work evidence on {string.Join(", ", evidence.Items.Select(item => item.Key))}.";
        return indexing.Outcome == GitHubWebhookOutcome.Ignored
            ? new GitHubWebhookResult(GitHubWebhookOutcome.Recorded, summary, Evidence: evidence)
            : indexing with { Evidence = evidence, Message = $"{indexing.Message} {summary}" };
    }

    // The names CodeGraph knows the payload's repository by, for matching a project's repositories.
    private async Task<IReadOnlyList<string>> RepositoryNamesAsync(byte[] body)
    {
        try
        {
            var repository = JsonSerializer.Deserialize<EventEnvelope>(body)?.Repository;
            return repository is null || await FindRepositoryAsync(repository) is not { } indexed ? [] : [indexed.Name];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<GitHubWebhookResult> HandleIndexingAsync(string? eventName, byte[] body, CancellationToken ct)
    {
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

        var indexed = await FindRepositoryAsync(repository);
        if (indexed is null)
            return Ignored($"Repository '{repository.FullName}' is not indexed.");

        var exclusionType = await exclusionService.GetExclusionTypeAsync(indexed.Name, indexed.SourceGroup);
        if (exclusionType == "complete")
            return Ignored($"Repository '{indexed.Name}' is excluded.");

        var sha = payload.After.Trim().ToLowerInvariant();
        IndexerAcceptedResponse accepted;
        try
        {
            accepted = await indexerOperations.StartProcessRepositoriesAsync(
                RequestedBy,
                new ProcessRequest
                {
                    Repos = [indexed.Name],
                    ShouldIndex = true,
                    ShouldAnalyze = exclusionType != "no_analysis",
                    SkipIfUpToDate = true
                },
                $"github-push:{indexed.Name}:{sha}",
                ct);
        }
        catch (IndexerSubmissionConflictException)
        {
            return Ignored($"A sync for {indexed.Name}@{sha} was already submitted.");
        }

        logger.LogInformation(
            "GitHub push to {Repo}@{Sha} {Disposition} indexer run {RunId}.",
            indexed.Name, sha, accepted.Duplicate ? "matched existing" : "queued", accepted.RunId);
        return new GitHubWebhookResult(
            GitHubWebhookOutcome.Queued,
            accepted.Duplicate
                ? $"A sync for {indexed.Name}@{sha} is already queued."
                : $"Queued incremental sync for {indexed.Name}@{sha}.",
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

    private async Task<RepositoryInfo?> FindRepositoryAsync(PushRepository repository)
    {
        var payloadUrls = new[] { repository.CloneUrl, repository.HtmlUrl }
            .Select(NormalizeRepoUrl)
            .Where(url => url is not null)
            .ToHashSet(StringComparer.Ordinal);

        var repositories = await graphStore.ListRepositoriesAsync();
        return repositories.FirstOrDefault(p =>
                   NormalizeRepoUrl(p.RepoUrl) is { } url && payloadUrls.Contains(url))
               ?? repositories.FirstOrDefault(p =>
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

    private sealed record EventEnvelope(
        [property: JsonPropertyName("repository")] PushRepository? Repository);

    private sealed record PushRepository(
        [property: JsonPropertyName("full_name")] string? FullName,
        [property: JsonPropertyName("default_branch")] string? DefaultBranch,
        [property: JsonPropertyName("clone_url")] string? CloneUrl,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);
}
