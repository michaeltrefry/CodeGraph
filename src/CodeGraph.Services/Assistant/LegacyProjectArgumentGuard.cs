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
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = error.ToJsonString() }],
            StructuredContent = JsonSerializer.SerializeToElement(error)
        };
    }

    /// <summary>The request filter that applies <see cref="Check"/> to every tool call.</summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Filter => next => async (request, cancellationToken) =>
    {
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
