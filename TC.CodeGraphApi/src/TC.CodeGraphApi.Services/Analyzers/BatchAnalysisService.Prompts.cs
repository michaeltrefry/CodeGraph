using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Data;

namespace TC.CodeGraphApi.Services.Analyzers;

public partial class BatchAnalysisService
{
    private async Task<string> BuildProjectPromptAsync(
        string repoName,
        string projectName,
        IReadOnlyList<NodeEntity> projectNodes,
        IReadOnlyList<EdgeEntity> allRepoEdges,
        Dictionary<long, NodeEntity> nodeById,
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
            if (e.Type == "DEFINES")
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

        // Detect if this is an IaC project (Ansible or Terraform)
        var isIacProject = projectNodes.Any(n =>
            n.Label is "Playbook" or "Role" or "AnsibleTask" or "AnsibleHandler" or "AnsibleVariable"
                or "TerraformResource" or "TerraformModule" or "TerraformVariable" or "TerraformOutput" or "TerraformDataSource");

        var describableNodes = isIacProject
            ? projectNodes
                .Where(n => n.Label is "Playbook" or "Role" or "AnsibleTask" or "AnsibleHandler" or "AnsibleVariable"
                    or "TerraformResource" or "TerraformModule" or "TerraformVariable" or "TerraformOutput" or "TerraformDataSource")
                .OrderBy(n => n.QualifiedName)
                .ToList()
            : projectNodes
                .Where(n => n.Label is "Class" or "Interface")
                .OrderBy(n => n.QualifiedName)
                .ToList();

        // Keep original name for source code section (only relevant for .NET)
        var classAndInterfaceNodes = isIacProject ? new List<NodeEntity>() : describableNodes;

        var sb = new StringBuilder();

        if (isIacProject)
        {
            sb.AppendLine("You are analyzing an infrastructure-as-code project from a repository's structural graph.");
            sb.AppendLine("This repository may contain Ansible (playbooks, roles, tasks) and/or Terraform (resources, modules, variables).");
            sb.AppendLine("Based on the structural metadata and relationships below, provide:");
            sb.AppendLine("1. A project-level summary (what infrastructure this project manages, which services it deploys/configures)");
            sb.AppendLine("2. A description for every significant node (playbooks, roles, resources, modules)");
            sb.AppendLine();
            sb.AppendLine("Pay special attention to:");
            sb.AppendLine("- DEPLOYS edges: which application services does this IaC deploy?");
            sb.AppendLine("- CONFIGURES edges: what configuration/infrastructure does this manage?");
            sb.AppendLine("- INCLUDES_ROLE / INCLUDES_MODULE edges: how are components composed?");
            sb.AppendLine("- DEPENDS_ON edges: what are the dependency relationships between resources?");
            sb.AppendLine("- Cross-repo references: hostnames, URLs, service names that map to application repos");
        }
        else
        {
            sb.AppendLine("You are analyzing a single .NET project from a repository's structural graph and selected source code.");
            sb.AppendLine("Based on the type signatures, relationships, structural metadata, and any included source code below, provide:");
            sb.AppendLine("1. A project-level summary (what this project does and its role in the repository)");
            sb.AppendLine("2. A description for every class/interface node");
        }
        sb.AppendLine();
        sb.AppendLine($"Repository: {repoName}");
        sb.AppendLine($"Project: {projectName}");
        sb.AppendLine();
        sb.AppendLine(isIacProject
            ? "Graph (playbooks, roles, tasks, handlers, variables with relationships):"
            : "Graph (each class/interface with typed members, signatures, and one-hop relationships):");
        sb.AppendLine();

        foreach (var node in describableNodes)
        {
            var nodeProps = ParseProperties(node.Properties);

            if (isIacProject)
            {
                RenderIacNode(sb, node, nodeProps);
            }
            else
            {
                RenderDotNetNode(sb, node, nodeProps, childrenByParent, nodeById);
            }

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
                    sb.AppendLine($"  ← {group.Key} from: {string.Join(", ", sources)}");
                }
            }

