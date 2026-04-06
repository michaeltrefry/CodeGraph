using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Services;

namespace TC.CodeGraphApi.Extractors.Terraform;

/// <summary>
/// Regex-based best-effort parser for Terraform .tf and .tfvars files.
/// Extracts resources, data sources, modules, variables, outputs, and their relationships.
/// </summary>
public partial class HclParser
{
    private readonly ExtractorContext _context;
    private readonly string _filePath;
    private readonly ILogger _logger;

    private readonly List<GraphNode> _nodes = [];
    private readonly List<PendingEdge> _edges = [];
    private readonly List<UnresolvedCall> _unresolvedCalls = [];
    private readonly HashSet<string> _discoveredReferences = [];

    public HclParser(ExtractorContext context, string filePath, ILogger logger)
    {
        _context = context;
        _filePath = filePath;
        _logger = logger;
    }

    private static readonly ExtractionResult EmptyResult = new();

    public ExtractionResult Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return EmptyResult;

        var isTfvars = _filePath.EndsWith(".tfvars", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (isTfvars)
            {
                ParseTfvars(content);
            }
            else
            {
                ParseResources(content);
                ParseDataSources(content);
                ParseModules(content);
                ParseVariables(content);
                ParseOutputs(content);
                ParseLocals(content);
                ExtractResourceReferences(content);
            }

            // Static reference analysis: scan for service refs in all string values
            ScanForReferences(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Terraform HCL {File}", _filePath);
        }

        if (_nodes.Count == 0 && _edges.Count == 0 && _discoveredReferences.Count == 0)
            return EmptyResult;

        return new ExtractionResult
        {
            Nodes = _nodes,
            Edges = _edges,
            UnresolvedCalls = _unresolvedCalls,
            Metadata = new ProjectMetadata("HCL", "Terraform")
        };
    }

    // ── Resources ───────────────────────────────────────────────────

    private void ParseResources(string content)
    {
        foreach (Match m in ResourceBlockRegex().Matches(content))
        {
            var resourceType = m.Groups["type"].Value;
            var resourceName = m.Groups["name"].Value;
            var body = ExtractBlockBody(content, m.Index);
            var qn = $"{_context.ProjectName}.resource.{resourceType}.{resourceName}";

            var props = new Dictionary<string, object>
            {
                ["resource_type"] = resourceType,
                ["provider"] = resourceType.Contains('_') ? resourceType[..resourceType.IndexOf('_')] : resourceType,
                ["confidence"] = "high"
            };

            // Extract key properties from the body
            ExtractResourceProperties(body, resourceType, props);

            _nodes.Add(MakeNode(qn, $"{resourceType}.{resourceName}", NodeLabel.TerraformResource, props));

            // depends_on → explicit dependency edges
            ExtractDependsOn(body, qn);

            // Cross-repo edges based on resource type
            ExtractCrossRepoEdges(resourceType, resourceName, body, qn);
        }
    }

    // ── Data sources ────────────────────────────────────────────────

    private void ParseDataSources(string content)
    {
        foreach (Match m in DataBlockRegex().Matches(content))
        {
            var dataType = m.Groups["type"].Value;
            var dataName = m.Groups["name"].Value;
            var qn = $"{_context.ProjectName}.data.{dataType}.{dataName}";

            _nodes.Add(MakeNode(qn, $"data.{dataType}.{dataName}", NodeLabel.TerraformDataSource, new()
            {
                ["data_type"] = dataType,
                ["provider"] = dataType.Contains('_') ? dataType[..dataType.IndexOf('_')] : dataType,
                ["confidence"] = "high"
            }));
        }
    }

    // ── Modules ─────────────────────────────────────────────────────

    private void ParseModules(string content)
    {
        foreach (Match m in ModuleBlockRegex().Matches(content))
        {
            var moduleName = m.Groups["name"].Value;
            var body = ExtractBlockBody(content, m.Index);
            var qn = $"{_context.ProjectName}.module.{moduleName}";

            var source = ExtractStringValue(body, "source");
            var version = ExtractStringValue(body, "version");

            var props = new Dictionary<string, object> { ["confidence"] = "high" };
            if (source != null) props["source"] = source;
            if (version != null) props["version"] = version;

            _nodes.Add(MakeNode(qn, moduleName, NodeLabel.TerraformModule, props));

            // Module source → INCLUDES_MODULE edge
            if (source != null)
            {
                var localSource = source.TrimStart('.', '/');
                var sourceQN = source.StartsWith("./") || source.StartsWith("../")
                    ? $"{_context.ProjectName}.module_source.{Sanitize(localSource)}"
                    : $"terraform_registry:{source}";

                _edges.Add(new PendingEdge(qn, sourceQN, EdgeType.INCLUDES_MODULE,
                    new() { ["source"] = source }));
            }

            ExtractDependsOn(body, qn);
        }
    }

