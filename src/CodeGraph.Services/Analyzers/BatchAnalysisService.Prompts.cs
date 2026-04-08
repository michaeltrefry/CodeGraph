using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeGraph.Data;

namespace CodeGraph.Services.Analyzers;

public partial class BatchAnalysisService
{
    private const int MaxCStyleFunctionsInPrompt = 24;
    private const int MaxCStyleStructsInPrompt = 12;

    private async Task<string> BuildProjectPromptAsync(
        string repoName,
        string projectName,
        IReadOnlyList<NodeEntity> projectNodes,
        IReadOnlyList<EdgeEntity> allRepoEdges,
        Dictionary<long, NodeEntity> nodeById,
        IAnalysisModelProvider provider,
        string? repoPath = null,
        bool includeAllSource = false)
    {
        var projectNodeIds = new HashSet<long>(projectNodes.Select(n => n.Id));

        // Edges where source OR target is in this project (includes cross-project edges within the repo)
        var relevantEdges = allRepoEdges
            .Where(e => projectNodeIds.Contains(e.SourceId) || projectNodeIds.Contains(e.TargetId))
            .ToList();

        // Single-pass edge grouping: build all three dictionaries simultaneously
        var outboundBySource = new Dictionary<long, List<EdgeEntity>>();
        var inboundByTarget = new Dictionary<long, List<EdgeEntity>>();
        var childrenByParent = new Dictionary<long, List<EdgeEntity>>();

        foreach (var e in relevantEdges)
        {
            if (e.Type is "DEFINES" or "DEFINES_METHOD")
            {
                if (!childrenByParent.TryGetValue(e.SourceId, out var children))
                {
                    children = new List<EdgeEntity>();
                    childrenByParent[e.SourceId] = children;
                }
                children.Add(e);
            }

            if (!StructuralEdgeTypes.Contains(e.Type))
            {
                if (!outboundBySource.TryGetValue(e.SourceId, out var outList))
                {
                    outList = new List<EdgeEntity>();
                    outboundBySource[e.SourceId] = outList;
                }
                outList.Add(e);

                if (!inboundByTarget.TryGetValue(e.TargetId, out var inList))
                {
                    inList = new List<EdgeEntity>();
                    inboundByTarget[e.TargetId] = inList;
                }
                inList.Add(e);
            }
        }

        var repo = await store.GetRepositoryByName(repoName);
        var promptStyle = DeterminePromptStyle(projectNodes, repo?.Language);
        var promptBudget = GetPromptBudget(provider, promptStyle);
        var describableNodes = SelectDescribableNodesForPrompt(projectNodes, outboundBySource, promptStyle, promptBudget);

        var sb = new StringBuilder();

        sb.AppendLine(promptStyle == PromptStyle.CStyle
            ? "You are analyzing a single firmware-oriented project from a repository's structural graph and selected source code."
            : "You are analyzing a single project from a repository's structural graph and selected source code.");
        sb.AppendLine(promptStyle == PromptStyle.CStyle
            ? "Based on the function signatures, struct definitions, relationships, file paths, and any included source code below, provide:"
            : "Based on the type signatures, relationships, structural metadata, and any included source code below, provide:");
        sb.AppendLine("1. A project-level summary (what this project does and its role in the repository)");
        sb.AppendLine(promptStyle == PromptStyle.CStyle
            ? "2. A description for every listed function/struct node"
            : "2. A description for every listed class/interface node");
        sb.AppendLine();
        sb.AppendLine($"Repository: {repoName}");
        sb.AppendLine($"Project: {projectName}");
        sb.AppendLine();
        sb.AppendLine(promptStyle == PromptStyle.CStyle
            ? "Graph (selected first-party functions/structs with signatures, file paths, and one-hop relationships):"
            : "Graph (each class/interface with typed members, signatures, and one-hop relationships):");
        sb.AppendLine();

        foreach (var node in describableNodes)
        {
            var nodeProps = ParseProperties(node.Properties);

            if (promptStyle == PromptStyle.CStyle)
                RenderCStyleNode(sb, node, nodeProps, childrenByParent, nodeById);
            else
                RenderDotNetNode(sb, node, nodeProps, childrenByParent, nodeById);

            // Outbound relationships (include cross-project targets by qualified name)
            if (outboundBySource.TryGetValue(node.Id, out var outEdges))
            {
                foreach (var group in outEdges.GroupBy(e => e.Type).OrderBy(g => g.Key))
                {
                    var targets = group.Select(e =>
                    {
                        if (!nodeById.TryGetValue(e.TargetId, out var tn)) return $"id:{e.TargetId}";
                        var crossProject = !projectNodeIds.Contains(e.TargetId);
                        return crossProject ? $"{tn.QualifiedName} [ext]" : tn.QualifiedName;
                    }).ToList();
                    targets = LimitRelationshipTargets(targets, promptBudget.MaxRelationshipTargetsPerType);
                    sb.AppendLine($"  {group.Key} → {string.Join(", ", targets)}");
                }
            }

            // Inbound relationships (callers/implementers/injectors)
            if (inboundByTarget.TryGetValue(node.Id, out var inEdges))
            {
                foreach (var group in inEdges.GroupBy(e => e.Type).OrderBy(g => g.Key))
                {
                    var sources = group.Select(e =>
                    {
                        if (!nodeById.TryGetValue(e.SourceId, out var sn)) return $"id:{e.SourceId}";
                        var crossProject = !projectNodeIds.Contains(e.SourceId);
                        return crossProject ? $"{sn.QualifiedName} [ext]" : sn.QualifiedName;
                    }).ToList();
                    sources = LimitRelationshipTargets(sources, promptBudget.MaxRelationshipTargetsPerType);
                    sb.AppendLine($"  ← {group.Key} from: {string.Join(", ", sources)}");
                }
            }

            sb.AppendLine();
        }

        // Get files flagged for secrets — these must never be sent for AI analysis
        var secretFiles = await exclusionService.GetSecretFilePathsAsync(repoName);

        // Append source code
        var sourceSection = await BuildSourceSectionAsync(
            describableNodes,
            outboundBySource,
            repoPath,
            includeAllSource,
            secretFiles,
            promptStyle,
            promptBudget.MaxSourceChars);
        if (sourceSection is not null)
            sb.Append(sourceSection);

        sb.AppendLine("""
            Respond with JSON only (no markdown fences):
            {
              "projectSummary": "2-3 sentence description of this project's purpose and role",
              "confidence": "high|medium|low",
              "nodes": [
                { "nodeId": 123, "description": "2-3 sentence description in business terms", "confidence": "high|medium|low" }
              ]
            }

            Include an entry in "nodes" for every node listed above.
            Use "low" confidence when relationships are sparse or purpose is unclear.
            Nodes marked [ext] are from other projects in the same repo — use them for context but do not describe them.
            For firmware/C-style projects, prioritize first-party application logic over vendored components, third-party libraries, examples, or generated tooling.
            """);

        return sb.ToString();
    }

