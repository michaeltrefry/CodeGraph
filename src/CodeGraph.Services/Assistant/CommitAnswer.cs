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
    public static CallToolResult Create(string text, CommitDescription commit, CommitScope scope)
    {
        var structured = new JsonObject
        {
            ["commitSha"] = commit.CommitSha,
            ["source"] = scope.Overlay is not null ? "overlay" : "default-branch"
        };
        if (commit.CommitSha is null && commit.Commits.Count > 1)
        {
            var commits = new JsonObject();
            foreach (var (project, sha) in commit.Commits.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                commits[project] = sha;
            structured["commits"] = commits;
        }

        if (scope.IsPinned)
            structured["repo"] = scope.Project;
        if (scope.Overlay is { } overlay)
        {
            structured["overlayId"] = overlay.Overlay.Id;
            structured["baseSha"] = overlay.Overlay.BaseSha;
        }

        return Result($"{commit.HeaderLine}\n\n{text}", structured, isError: false);
    }

    /// <summary>A commit-pinned request that cannot be answered for its SHA; it carries no default-branch data.</summary>
    public static CallToolResult NotIndexed(CommitNotIndexedResponse notIndexed)
    {
        var isSha = notIndexed.Code != CommitScopeResolver.InvalidShaCode;
        var text = $"Commit: {(isSha ? notIndexed.Sha : "unknown")}\n\n" +
                   $"Not indexed ({notIndexed.Code}, status: {notIndexed.Status}): {notIndexed.Message}";
        var structured = new JsonObject
        {
            ["commitSha"] = isSha ? notIndexed.Sha : null,
            ["notIndexed"] = JsonSerializer.SerializeToNode(notIndexed, JsonOptions)
        };
        return Result(text, structured, isError: notIndexed.Code != CommitScopeResolver.NotIndexedCode);
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