    // ── Variables ────────────────────────────────────────────────────

    private void ParseVariables(string content)
    {
        foreach (Match m in VariableBlockRegex().Matches(content))
        {
            var varName = m.Groups["name"].Value;
            var body = ExtractBlockBody(content, m.Index);
            var qn = $"{_context.ProjectName}.var.{varName}";

            var props = new Dictionary<string, object> { ["confidence"] = "high" };

            var varType = ExtractStringValue(body, "type");
            var defaultVal = ExtractStringValue(body, "default");
            var description = ExtractStringValue(body, "description");

            if (varType != null) props["type"] = varType;
            if (defaultVal != null) props["default_value"] = defaultVal;
            if (description != null) props["description"] = description;

            _nodes.Add(MakeNode(qn, varName, NodeLabel.TerraformVariable, props));
        }
    }

    // ── Outputs ──────────────────────────────────────────────────────

    private void ParseOutputs(string content)
    {
        foreach (Match m in OutputBlockRegex().Matches(content))
        {
            var outputName = m.Groups["name"].Value;
            var body = ExtractBlockBody(content, m.Index);
            var qn = $"{_context.ProjectName}.output.{outputName}";

            var props = new Dictionary<string, object> { ["confidence"] = "high" };

            var description = ExtractStringValue(body, "description");
            var value = ExtractStringValue(body, "value");

            if (description != null) props["description"] = description;
            if (value != null) props["value_expression"] = value;

            _nodes.Add(MakeNode(qn, outputName, NodeLabel.TerraformOutput, props));

            // If the value references a resource, create an edge
            if (value != null)
            {
                foreach (Match refMatch in ResourceRefRegex().Matches(value))
                {
                    var refType = refMatch.Groups["type"].Value;
                    var refName = refMatch.Groups["name"].Value;
                    var targetQN = $"{_context.ProjectName}.resource.{refType}.{refName}";
                    _edges.Add(new PendingEdge(qn, targetQN, EdgeType.CALLS));
                }
            }
        }
    }

    // ── Locals ───────────────────────────────────────────────────────

    private void ParseLocals(string content)
    {
        foreach (Match m in LocalsBlockRegex().Matches(content))
        {
            var body = ExtractBlockBody(content, m.Index);
            foreach (Match assign in LocalAssignmentRegex().Matches(body))
            {
                var localName = assign.Groups["name"].Value;
                var qn = $"{_context.ProjectName}.local.{localName}";

                _nodes.Add(MakeNode(qn, localName, NodeLabel.TerraformVariable, new()
                {
                    ["scope"] = "local",
                    ["confidence"] = "high"
                }));
            }
        }
    }

    // ── Tfvars ───────────────────────────────────────────────────────

    private void ParseTfvars(string content)
    {
        foreach (Match m in TfvarAssignmentRegex().Matches(content))
        {
            var varName = m.Groups["name"].Value;
            var value = m.Groups["value"].Value.Trim().Trim('"');
            var qn = $"{_context.ProjectName}.tfvar.{varName}";

            var props = new Dictionary<string, object>
            {
                ["scope"] = "tfvar",
                ["confidence"] = "high"
            };

            if (value.Length <= 200)
                props["value_preview"] = value;

            // Detect service references in values
            if (value.Contains("://") || value.Contains(":5") ||
                value.Contains(":8") || value.Contains(":3"))
            {
                props["likely_service_ref"] = true;
            }

            _nodes.Add(MakeNode(qn, varName, NodeLabel.TerraformVariable, props));
        }
    }

    // ── Resource property extraction ────────────────────────────────

    private static void ExtractResourceProperties(string body, string resourceType, Dictionary<string, object> props)
    {
        // Extract common identifying properties
        var name = ExtractStringValue(body, "name");
        if (name != null) props["display_name"] = name;

        var tags = ExtractMapValues(body, "tags");
        if (tags.TryGetValue("Name", out var tagName))
            props["tag_name"] = tagName;

        // Resource-type-specific properties
        if (resourceType.Contains("ecs_service") || resourceType.Contains("ecs_task"))
        {
            var image = ExtractStringValue(body, "image");
            if (image != null) props["image"] = image;
            var containerName = ExtractStringValue(body, "container_name");
            if (containerName != null) props["container_name"] = containerName;
        }
        else if (resourceType.Contains("lambda"))
        {
            var handler = ExtractStringValue(body, "handler");
            var runtime = ExtractStringValue(body, "runtime");
            if (handler != null) props["handler"] = handler;
            if (runtime != null) props["runtime"] = runtime;
        }
        else if (resourceType.Contains("db_instance") || resourceType.Contains("rds"))
        {
            var engine = ExtractStringValue(body, "engine");
            var dbName = ExtractStringValue(body, "db_name") ?? ExtractStringValue(body, "name");
            if (engine != null) props["engine"] = engine;
            if (dbName != null) props["database_name"] = dbName;
        }
        else if (resourceType.Contains("sqs_queue"))
        {
            var queueName = ExtractStringValue(body, "name");
            if (queueName != null) props["queue_name"] = queueName;
        }
    }

