using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeGraph.Services.Assistant;

/// <summary>
/// Refuses a call that passes <c>project</c> to a tool that has no <c>project</c> argument
/// (sc-27867). "project" used to name a repository on every legacy tool; it now means only a
/// multi-repo Project, so a stale client must fail loudly rather than have an unknown argument
/// dropped by the SDK's binding and run the call unscoped (the repository argument is now
/// <c>repository</c>). Work tools declare their own <c>project</c> argument and are untouched.
/// </summary>
public static class LegacyProjectArgumentGuard
{
    public const string Code = "invalid_argument";

    public const string Message = "`project` now means a multi-repo project; pass `repository`";

    /// <summary>Tools whose names said "project" for a repository, and what they were renamed to.</summary>
    public static readonly IReadOnlyDictionary<string, string> RenamedTools = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["list_projects"] = "list_repositories",
        ["search_projects"] = "search_repositories",
        ["project_report"] = "repository_report",
        ["get_project_health"] = "get_repository_health"
    };

    /// <summary>
    /// The refusal for a call to a tool that was renamed away from "project", or null for any other
    /// name. It names the replacement and grants nothing: the caller still needs the new tool's
    /// entitlement to call it.
    /// </summary>
    public static JsonObject? RenamedToolError(string? toolName) =>
        toolName is not null && RenamedTools.TryGetValue(toolName, out var replacement)
            ? new JsonObject
            {
                ["code"] = Code,
                ["message"] = $"`{toolName}` was renamed to `{replacement}`; `project` now means a multi-repo project"
            }
            : null;

    /// <summary>The tools/call result body for <paramref name="error"/>, in the shape the SDK returns.</summary>
    public static JsonObject ToResultBody(JsonObject error) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = error.ToJsonString() }),
        ["structuredContent"] = error.DeepClone(),
        ["isError"] = true
    };

    /// <summary>The refusal for a call to <paramref name="tool"/>, or null when the call is fine.</summary>
    public static CallToolResult? Check(Tool? tool, IEnumerable<string>? argumentNames)
    {
        if (tool is null || argumentNames is null || !argumentNames.Any(name => name == "project"))
            return null;

        var properties = PropertyNames(tool);
        if (properties.Contains("project"))
            return null;

        var error = new JsonObject
        {
            ["code"] = Code,
            ["message"] = $"{Message}. Valid arguments of {tool.Name}: {string.Join(", ", properties.Order(StringComparer.Ordinal))}."
        };
        return ToCallToolResult(error);
    }

    private static CallToolResult ToCallToolResult(JsonObject error) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = error.ToJsonString() }],
        StructuredContent = JsonSerializer.SerializeToElement(error)
    };

    /// <summary>The request filter that applies <see cref="Check"/> to every tool call.</summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Filter => next => async (request, cancellationToken) =>
    {
        if (RenamedToolError(request.Params?.Name) is { } renamed)
            return ToCallToolResult(renamed);

        var refusal = Check((request.MatchedPrimitive as McpServerTool)?.ProtocolTool, request.Params?.Arguments?.Keys);
        return refusal ?? await next(request, cancellationToken);
    };

    private static HashSet<string> PropertyNames(Tool tool)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (tool.InputSchema.ValueKind == JsonValueKind.Object
            && tool.InputSchema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
                names.Add(property.Name);
        }

        return names;
    }
}
