using System.Text.Json;
using Anthropic.Models.Messages;
using TC.CodeGraphApi.Data;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Services.Models;

namespace TC.CodeGraphApi.Services.Analyzers;

public partial class ClaudeCodeAnalyzer
{
    private static string GetTextContent(Message response)
    {
        // Anthropic SDK uses discriminated unions with TryPick* methods
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var textBlock))
                return textBlock.Text;
        }
        return "";
    }

    private static ProjectAnalysis ParseProjectAnalysis(string json)
    {
        // Strip markdown code fences if present
        json = StripCodeFences(json);

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new ProjectAnalysis(
            ProjectName: root.GetProperty("projectName").GetString() ?? "",
            Summary: root.GetProperty("summary").GetString() ?? "",
            Confidence: ParseConfidence(root.GetProperty("confidence").GetString()),
            Endpoints: ParseEndpoints(root),
            Services: ParseServices(root),
            ExternalDependencies: ParseStringArray(root, "externalDependencies"),
            DatabaseTables: ParseStringArray(root, "databaseTables")
        );
    }

    private static (string Summary, ConfidenceLevel Confidence) ParseRepoSummary(string json)
    {
        json = StripCodeFences(json);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return (
            root.GetProperty("summary").GetString() ?? "",
            ParseConfidence(root.GetProperty("confidence").GetString())
        );
    }

    private static AnalysisUpdate? ParseChangeAnalysis(string json)
    {
        json = StripCodeFences(json);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.GetProperty("needsUpdate").GetBoolean())
            return null;

        return new AnalysisUpdate(
            UpdatedSummary: root.GetProperty("updatedSummary").GetString() ?? "",
            Confidence: ParseConfidence(root.GetProperty("confidence").GetString()),
            ChangeDescription: root.GetProperty("changeDescription").GetString() ?? ""
        );
    }

    private static ConfidenceLevel ParseConfidence(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "high" => ConfidenceLevel.High,
            "low" => ConfidenceLevel.Low,
            _ => ConfidenceLevel.Medium
        };
    }

    private static IReadOnlyList<StoredEndpoint> ParseEndpoints(JsonElement root)
    {
        if (!root.TryGetProperty("endpoints", out var arr))
            return [];

        return arr.EnumerateArray().Select(e => new StoredEndpoint(
            Route: e.GetProperty("route").GetString() ?? "",
            HttpMethod: e.GetProperty("httpMethod").GetString() ?? "",
            Description: e.GetProperty("description").GetString() ?? "",
            RequestModel: e.TryGetProperty("requestModel", out var rm) ? rm.GetString() : null,
            ResponseModel: e.TryGetProperty("responseModel", out var rsp) ? rsp.GetString() : null
        )).ToList();
    }

    private static IReadOnlyList<StoredService> ParseServices(JsonElement root)
    {
        if (!root.TryGetProperty("services", out var arr))
            return [];

        return arr.EnumerateArray().Select(s => new StoredService(
            Name: s.GetProperty("name").GetString() ?? "",
            Description: s.GetProperty("description").GetString() ?? "",
            InterfaceName: s.TryGetProperty("interfaceName", out var iface) ? iface.GetString() : null,
            Lifetime: s.TryGetProperty("lifetime", out var lt) ? lt.GetString() ?? "scoped" : "scoped"
        )).ToList();
    }

    private static List<string> ParseStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arr))
            return [];

        return arr.EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static string StripCodeFences(string text)
    {
        // Walk the string to find the balanced closing brace/bracket for the first
        // JSON value. Using LastIndexOf('}') is unreliable when Claude adds trailing
        // commentary or examples that also contain braces.
        var first = text.IndexOfAny(['{', '[']);
        if (first < 0) return text.Trim();

        var open = text[first];
        var close = open == '{' ? '}' : ']';
        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = first; i < text.Length; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == open) depth++;
            else if (c == close && --depth == 0) return text[first..(i + 1)];
        }

        return text.Trim();
    }
}