    private static void RenderDotNetNode(StringBuilder sb, NodeEntity node, Dictionary<string, object> nodeProps,
        Dictionary<long, List<EdgeEntity>> childrenByParent, Dictionary<long, NodeEntity> nodeById)
    {
        var classModifiers = new List<string>();
        if (GetBool(nodeProps, "is_abstract")) classModifiers.Add("abstract");
        if (GetBool(nodeProps, "is_static")) classModifiers.Add("static");
        if (GetBool(nodeProps, "is_generic")) classModifiers.Add("generic");
        var modifierPrefix = classModifiers.Count > 0 ? $" ({string.Join(", ", classModifiers)})" : "";
        sb.AppendLine($"[{node.Label}] {node.QualifiedName}{modifierPrefix} (id:{node.Id})");

        if (nodeProps.TryGetValue("base_types", out var bt) && bt is string baseTypes && !string.IsNullOrWhiteSpace(baseTypes))
            sb.AppendLine($"  Base types: {baseTypes}");

        // Members (children via DEFINES / DEFINES_METHOD)
        if (childrenByParent.TryGetValue(node.Id, out var childEdges))
        {
            var children = childEdges
                .Select(e => nodeById.TryGetValue(e.TargetId, out var cn) ? cn : null)
                .Where(cn => cn is not null)
                .ToList();

            // Methods with signatures and return types
            var methodNodes = children.Where(c => c!.Label is "Method" or "Constructor").ToList();
            if (methodNodes.Count > 0)
            {
                sb.AppendLine("  Methods:");
                foreach (var m in methodNodes)
                {
                    var mProps = ParseProperties(m!.Properties);
                    var sig = GetString(mProps, "signature");
                    var retType = GetString(mProps, "return_type");
                    var isAsync = GetBool(mProps, "is_async");
                    var isEntry = GetBool(mProps, "is_entry_point");

                    if (!string.IsNullOrEmpty(sig))
                    {
                        var prefix = isAsync ? "async " : "";
                        var ret = !string.IsNullOrEmpty(retType) && retType != "unknown" ? $"{retType} " : "";
                        var entryMarker = isEntry ? " [endpoint]" : "";
                        sb.AppendLine($"    {prefix}{ret}{sig}{entryMarker}");
                    }
                    else
                    {
                        sb.AppendLine($"    {m.Name}");
                    }
                }
            }

            // Properties with types
            var propNodes = children.Where(c => c!.Label == "Property").ToList();
            if (propNodes.Count > 0)
            {
                sb.AppendLine("  Properties:");
                foreach (var p in propNodes)
                {
                    var pProps = ParseProperties(p!.Properties);
                    var propType = GetString(pProps, "type");
                    if (!string.IsNullOrEmpty(propType))
                        sb.AppendLine($"    {propType} {p.Name}");
                    else
                        sb.AppendLine($"    {p.Name}");
                }
            }
        }
    }