    // ── depends_on extraction ───────────────────────────────────────

    private void ExtractDependsOn(string body, string sourceQN)
    {
        var match = DependsOnRegex().Match(body);
        if (!match.Success) return;

        var deps = match.Groups["deps"].Value;
        foreach (Match dep in ResourceRefRegex().Matches(deps))
        {
            var refType = dep.Groups["type"].Value;
            var refName = dep.Groups["name"].Value;
            var targetQN = $"{_context.ProjectName}.resource.{refType}.{refName}";
            _edges.Add(new PendingEdge(sourceQN, targetQN, EdgeType.DEPENDS_ON));
        }

        // Also handle module references in depends_on
        foreach (Match dep in ModuleRefRegex().Matches(deps))
        {
            var moduleName = dep.Groups["name"].Value;
            var targetQN = $"{_context.ProjectName}.module.{moduleName}";
            _edges.Add(new PendingEdge(sourceQN, targetQN, EdgeType.DEPENDS_ON));
        }
    }

    // ── Resource cross-references ───────────────────────────────────

    private void ExtractResourceReferences(string content)
    {
        // Find all resource.type.name references outside of depends_on
        // and create edges between resources that reference each other
        var resourceQNs = _nodes
            .Where(n => n.Label == NodeLabel.TerraformResource)
            .Select(n => n.QualifiedName)
            .ToHashSet();

        foreach (var node in _nodes.ToList())
        {
            if (node.Label is not (NodeLabel.TerraformResource or NodeLabel.TerraformModule
                or NodeLabel.TerraformOutput))
                continue;

            // Find the block body for this node by searching for its definition
            // Use a simpler approach: find resource refs in the content after this node's position
            // Since we can't reliably scope to a block with regex, we track which refs exist
        }
    }

    // ── Cross-repo edge detection ───────────────────────────────────

