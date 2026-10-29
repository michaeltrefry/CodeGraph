using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Overlays;
using ModelContextProtocol.Protocol;

namespace CodeGraph.Services.Assistant;

/// <summary>
/// Builds MCP answers that name the commit they describe in two places: the first text line
/// (<c>Commit: &lt;sha&gt;</c>) and <c>structuredContent.commitSha</c>.
/// </summary>
public static class CommitAnswer
{
    /// <param name="extra">Additional structuredContent fields (for example the low-confidence flags).</param>
    public static CallToolResult Create(string text, CommitDescription commit, CommitScope scope, JsonObject? extra = null)
    {
        var structured = new JsonObject
        {
            ["commitSha"] = commit.CommitSha,
            ["source"] = scope.Overlay is not null ? "overlay" : "default-branch"
        };
        // Several repositories: name each one's commit (for a pinned answer, the other repositories
        // are answered from their default-branch index).
        if (commit.Commits.Count > 1)
        {
            var commits = new JsonObject();
            foreach (var (repository, sha) in commit.Commits.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                commits[repository] = sha;
            structured["commits"] = commits;
        }

        if (scope.IsPinned)
            structured["repo"] = scope.Repository;
        if (scope.Overlay is { } overlay)
        {
            structured["overlayId"] = overlay.Overlay.Id;
            structured["baseSha"] = overlay.Overlay.BaseSha;
        }

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
                structured[key] = value?.DeepClone();
        }

        return Result($"{commit.HeaderLine}\n\n{text}", structured, isError: false);
    }

    /// <summary>A commit-pinned request that cannot be answered for its SHA; it carries no default-branch data.</summary>
    /// <param name="extra">Additional structuredContent fields that do not depend on the commit.</param>
    public static CallToolResult NotIndexed(CommitNotIndexedResponse notIndexed, JsonObject? extra = null)
    {
        var isSha = notIndexed.Code != CommitScopeResolver.InvalidShaCode;
        var text = $"Commit: {(isSha ? notIndexed.Sha : "unknown")}\n\n" +
                   $"Not indexed ({notIndexed.Code}, status: {notIndexed.Status}): {notIndexed.Message}";
        var structured = new JsonObject
        {
            ["commitSha"] = isSha ? notIndexed.Sha : null,
            ["notIndexed"] = JsonSerializer.SerializeToNode(notIndexed, JsonOptions)
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
                structured[key] = value?.DeepClone();
        }

        return Result(text, structured, isError: notIndexed.Code != CommitScopeResolver.NotIndexedCode);
    }

    /// <summary>A typed tool error: <c>structuredContent.error = {code, message}</c>, flagged as an error.</summary>
    public static CallToolResult Error(string code, string message)
    {
        var structured = new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };
        return Result($"Error ({code}): {message}", structured, isError: true);
    }

    /// <summary>The first text block of a tool result.</summary>
    public static string TextOf(CallToolResult result)
        => result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;

    public static CallToolResult Text(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }]
    };

    private static CallToolResult Result(string text, JsonObject structured, bool isError) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        StructuredContent = JsonSerializer.SerializeToElement(structured),
        IsError = isError ? true : null
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