    private static void RenderCStyleNode(StringBuilder sb, NodeEntity node, Dictionary<string, object> nodeProps,
        Dictionary<long, List<EdgeEntity>> childrenByParent, Dictionary<long, NodeEntity> nodeById)
    {
        sb.AppendLine($"[{node.Label}] {node.QualifiedName} (id:{node.Id})");

        if (!string.IsNullOrWhiteSpace(node.FilePath))
            sb.AppendLine($"  File: {node.FilePath}");

        var returnType = GetString(nodeProps, "return_type");
        var parameters = GetString(nodeProps, "parameters");
        if (node.Label == "Function")
        {
            if (!string.IsNullOrWhiteSpace(returnType))
                sb.AppendLine($"  Returns: {returnType}");
            if (!string.IsNullOrWhiteSpace(parameters))
                sb.AppendLine($"  Parameters: {parameters}");
        }

        if (childrenByParent.TryGetValue(node.Id, out var childEdges))
        {
            var children = childEdges
                .Select(e => nodeById.TryGetValue(e.TargetId, out var cn) ? cn : null)
                .Where(cn => cn is not null)
                .ToList();

            var methodNodes = children.Where(c => c!.Label is "Method" or "Function" or "Constructor").ToList();
            if (methodNodes.Count > 0)
            {
                sb.AppendLine("  Members:");
                foreach (var m in methodNodes)
                    sb.AppendLine($"    {m!.Name}");
            }
        }
    }

    // --- Source code inclusion for analysis prompts ---