    private void ExtractCrossRepoEdges(string resourceType, string resourceName,
        string body, string resourceQN)
    {
        // ECS/Fargate → DEPLOYS
        if (resourceType is "aws_ecs_service" or "aws_ecs_task_definition")
        {
            var image = ExtractStringValue(body, "image");
            var containerName = ExtractStringValue(body, "container_name")
                ?? ExtractStringValue(body, "name") ?? resourceName;

            var props = new Dictionary<string, object>
            {
                ["container"] = containerName,
                ["confidence"] = "medium"
            };
            if (image != null) props["image"] = image;
            _edges.Add(new PendingEdge(resourceQN, containerName, EdgeType.DEPLOYS, props));
        }
        // Lambda → DEPLOYS
        else if (resourceType is "aws_lambda_function")
        {
            var funcName = ExtractStringValue(body, "function_name") ?? resourceName;
            _edges.Add(new PendingEdge(resourceQN, funcName, EdgeType.DEPLOYS,
                new() { ["function_name"] = funcName, ["confidence"] = "medium" }));
        }
        // SQS → maps to messaging infrastructure
        else if (resourceType is "aws_sqs_queue")
        {
            var queueName = ExtractStringValue(body, "name") ?? resourceName;
            _edges.Add(new PendingEdge(resourceQN, queueName, EdgeType.CONFIGURES,
                new() { ["queue_name"] = queueName, ["confidence"] = "medium" }));
        }
        // RDS/Aurora → CONFIGURES database
        else if (resourceType.Contains("db_instance") || resourceType.Contains("rds") ||
                 resourceType.Contains("aurora"))
        {
            var dbName = ExtractStringValue(body, "db_name")
                ?? ExtractStringValue(body, "name") ?? resourceName;
            _edges.Add(new PendingEdge(resourceQN, dbName, EdgeType.CONFIGURES,
                new() { ["database"] = dbName, ["confidence"] = "medium" }));
        }
        // ElastiCache/Redis → CONFIGURES
        else if (resourceType.Contains("elasticache"))
        {
            var clusterName = ExtractStringValue(body, "cluster_id")
                ?? ExtractStringValue(body, "replication_group_id") ?? resourceName;
            _edges.Add(new PendingEdge(resourceQN, clusterName, EdgeType.CONFIGURES,
                new() { ["cache_cluster"] = clusterName, ["confidence"] = "medium" }));
        }
        // Load balancer / target group → DEPLOYS
        else if (resourceType is "aws_lb" or "aws_alb" or "aws_lb_target_group" or "aws_alb_target_group")
        {
            var lbName = ExtractStringValue(body, "name") ?? resourceName;
            _edges.Add(new PendingEdge(resourceQN, lbName, EdgeType.DEPLOYS,
                new() { ["load_balancer"] = lbName, ["confidence"] = "low" }));
        }
        // S3 → CONFIGURES
        else if (resourceType is "aws_s3_bucket")
        {
            var bucket = ExtractStringValue(body, "bucket") ?? resourceName;
            _edges.Add(new PendingEdge(resourceQN, bucket, EdgeType.CONFIGURES,
                new() { ["bucket"] = bucket, ["confidence"] = "medium" }));
        }
        // Route53 → CONFIGURES DNS
        else if (resourceType.Contains("route53_record"))
        {
            var dnsName = ExtractStringValue(body, "name") ?? resourceName;
            _edges.Add(new PendingEdge(resourceQN, dnsName, EdgeType.CONFIGURES,
                new() { ["dns_record"] = dnsName, ["confidence"] = "medium" }));
        }
        // IIS / Windows (azurerm_windows_web_app, azurerm_app_service)
        else if (resourceType.Contains("web_app") || resourceType.Contains("app_service"))
        {
            var appName = ExtractStringValue(body, "name") ?? resourceName;
            _edges.Add(new PendingEdge(resourceQN, appName, EdgeType.DEPLOYS,
                new() { ["app_service"] = appName, ["confidence"] = "medium" }));
        }
        // Azure Container Instance / AKS
        else if (resourceType.Contains("container_group") || resourceType.Contains("kubernetes"))
        {
            var containerName = ExtractStringValue(body, "name") ?? resourceName;
            var image = ExtractStringValue(body, "image");
            var props = new Dictionary<string, object>
            {
                ["container"] = containerName,
                ["confidence"] = "medium"
            };
            if (image != null) props["image"] = image;
            _edges.Add(new PendingEdge(resourceQN, containerName, EdgeType.DEPLOYS, props));
        }
    }

    // ── Static reference analysis ───────────────────────────────────