            sb.AppendLine();
        }

        // Get files flagged for secrets — these must never be sent for AI analysis
        var secretFiles = await exclusionService.GetSecretFilePathsAsync(repoName);

        // Append source code
        if (isIacProject)
        {
            var iacSource = await BuildIacSourceSectionAsync(repoPath, secretFiles);
            if (iacSource is not null)
                sb.Append(iacSource);
        }
        else
        {
            var sourceSection = await BuildSourceSectionAsync(classAndInterfaceNodes, outboundBySource, repoPath, includeAllSource, secretFiles);
            if (sourceSection is not null)
                sb.Append(sourceSection);
        }

        if (isIacProject)
        {
            sb.AppendLine("""
                Respond with JSON only (no markdown fences):
                {
                  "projectSummary": "2-3 sentence description of what infrastructure this manages and which services it deploys/configures",
                  "confidence": "high|medium|low",
                  "nodes": [
                    { "nodeId": 123, "description": "2-3 sentence description of what this playbook/role/task does in business terms", "confidence": "high|medium|low" }
                  ]
                }

                Include an entry in "nodes" for every playbook, role, resource, and module listed above. Include significant tasks if they reveal important infrastructure operations.
                Focus descriptions on: what service/infrastructure is being managed, why it exists, and how it connects to application services.
                Use "low" confidence when the purpose is unclear from the YAML structure alone.
                """);
        }
        else
        {
            sb.AppendLine("""
                Respond with JSON only (no markdown fences):
                {
                  "projectSummary": "2-3 sentence description of this project's purpose and role",
                  "confidence": "high|medium|low",
                  "nodes": [
                    { "nodeId": 123, "description": "2-3 sentence description in business terms", "confidence": "high|medium|low" }
                  ]
                }

                Include an entry in "nodes" for every class/interface listed above.
                Use "low" confidence when relationships are sparse or purpose is unclear.
                Nodes marked [ext] are from other projects in the same repo — use them for context but do not describe them.
                """);
        }

        return sb.ToString();
    }

    private static void RenderIacNode(StringBuilder sb, NodeEntity node, Dictionary<string, object> nodeProps)
    {
        sb.AppendLine($"[{node.Label}] {node.QualifiedName} (id:{node.Id})");

        // Ansible properties
        if (nodeProps.TryGetValue("module", out var mod) && mod is string module && !string.IsNullOrEmpty(module))
            sb.AppendLine($"  Module: {module}");

        // Terraform properties
        if (nodeProps.TryGetValue("resource_type", out var rt) && rt is string resType)
            sb.AppendLine($"  Resource type: {resType}");
        if (nodeProps.TryGetValue("provider", out var prov) && prov is string provider)
            sb.AppendLine($"  Provider: {provider}");
        if (nodeProps.TryGetValue("source", out var src) && src is string source)
            sb.AppendLine($"  Source: {source}");
        if (nodeProps.TryGetValue("display_name", out var dn) && dn is string displayName)
            sb.AppendLine($"  Name: {displayName}");
        if (nodeProps.TryGetValue("image", out var img) && img is string image)
            sb.AppendLine($"  Image: {image}");
        if (nodeProps.TryGetValue("engine", out var eng) && eng is string engine)
            sb.AppendLine($"  Engine: {engine}");
        if (nodeProps.TryGetValue("runtime", out var runtime) && runtime is string rt2)
            sb.AppendLine($"  Runtime: {rt2}");
        if (nodeProps.TryGetValue("value_expression", out var valExpr) && valExpr is string expr)
            sb.AppendLine($"  Value: {expr}");

        // Common IaC properties
        if (nodeProps.TryGetValue("scope", out var scope) && scope is string scopeStr)
            sb.AppendLine($"  Scope: {scopeStr}");
        if (nodeProps.TryGetValue("value_preview", out var preview) && preview is string previewStr)
            sb.AppendLine($"  Value: {previewStr}");
        if (nodeProps.TryGetValue("default_value", out var defVal) && defVal is string defaultVal)
            sb.AppendLine($"  Default: {defaultVal}");
        if (nodeProps.TryGetValue("type", out var varType) && varType is string typeStr)
            sb.AppendLine($"  Type: {typeStr}");
        if (nodeProps.TryGetValue("likely_service_ref", out _))
            sb.AppendLine("  [likely service reference]");
        if (nodeProps.TryGetValue("description", out var desc) && desc is string descStr)
            sb.AppendLine($"  Description: {descStr}");
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

        // Members (children via DEFINES)
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

    // --- Source code inclusion for analysis prompts ---

    private async Task<string?> BuildSourceSectionAsync(
        IReadOnlyList<NodeEntity> classNodes,
        Dictionary<long, List<EdgeEntity>> outboundBySource,
        string? repoPath,
        bool includeAllSource,
        HashSet<string> secretFiles)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !fileSystem.DirectoryExists(repoPath))
            return null;

        var maxChars = options.MaxSourceChars;

        // When includeAllSource is set, prioritize high-signal classes first then include
        // everything else. This way the most important code fills the budget first.
        List<NodeEntity> ordered;
        if (includeAllSource)
        {
            var highSignal = new HashSet<long>(
                classNodes.Where(n => IsHighSignalClass(n, outboundBySource)).Select(n => n.Id));
            ordered = classNodes
                .OrderByDescending(n => highSignal.Contains(n.Id))
                .ThenByDescending(n => outboundBySource.TryGetValue(n.Id, out var e) ? e.Count : 0)
                .ToList();
        }
        else
        {
            ordered = classNodes
                .Where(n => IsHighSignalClass(n, outboundBySource))
                .ToList();

            if (ordered.Count == 0)
                return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine(includeAllSource
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
            sb.AppendLine("```csharp");
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
            logger.LogWarning("No source files could be read for {Count} candidate class(es). " +
                "Sample: {Sample}. RepoPath: {RepoPath}",
                ordered.Count, string.Join("; ", sample), repoPath);
            return null;
        }

        logger.LogInformation("Included source for {Count}/{Total} class(es) ({Chars} chars, budget {Budget})",
            included, ordered.Count, totalChars, maxChars);

        return sb.ToString();
    }

    /// <summary>
    /// For IaC repos (Ansible/Terraform), include all YAML source files directly since the
    /// graph alone doesn't capture the full picture (Jinja2 templates, conditionals, etc.).
    /// </summary>
    private async Task<string?> BuildIacSourceSectionAsync(string? repoPath, HashSet<string> secretFiles)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !fileSystem.DirectoryExists(repoPath))
            return null;

        var maxChars = options.MaxSourceChars;
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".yml", ".yaml", ".j2", ".cfg", ".conf", ".ini", ".tf", ".tfvars", ".hcl" };

        var files = fileSystem.EnumerateFiles(repoPath, "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f)))
            .Where(f => !f.Contains(".git", StringComparison.OrdinalIgnoreCase))
            .Where(f => !secretFiles.Contains(Path.GetRelativePath(repoPath, f).Replace('\\', '/')))
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("Source files (IaC — Ansible YAML, Terraform HCL, templates):");
        sb.AppendLine();

        int totalChars = 0;
        int included = 0;

        foreach (var file in files)
        {
            if (totalChars >= maxChars) break;

            try
            {
                var content = await fileSystem.ReadAllTextAsync(file);
                if (string.IsNullOrWhiteSpace(content)) continue;
                if (totalChars + content.Length > maxChars && included > 0) continue;

                var relativePath = Path.GetRelativePath(repoPath, file).Replace('\\', '/');
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var lang = ext is ".tf" or ".tfvars" or ".hcl" ? "hcl" : "yaml";
                sb.AppendLine($"### {relativePath}");
                sb.AppendLine($"```{lang}");
                sb.AppendLine(content);
                sb.AppendLine("```");
                sb.AppendLine();

                totalChars += content.Length;
                included++;
            }
            catch
            {
                // Skip unreadable files
            }
        }

        if (included == 0)
            return null;

        logger.LogInformation("Included {Count} IaC source file(s) ({Chars} chars, budget {Budget})",
            included, totalChars, maxChars);

        return sb.ToString();
    }

    private static bool IsHighSignalClass(NodeEntity node, Dictionary<long, List<EdgeEntity>> outboundBySource)
    {
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
}