    private async Task<string?> BuildSourceSectionAsync(
        IReadOnlyList<NodeEntity> promptNodes,
        Dictionary<long, List<EdgeEntity>> outboundBySource,
        string? repoPath,
        bool includeAllSource,
        HashSet<string> secretFiles,
        PromptStyle promptStyle,
        int maxChars)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !fileSystem.DirectoryExists(repoPath))
            return null;

        // When includeAllSource is set, prioritize high-signal classes first then include
        // everything else. This way the most important code fills the budget first.
        List<NodeEntity> ordered;
        if (includeAllSource)
        {
            var highSignal = new HashSet<long>(
                promptNodes.Where(n => IsHighSignalNode(n, outboundBySource, promptStyle)).Select(n => n.Id));
            ordered = promptNodes
                .OrderByDescending(n => highSignal.Contains(n.Id))
                .ThenByDescending(n => outboundBySource.TryGetValue(n.Id, out var e) ? e.Count : 0)
                .ToList();
        }
        else
        {
            ordered = promptNodes
                .Where(n => IsHighSignalNode(n, outboundBySource, promptStyle))
                .ToList();

            if (ordered.Count == 0)
                return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine(promptStyle == PromptStyle.CStyle
            ? includeAllSource
                ? "Source code (selected first-party firmware functions/structs, prioritized by signal — budget capped):"
                : "Selected source code for key firmware functions/structs:"
            : includeAllSource
                ? "Source code (all classes, prioritized by signal — budget capped):"
                : "Selected source code for key classes (business logic, controllers, consumers):");
        sb.AppendLine();

        int totalChars = 0;
        int included = 0;

        foreach (var node in ordered)
        {
            if (totalChars >= maxChars) break;

            // Skip files flagged for exposed secrets
            if (!string.IsNullOrEmpty(node.FilePath) && secretFiles.Contains(node.FilePath))
                continue;

            var source = await ReadCompressedSourceAsync(repoPath, node.FilePath, node.StartLine, node.EndLine);
            if (source is null) continue;

            // Respect the budget — skip this class if it would exceed the limit
            if (totalChars + source.Length > maxChars)
                continue;

            sb.AppendLine($"### {node.QualifiedName}");
            sb.AppendLine(promptStyle == PromptStyle.CStyle ? "```c" : "```csharp");
            sb.AppendLine(source);
            sb.AppendLine("```");
            sb.AppendLine();

            totalChars += source.Length;
            included++;
        }

        if (included == 0)
        {
            // Log a sample of what we tried so it's diagnosable
            var sample = ordered.Take(3)
                .Select(n => $"{n.QualifiedName} (file={n.FilePath}, lines={n.StartLine}-{n.EndLine})")
                .ToList();
            logger.LogWarning("No source files could be read for {Count} candidate node(s). " +
                "Sample: {Sample}. RepoPath: {RepoPath}",
                ordered.Count, string.Join("; ", sample), repoPath);
            return null;
        }

        logger.LogInformation("Included source for {Count}/{Total} class(es) ({Chars} chars, budget {Budget})",
            included, ordered.Count, totalChars, maxChars);

        return sb.ToString();
    }

    private static bool IsHighSignalNode(NodeEntity node, Dictionary<long, List<EdgeEntity>> outboundBySource, PromptStyle promptStyle)
    {
        if (promptStyle == PromptStyle.CStyle)
            return IsHighSignalCNode(node, outboundBySource);

        var project = GetDotnetProject(node);

        // Services projects contain business logic
        if (project.Contains(".Services", StringComparison.OrdinalIgnoreCase))
            return true;

        // Controllers are API entry points
        if (node.QualifiedName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
            return true;

        // MassTransit consumers show event handling
        if (node.QualifiedName.EndsWith("Consumer", StringComparison.OrdinalIgnoreCase))
            return true;

        // High outbound edge count = orchestrator worth reading
        if (outboundBySource.TryGetValue(node.Id, out var edges) && edges.Count >= 5)
            return true;

        return false;
    }

    private static bool IsHighSignalCNode(NodeEntity node, Dictionary<long, List<EdgeEntity>> outboundBySource)
    {
        if (IsOwnedSourcePath(node.FilePath))
            return true;

        if (outboundBySource.TryGetValue(node.Id, out var edges) && edges.Count >= 3)
            return true;

        return false;
    }

    private async Task<string?> ReadCompressedSourceAsync(string repoPath, string filePath, int startLine, int endLine)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            logger.LogDebug("Skipping node — empty FilePath");
            return null;
        }

        if (startLine <= 0 || endLine <= 0)
        {
            logger.LogDebug("Skipping {FilePath} — StartLine={Start}, EndLine={End}",
                filePath, startLine, endLine);
            return null;
        }

        try
        {
            // If filePath is absolute, use it directly; otherwise combine with repoPath
            var fullPath = Path.IsPathRooted(filePath)
                ? filePath
                : Path.Combine(repoPath, filePath);

            if (!fileSystem.FileExists(fullPath))
            {
                logger.LogDebug("File not found: {FullPath} (filePath={FilePath}, repoPath={RepoPath})",
                    fullPath, filePath, repoPath);
                return null;
            }

            var allLines = await fileSystem.ReadAllLinesAsync(fullPath);
            if (startLine > allLines.Length)
                return null;

            var end = Math.Min(endLine, allLines.Length);
            var span = allLines[(startLine - 1)..end];

            var compressed = CompressMethodBodies(span);
            return compressed.Count > 0 ? string.Join('\n', compressed) : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read source from {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Compress source lines: keep signatures and structure, collapse method bodies.
    /// </summary>
    internal static List<string> CompressMethodBodies(string[] lines)
    {
        var compressed = new List<string>();
        int braceDepth = 0;
        bool inMethodBody = false;
        int methodBodyStart = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (string.IsNullOrWhiteSpace(line)) continue;
            if (trimmed.StartsWith("using ")) continue;
            if (trimmed.StartsWith("///")) continue;

            int openBraces = trimmed.Count(c => c == '{');
            int closeBraces = trimmed.Count(c => c == '}');

            if (!inMethodBody)
            {
                compressed.Add(line);

                braceDepth += openBraces - closeBraces;
                if (braceDepth >= 2 && openBraces > 0)
                {
                    inMethodBody = true;
                    methodBodyStart = i;
                }
            }
            else
            {
                braceDepth += openBraces - closeBraces;
                if (braceDepth < 2)
                {
                    int bodyLines = i - methodBodyStart - 1;
                    if (bodyLines > 1)
                        compressed.Add($"{GetIndent(line)}    // ... {bodyLines} lines");
                    compressed.Add(line);
                    inMethodBody = false;
                }
            }
        }

        return compressed;
    }

    // --- Property helpers ---

    private static string GetDotnetProject(NodeEntity node)
        => node.DotnetProject ?? node.Project;

    private static string GetIndent(string line)
    {
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        return line[..i];
    }

    private static Dictionary<string, object> ParseProperties(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json, CamelOpts)
                ?? new();
        }
        catch { return new(); }
    }

    private static string GetString(Dictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var val)) return "";
        return val switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString() ?? "",
            JsonElement je => je.ToString(),
            _ => val.ToString() ?? ""
        };
    }

    private static bool GetBool(Dictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var val)) return false;
        return val switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => false
        };
    }

    private static PromptStyle DeterminePromptStyle(IReadOnlyList<NodeEntity> projectNodes, string? repoLanguage)
    {
        if (string.Equals(repoLanguage, "C", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(repoLanguage, "C++", StringComparison.OrdinalIgnoreCase))
            return PromptStyle.CStyle;

        var classLikeCount = projectNodes.Count(n => n.Label is "Class" or "Interface" or "Record");
        var cStyleCount = projectNodes.Count(n => n.Label is "Struct" or "Function");
        return cStyleCount >= Math.Max(12, classLikeCount * 3)
            ? PromptStyle.CStyle
            : PromptStyle.ObjectOriented;
    }

    private static List<NodeEntity> SelectDescribableNodesForPrompt(
        IReadOnlyList<NodeEntity> projectNodes,
        Dictionary<long, List<EdgeEntity>> outboundBySource,
        PromptStyle promptStyle,
        PromptBudget promptBudget)
    {
        if (promptStyle == PromptStyle.CStyle)
        {
            var candidates = projectNodes
                .Where(n => n.Label is "Function" or "Struct")
                .ToList();

            var ownedSource = candidates
                .Where(n => IsOwnedSourcePath(n.FilePath) && !IsNonProductionPath(n.FilePath))
                .ToList();
            if (ownedSource.Count > 0)
            {
                candidates = ownedSource;
            }
            else
            {
                var nonVendored = candidates
                    .Where(n => !IsVendoredPath(n.FilePath))
                    .ToList();
                if (nonVendored.Count > 0)
                    candidates = nonVendored;
            }

            var orderedStructs = candidates
                .Where(n => n.Label == "Struct")
                .OrderByDescending(n => GetCNodePriority(n, outboundBySource))
                .ThenBy(n => n.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.QualifiedName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxCStyleStructsInPrompt);

            var orderedFunctions = candidates
                .Where(n => n.Label == "Function")
                .OrderByDescending(n => GetCNodePriority(n, outboundBySource))
                .ThenBy(n => n.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.QualifiedName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxCStyleFunctionsInPrompt);

            return orderedStructs
                .Concat(orderedFunctions)
                .OrderBy(n => n.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.QualifiedName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return projectNodes
            .Where(n => n.Label is "Class" or "Interface")
            .OrderByDescending(n => IsHighSignalNode(n, outboundBySource, promptStyle))
            .ThenByDescending(n => outboundBySource.TryGetValue(n.Id, out var edges) ? edges.Count : 0)
            .ThenBy(n => n.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .Take(promptBudget.MaxPromptNodes ?? int.MaxValue)
            .ToList();
    }

    private PromptBudget GetPromptBudget(IAnalysisModelProvider provider, PromptStyle promptStyle)
    {
        if (!string.Equals(provider.ProviderName, "local", StringComparison.OrdinalIgnoreCase))
        {
            return new PromptBudget(options.MaxSourceChars, MaxPromptNodes: null, MaxRelationshipTargetsPerType: null);
        }

        var localMaxNodes = Math.Max(1, options.Local.MaxPromptNodes);
        if (promptStyle == PromptStyle.CStyle)
            localMaxNodes = Math.Max(localMaxNodes, MaxCStyleFunctionsInPrompt + MaxCStyleStructsInPrompt);

        return new PromptBudget(
            MaxSourceChars: Math.Max(1, options.Local.MaxSourceChars),
            MaxPromptNodes: localMaxNodes,
            MaxRelationshipTargetsPerType: Math.Max(1, options.Local.MaxRelationshipTargetsPerType));
    }

    private static List<string> LimitRelationshipTargets(List<string> items, int? maxItems)
    {
        if (maxItems is null || items.Count <= maxItems.Value)
            return items;

        var limited = items.Take(maxItems.Value).ToList();
        limited.Add($"... (+{items.Count - maxItems.Value} more)");
        return limited;
    }

    private static int GetCNodePriority(NodeEntity node, Dictionary<long, List<EdgeEntity>> outboundBySource)
    {
        var score = 0;

        if (IsOwnedSourcePath(node.FilePath))
            score += 100;
        else if (!IsVendoredPath(node.FilePath))
            score += 40;

        if (node.FilePath.Contains("/main/", StringComparison.OrdinalIgnoreCase))
            score += 40;
        if (node.FilePath.Contains("/shared/", StringComparison.OrdinalIgnoreCase))
            score += 20;
        if (node.Label == "Function")
            score += 15;
        if (outboundBySource.TryGetValue(node.Id, out var edges))
            score += Math.Min(edges.Count, 15);
        if (IsNonProductionPath(node.FilePath))
            score -= 40;

        return score;
    }

    private static bool IsOwnedSourcePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        if (IsVendoredPath(filePath) || IsNonProductionPath(filePath))
            return false;

        return filePath.Contains("/main/", StringComparison.OrdinalIgnoreCase) ||
               filePath.Contains("/shared/", StringComparison.OrdinalIgnoreCase) ||
               IsOwnedComponentPath(filePath);
    }

    private static bool IsVendoredPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        if (filePath.Contains("/managed_components/", StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains("/packages/", StringComparison.OrdinalIgnoreCase))
            return true;

        var componentName = GetComponentName(filePath);
        return componentName?.Contains("__", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsNonProductionPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        return filePath.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
               filePath.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
               filePath.Contains("/sim/", StringComparison.OrdinalIgnoreCase) ||
               filePath.Contains("/examples/", StringComparison.OrdinalIgnoreCase) ||
               filePath.Contains("/build/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOwnedComponentPath(string filePath)
    {
        var componentName = GetComponentName(filePath);
        return !string.IsNullOrWhiteSpace(componentName) &&
               !componentName.Contains("__", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetComponentName(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "components", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1];
        }

        return null;
    }

    private enum PromptStyle
    {
        ObjectOriented,
        CStyle
    }

    private sealed record PromptBudget(
        int MaxSourceChars,
        int? MaxPromptNodes,
        int? MaxRelationshipTargetsPerType);
}