    private static readonly Regex UrlPattern = new(
        @"https?://[\w\-\.]+(?::\d+)?(?:/[\w\-\./%]*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HostPortPattern = new(
        @"(?<host>[\w\-\.]+\.(?:internal|local|corp|svc|cluster\.local|com|net|io))(?::(?<port>\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ConnectionStringPattern = new(
        @"(?:Server|Data Source|Host|endpoint)=(?<host>[^;""]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private void ScanForReferences(string content)
    {
        var parentQN = $"{_context.ProjectName}.file.{Path.GetFileNameWithoutExtension(_filePath)}";

        foreach (Match m in StringLiteralRegex().Matches(content))
        {
            var value = m.Groups["value"].Value;
            if (string.IsNullOrWhiteSpace(value)) continue;

            foreach (Match url in UrlPattern.Matches(value))
            {
                if (_discoveredReferences.Add($"url:{url.Value}"))
                    _unresolvedCalls.Add(new UnresolvedCall(parentQN, url.Value, "terraform_url_ref", 0.4));
            }

            foreach (Match host in HostPortPattern.Matches(value))
            {
                var h = host.Groups["host"].Value;
                if (_discoveredReferences.Add($"host:{h}"))
                    _unresolvedCalls.Add(new UnresolvedCall(parentQN, h, "terraform_host_ref", 0.3));
            }

            foreach (Match conn in ConnectionStringPattern.Matches(value))
            {
                var h = conn.Groups["host"].Value;
                if (_discoveredReferences.Add($"connstr:{h}"))
                    _unresolvedCalls.Add(new UnresolvedCall(parentQN, h, "terraform_connection_ref", 0.5));
            }
        }
    }

    // ── HCL block body extraction ───────────────────────────────────

    /// <summary>
    /// Starting from the opening '{', extract the body up to the matching '}'.
    /// </summary>
    private static string ExtractBlockBody(string content, int startIndex)
    {
        // Find the opening brace
        var braceStart = content.IndexOf('{', startIndex);
        if (braceStart < 0) return "";

        int depth = 1;
        int pos = braceStart + 1;
        while (pos < content.Length && depth > 0)
        {
            var ch = content[pos];
            if (ch == '{') depth++;
            else if (ch == '}') depth--;
            else if (ch == '"')
            {
                // Skip string literals (handle escaped quotes)
                pos++;
                while (pos < content.Length && content[pos] != '"')
                {
                    if (content[pos] == '\\') pos++; // skip escaped char
                    pos++;
                }
            }
            else if (ch == '#')
            {
                // Skip single-line comments
                while (pos < content.Length && content[pos] != '\n') pos++;
            }

            pos++;
        }

        if (depth != 0) return content[(braceStart + 1)..];
        return content[(braceStart + 1)..(pos - 1)];
    }

    private static string? ExtractStringValue(string body, string key)
    {
        // Match: key = "value" or key = value
        var pattern = $@"(?:^|\n)\s*{Regex.Escape(key)}\s*=\s*""?(?<value>[^""\n]+?)""?\s*(?:\n|$)";
        var match = Regex.Match(body, pattern);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static Dictionary<string, string> ExtractMapValues(string body, string key)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pattern = $@"{Regex.Escape(key)}\s*=\s*\{{";
        var match = Regex.Match(body, pattern);
        if (!match.Success) return result;

        var mapBody = ExtractBlockBody(body, match.Index + match.Length - 1);
        foreach (Match entry in Regex.Matches(mapBody, @"(?<key>\w+)\s*=\s*""(?<value>[^""]*?)"""))
        {
            result[entry.Groups["key"].Value] = entry.Groups["value"].Value;
        }

        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private GraphNode MakeNode(string qualifiedName, string name, NodeLabel label,
        Dictionary<string, object>? properties = null)
    {
        var relativePath = Path.GetRelativePath(_context.RootPath, _filePath)
            .Replace('\\', '/');

        return new GraphNode
        {
            Project = _context.ProjectName,
            Label = label,
            Name = name,
            QualifiedName = qualifiedName,
            FilePath = relativePath,
            Properties = properties ?? new()
        };
    }

    private static string Sanitize(string name) =>
        name.ToLowerInvariant()
            .Replace(' ', '_')
            .Replace('-', '_')
            .Replace('/', '_')
            .Replace('.', '_');

    // ── Compiled regexes ────────────────────────────────────────────

    [GeneratedRegex(@"^\s*resource\s+""(?<type>[^""]+)""\s+""(?<name>[^""]+)""\s*\{",
        RegexOptions.Multiline)]
    private static partial Regex ResourceBlockRegex();

    [GeneratedRegex(@"^\s*data\s+""(?<type>[^""]+)""\s+""(?<name>[^""]+)""\s*\{",
        RegexOptions.Multiline)]
    private static partial Regex DataBlockRegex();

    [GeneratedRegex(@"^\s*module\s+""(?<name>[^""]+)""\s*\{",
        RegexOptions.Multiline)]
    private static partial Regex ModuleBlockRegex();

    [GeneratedRegex(@"^\s*variable\s+""(?<name>[^""]+)""\s*\{",
        RegexOptions.Multiline)]
    private static partial Regex VariableBlockRegex();

    [GeneratedRegex(@"^\s*output\s+""(?<name>[^""]+)""\s*\{",
        RegexOptions.Multiline)]
    private static partial Regex OutputBlockRegex();

    [GeneratedRegex(@"^\s*locals\s*\{", RegexOptions.Multiline)]
    private static partial Regex LocalsBlockRegex();

    [GeneratedRegex(@"^\s*(?<name>\w+)\s*=", RegexOptions.Multiline)]
    private static partial Regex LocalAssignmentRegex();

    [GeneratedRegex(@"^\s*(?<name>\w+)\s*=\s*(?<value>.+)", RegexOptions.Multiline)]
    private static partial Regex TfvarAssignmentRegex();

    [GeneratedRegex(@"depends_on\s*=\s*\[(?<deps>[^\]]*)\]")]
    private static partial Regex DependsOnRegex();

    [GeneratedRegex(@"(?<type>[a-z][a-z0-9_]+)\.(?<name>[a-z][a-z0-9_]+)")]
    private static partial Regex ResourceRefRegex();

    [GeneratedRegex(@"module\.(?<name>[a-z][a-z0-9_]+)")]
    private static partial Regex ModuleRefRegex();

    [GeneratedRegex(@"""(?<value>[^""\\]*(?:\\.[^""\\]*)*)""", RegexOptions.Compiled)]
    private static partial Regex StringLiteralRegex();
}
