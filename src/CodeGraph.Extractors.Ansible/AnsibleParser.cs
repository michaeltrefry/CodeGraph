using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using CodeGraph.Models;
using YamlDotNet.RepresentationModel;

namespace CodeGraph.Extractors.Ansible;

/// <summary>
/// YAML-based parser for Ansible playbooks, roles, tasks, handlers, and variables.
/// Returns empty results for non-Ansible YAML files.
/// </summary>
public class AnsibleParser
{
    private readonly ExtractorContext _context;
    private readonly string _filePath;
    private readonly ILogger _logger;

    private readonly List<GraphNode> _nodes = [];
    private readonly List<PendingEdge> _edges = [];
    private readonly List<UnresolvedCall> _unresolvedCalls = [];
    private readonly HashSet<string> _discoveredReferences = [];

    public AnsibleParser(ExtractorContext context, string filePath, ILogger logger)
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

        var fileType = ClassifyFile(_filePath, content);
        if (fileType == AnsibleFileType.NotAnsible)
            return EmptyResult;

        try
        {
            var yaml = new YamlStream();
            yaml.Load(new StringReader(content));

            if (yaml.Documents.Count == 0)
                return EmptyResult;

            var root = yaml.Documents[0].RootNode;

            switch (fileType)
            {
                case AnsibleFileType.Playbook:
                    ParsePlaybook(root);
                    break;
                case AnsibleFileType.TasksFile:
                    ParseTaskList(root, ParentQN());
                    break;
                case AnsibleFileType.HandlersFile:
                    ParseHandlerList(root);
                    break;
                case AnsibleFileType.VarsFile:
                case AnsibleFileType.DefaultsFile:
                    ParseVariables(root, fileType);
                    break;
                case AnsibleFileType.MetaFile:
                    ParseRoleMeta(root);
                    break;
                case AnsibleFileType.RequirementsFile:
                    ParseRequirements(root);
                    break;
            }

            // Static reference analysis: scan all string values for service refs
            ScanForReferences(root);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Ansible YAML {File}", _filePath);
        }

        if (_nodes.Count == 0 && _edges.Count == 0 && _discoveredReferences.Count == 0)
            return EmptyResult;

        return new ExtractionResult
        {
            Nodes = _nodes,
            Edges = _edges,
            UnresolvedCalls = _unresolvedCalls,
            Metadata = new ProjectMetadata("YAML", "Ansible")
        };
    }

    // -- File classification -----------------------------------------

    private static AnsibleFileType ClassifyFile(string filePath, string content)
    {
        var normalized = filePath.Replace('\\', '/');
        var segments = normalized.Split('/');

        // Check path-based classification (role structure)
        for (int i = 0; i < segments.Length; i++)
        {
            if (!segments[i].Equals("roles", StringComparison.OrdinalIgnoreCase))
                continue;

            // We're inside a roles/ directory. Look at subdirectory name.
            if (i + 2 < segments.Length)
            {
                var subdir = segments[i + 2].ToLowerInvariant();
                return subdir switch
                {
                    "tasks" => AnsibleFileType.TasksFile,
                    "handlers" => AnsibleFileType.HandlersFile,
                    "vars" => AnsibleFileType.VarsFile,
                    "defaults" => AnsibleFileType.DefaultsFile,
                    "meta" => AnsibleFileType.MetaFile,
                    _ => AnsibleFileType.NotAnsible
                };
            }
        }

        // Standalone role: the repo root IS the role, so the immediate parent
        // directory name (tasks/, handlers/, etc.) is the indicator.
        // Match when the second-to-last segment is a known role subdirectory.
        if (segments.Length >= 2)
        {
            var parentDir = segments[^2].ToLowerInvariant();
            var standaloneMatch = parentDir switch
            {
                "tasks" => AnsibleFileType.TasksFile,
                "handlers" => AnsibleFileType.HandlersFile,
                "vars" => AnsibleFileType.VarsFile,
                "defaults" => AnsibleFileType.DefaultsFile,
                "meta" => AnsibleFileType.MetaFile,
                _ => (AnsibleFileType?)null
            };
            if (standaloneMatch is not null)
                return standaloneMatch.Value;
        }

        // Check for requirements.yml (role dependencies)
        var fileName = Path.GetFileName(filePath);
        if (fileName.Equals("requirements.yml", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("requirements.yaml", StringComparison.OrdinalIgnoreCase))
            return AnsibleFileType.RequirementsFile;

        // Content-based: look for playbook markers (list of plays with hosts:)
        if (content.Contains("hosts:", StringComparison.Ordinal) &&
            (content.Contains("tasks:", StringComparison.Ordinal) ||
             content.Contains("roles:", StringComparison.Ordinal) ||
             content.Contains("pre_tasks:", StringComparison.Ordinal) ||
             content.Contains("post_tasks:", StringComparison.Ordinal)))
            return AnsibleFileType.Playbook;

        // Content-based: standalone task file (list starting with - name: or - include)
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("- name:", StringComparison.Ordinal) ||
            trimmed.StartsWith("---\n- name:", StringComparison.Ordinal) ||
            trimmed.StartsWith("- include:", StringComparison.Ordinal) ||
            trimmed.StartsWith("- include_tasks:", StringComparison.Ordinal) ||
            trimmed.StartsWith("- import_tasks:", StringComparison.Ordinal) ||
            trimmed.StartsWith("---\n- include:", StringComparison.Ordinal))
            return AnsibleFileType.TasksFile;

        return AnsibleFileType.NotAnsible;
    }

    // -- Playbooks ---------------------------------------------------

    private void ParsePlaybook(YamlNode root)
    {
        if (root is not YamlSequenceNode plays) return;

        var playbookName = Path.GetFileNameWithoutExtension(_filePath);
        var playbookQN = $"{_context.ProjectName}.playbook.{playbookName}";

        _nodes.Add(MakeNode(playbookQN, playbookName, NodeLabel.Playbook, new()
        {
            ["type"] = "playbook"
        }));

        foreach (var playNode in plays.Children)
        {
            if (playNode is not YamlMappingNode play) continue;

            var playName = GetScalar(play, "name") ?? "unnamed_play";
            var hosts = GetScalar(play, "hosts") ?? "unknown";

            // Extract roles referenced in this play
            if (play.Children.TryGetValue(new YamlScalarNode("roles"), out var rolesNode) &&
                rolesNode is YamlSequenceNode rolesList)
            {
                ParsePlaybookRoles(rolesList, playbookQN);
            }

            // Extract tasks in this play
            foreach (var taskListKey in new[] { "tasks", "pre_tasks", "post_tasks" })
            {
                if (play.Children.TryGetValue(new YamlScalarNode(taskListKey), out var tasksNode) &&
                    tasksNode is YamlSequenceNode taskList)
                {
                    ParseTaskList(taskList, playbookQN);
                }
            }

            // Extract handlers in this play
            if (play.Children.TryGetValue(new YamlScalarNode("handlers"), out var handlersNode) &&
                handlersNode is YamlSequenceNode handlerList)
            {
                ParseHandlerList(handlerList);
            }

            // Cross-repo: hosts can map to services/servers
            AddHostsProperty(playbookQN, hosts);
        }
    }

    private void ParsePlaybookRoles(YamlSequenceNode rolesList, string parentQN)
    {
        foreach (var roleRef in rolesList.Children)
        {
            string? roleName = null;

            if (roleRef is YamlScalarNode scalar)
            {
                roleName = scalar.Value;
            }
            else if (roleRef is YamlMappingNode mapping)
            {
                roleName = GetScalar(mapping, "role") ?? GetScalar(mapping, "name");
            }

            if (string.IsNullOrEmpty(roleName)) continue;

            var roleQN = $"{_context.ProjectName}.role.{roleName}";

            // Create a role node (may be merged with existing one from the role's own files)
            _nodes.Add(MakeNode(roleQN, roleName, NodeLabel.Role, new()
            {
                ["confidence"] = "medium"
            }));

            _edges.Add(new PendingEdge(parentQN, roleQN, EdgeType.INCLUDES_ROLE));
        }
    }

    // -- Tasks -------------------------------------------------------

    private void ParseTaskList(YamlNode root, string parentQN)
    {
        var tasks = root as YamlSequenceNode;
        if (tasks is null) return;

        foreach (var taskNode in tasks.Children)
        {
            if (taskNode is not YamlMappingNode task) continue;

            // Skip block/rescue/always wrappers - recurse into them
            if (task.Children.ContainsKey(new YamlScalarNode("block")))
            {
                if (task.Children.TryGetValue(new YamlScalarNode("block"), out var block))
                    ParseTaskList(block, parentQN);
                if (task.Children.TryGetValue(new YamlScalarNode("rescue"), out var rescue))
                    ParseTaskList(rescue, parentQN);
                if (task.Children.TryGetValue(new YamlScalarNode("always"), out var always))
                    ParseTaskList(always, parentQN);
                continue;
            }

            ParseSingleTask(task, parentQN);
        }
    }

    private void ParseSingleTask(YamlMappingNode task, string parentQN)
    {
        var taskName = GetScalar(task, "name") ?? "unnamed_task";
        var module = DetectModule(task);
        var safeTaskName = Sanitize(taskName);
        var taskQN = $"{parentQN}.task.{safeTaskName}";

        var props = new Dictionary<string, object>
        {
            ["confidence"] = "high"
        };
        if (module != null)
            props["module"] = module;

        _nodes.Add(MakeNode(taskQN, taskName, NodeLabel.AnsibleTask, props));
        _edges.Add(new PendingEdge(parentQN, taskQN, EdgeType.DEFINES));

        // Handle notify -> handler edges
        if (task.Children.TryGetValue(new YamlScalarNode("notify"), out var notifyNode))
        {
            var handlerNames = new List<string>();
            if (notifyNode is YamlScalarNode singleHandler)
                handlerNames.Add(singleHandler.Value ?? "");
            else if (notifyNode is YamlSequenceNode handlerSeq)
                handlerNames.AddRange(handlerSeq.Children.OfType<YamlScalarNode>()
                    .Select(s => s.Value ?? ""));

            foreach (var handler in handlerNames.Where(h => !string.IsNullOrEmpty(h)))
            {
                var handlerQN = $"{_context.ProjectName}.handler.{Sanitize(handler)}";
                _edges.Add(new PendingEdge(taskQN, handlerQN, EdgeType.NOTIFIES_HANDLER));
            }
        }

        // Handle include_role / import_role
        foreach (var includeKey in new[] { "include_role", "import_role" })
        {
            if (task.Children.TryGetValue(new YamlScalarNode(includeKey), out var includeNode) &&
                includeNode is YamlMappingNode includeMap)
            {
                var roleName = GetScalar(includeMap, "name");
                if (!string.IsNullOrEmpty(roleName))
                {
                    var roleQN = $"{_context.ProjectName}.role.{roleName}";
                    _nodes.Add(MakeNode(roleQN, roleName, NodeLabel.Role));
                    _edges.Add(new PendingEdge(taskQN, roleQN, EdgeType.INCLUDES_ROLE));
                }
            }
        }

        // Handle include_tasks / import_tasks
        foreach (var includeKey in new[] { "include_tasks", "import_tasks" })
        {
            var includeFile = GetScalar(task, includeKey);
            if (!string.IsNullOrEmpty(includeFile))
            {
                var targetQN = $"{parentQN}.taskfile.{Sanitize(Path.GetFileNameWithoutExtension(includeFile))}";
                _edges.Add(new PendingEdge(taskQN, targetQN, EdgeType.CALLS));
            }
        }

        // Cross-repo edges based on module type
        ExtractCrossRepoEdges(task, module, taskQN);
    }

    // -- Handlers ----------------------------------------------------

    private void ParseHandlerList(YamlNode root)
    {
        if (root is not YamlSequenceNode handlers) return;

        foreach (var handlerNode in handlers.Children)
        {
            if (handlerNode is not YamlMappingNode handler) continue;

            var handlerName = GetScalar(handler, "name") ?? "unnamed_handler";
            var module = DetectModule(handler);
            var handlerQN = $"{_context.ProjectName}.handler.{Sanitize(handlerName)}";

            var props = new Dictionary<string, object>
            {
                ["confidence"] = "high"
            };
            if (module != null)
                props["module"] = module;

            _nodes.Add(MakeNode(handlerQN, handlerName, NodeLabel.AnsibleHandler, props));

            // Listen directives create additional handler aliases
            var listen = GetScalar(handler, "listen");
            if (!string.IsNullOrEmpty(listen))
            {
                props["listen"] = listen;
            }

            ExtractCrossRepoEdges(handler, module, handlerQN);
        }
    }

    // -- Variables ----------------------------------------------------

    private void ParseVariables(YamlNode root, AnsibleFileType fileType)
    {
        if (root is not YamlMappingNode vars) return;

        var roleName = DetectRoleName() ?? Path.GetFileNameWithoutExtension(_filePath);
        var scope = fileType == AnsibleFileType.DefaultsFile ? "default" : "var";

        foreach (var entry in vars.Children)
        {
            if (entry.Key is not YamlScalarNode keyNode) continue;
            var varName = keyNode.Value ?? "unknown";
            var varQN = $"{_context.ProjectName}.{scope}.{roleName}.{varName}";

            var props = new Dictionary<string, object>
            {
                ["scope"] = scope,
                ["confidence"] = "high"
            };

            // Capture the value type and simple values for reference tracking
            if (entry.Value is YamlScalarNode valueNode && valueNode.Value != null)
            {
                props["value_preview"] = valueNode.Value.Length > 100
                    ? valueNode.Value[..100] + "..."
                    : valueNode.Value;

                // Detect service references in variable values
                if (valueNode.Value.Contains("://") || valueNode.Value.Contains(":5") ||
                    valueNode.Value.Contains(":8") || valueNode.Value.Contains(":3"))
                {
                    props["likely_service_ref"] = true;
                }
            }

            _nodes.Add(MakeNode(varQN, varName, NodeLabel.AnsibleVariable, props));
        }
    }

    // -- Role meta (dependencies) ------------------------------------

    private void ParseRoleMeta(YamlNode root)
    {
        if (root is not YamlMappingNode meta) return;

        var roleName = DetectRoleName() ?? Path.GetFileNameWithoutExtension(_filePath);
        var roleQN = $"{_context.ProjectName}.role.{roleName}";

        _nodes.Add(MakeNode(roleQN, roleName, NodeLabel.Role, new()
        {
            ["confidence"] = "high"
        }));

        if (meta.Children.TryGetValue(new YamlScalarNode("dependencies"), out var depsNode) &&
            depsNode is YamlSequenceNode deps)
        {
            foreach (var dep in deps.Children)
            {
                string? depName = null;
                if (dep is YamlScalarNode scalar)
                    depName = scalar.Value;
                else if (dep is YamlMappingNode mapping)
                    depName = GetScalar(mapping, "role") ?? GetScalar(mapping, "name");

                if (string.IsNullOrEmpty(depName)) continue;

                var depQN = $"{_context.ProjectName}.role.{depName}";
                _nodes.Add(MakeNode(depQN, depName, NodeLabel.Role));
                _edges.Add(new PendingEdge(roleQN, depQN, EdgeType.INCLUDES_ROLE));
            }
        }

        // Galaxy metadata
        if (meta.Children.TryGetValue(new YamlScalarNode("galaxy_info"), out var galaxyNode) &&
            galaxyNode is YamlMappingNode galaxy)
        {
            var desc = GetScalar(galaxy, "description");
            if (!string.IsNullOrEmpty(desc))
            {
                // Update role node with description
                _nodes.Add(MakeNode(roleQN, roleName, NodeLabel.Role, new()
                {
                    ["description"] = desc,
                    ["confidence"] = "high"
                }));
            }
        }
    }

    // -- Requirements file -------------------------------------------

    private void ParseRequirements(YamlNode root)
    {
        YamlSequenceNode? roles = null;

        if (root is YamlSequenceNode seq)
        {
            roles = seq;
        }
        else if (root is YamlMappingNode map &&
                 map.Children.TryGetValue(new YamlScalarNode("roles"), out var rolesNode))
        {
            roles = rolesNode as YamlSequenceNode;
        }

        if (roles is null) return;

        foreach (var roleRef in roles.Children)
        {
            string? roleName = null;
            string? src = null;

            if (roleRef is YamlScalarNode scalar)
            {
                roleName = scalar.Value;
            }
            else if (roleRef is YamlMappingNode mapping)
            {
                roleName = GetScalar(mapping, "name") ?? GetScalar(mapping, "role");
                src = GetScalar(mapping, "src");
            }

            if (string.IsNullOrEmpty(roleName) && string.IsNullOrEmpty(src)) continue;
            var name = roleName ?? src!;
            var roleQN = $"{_context.ProjectName}.role.{name}";

            var props = new Dictionary<string, object> { ["confidence"] = "medium" };
            if (src != null) props["source"] = src;

            _nodes.Add(MakeNode(roleQN, name, NodeLabel.Role, props));
        }
    }

    // -- Cross-repo edge detection -----------------------------------

    private void ExtractCrossRepoEdges(YamlMappingNode task, string? module, string taskQN)
    {
        if (module is null) return;

        switch (module)
        {
            // Service management -> DEPLOYS
            case "service" or "systemd" or "win_service":
            {
                var serviceName = GetModuleArg(task, module, "name");
                if (!string.IsNullOrEmpty(serviceName))
                {
                    _edges.Add(new PendingEdge(taskQN, serviceName, EdgeType.DEPLOYS,
                        new() { ["service_name"] = serviceName, ["confidence"] = "medium" }));
                }
                break;
            }

            // HTTP calls -> HTTP_CALLS
            case "uri" or "win_uri":
            {
                var url = GetModuleArg(task, module, "url");
                if (!string.IsNullOrEmpty(url))
                {
                    _edges.Add(new PendingEdge(taskQN, url, EdgeType.HTTP_CALLS,
                        new() { ["url"] = url, ["confidence"] = "low" }));
                }
                break;
            }

            // Template/copy -> CONFIGURES
            case "template" or "copy":
            {
                var dest = GetModuleArg(task, module, "dest");
                if (!string.IsNullOrEmpty(dest))
                {
                    _edges.Add(new PendingEdge(taskQN, dest, EdgeType.CONFIGURES,
                        new() { ["destination"] = dest, ["confidence"] = "medium" }));
                }
                break;
            }

            // Docker -> DEPLOYS
            case "docker_container" or "community.docker.docker_container":
            {
                var image = GetModuleArg(task, module, "image");
                var containerName = GetModuleArg(task, module, "name");
                if (!string.IsNullOrEmpty(containerName))
                {
                    var props = new Dictionary<string, object>
                    {
                        ["container"] = containerName,
                        ["confidence"] = "medium"
                    };
                    if (image != null) props["image"] = image;
                    _edges.Add(new PendingEdge(taskQN, containerName, EdgeType.DEPLOYS, props));
                }
                break;
            }

            // IIS -> DEPLOYS
            case "win_iis_website" or "community.windows.win_iis_website":
            {
                var siteName = GetModuleArg(task, module, "name");
                if (!string.IsNullOrEmpty(siteName))
                {
                    _edges.Add(new PendingEdge(taskQN, siteName, EdgeType.DEPLOYS,
                        new() { ["iis_site"] = siteName, ["confidence"] = "medium" }));
                }
                break;
            }

            // Database operations
            case "mysql_db" or "community.mysql.mysql_db":
            {
                var dbName = GetModuleArg(task, module, "name");
                if (!string.IsNullOrEmpty(dbName))
                {
                    _edges.Add(new PendingEdge(taskQN, dbName, EdgeType.CONFIGURES,
                        new() { ["database"] = dbName, ["confidence"] = "medium" }));
                }
                break;
            }
        }
    }

    // -- Module detection --------------------------------------------

    private static readonly HashSet<string> NonModuleKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "when", "register", "notify", "listen", "tags", "vars",
        "with_items", "with_dict", "with_fileglob", "loop", "loop_control",
        "until", "retries", "delay", "changed_when", "failed_when",
        "ignore_errors", "become", "become_user", "become_method",
        "environment", "no_log", "check_mode", "diff", "any_errors_fatal",
        "block", "rescue", "always", "include_role", "import_role",
        "include_tasks", "import_tasks", "include_vars", "set_fact",
        "delegate_to", "run_once", "connection", "timeout"
    };

    private static string? DetectModule(YamlMappingNode task)
    {
        foreach (var entry in task.Children)
        {
            if (entry.Key is not YamlScalarNode key || key.Value is null) continue;
            if (NonModuleKeys.Contains(key.Value)) continue;

            // Module keys typically have a mapping or string value for their args
            if (entry.Value is YamlMappingNode or YamlScalarNode)
                return key.Value;
        }

        return null;
    }

    private static string? GetModuleArg(YamlMappingNode task, string module, string argName)
    {
        if (!task.Children.TryGetValue(new YamlScalarNode(module), out var moduleNode))
            return null;

        if (moduleNode is YamlMappingNode moduleArgs)
            return GetScalar(moduleArgs, argName);

        return null;
    }

    // -- Static reference analysis -------------------------------------

    // Patterns for detecting infrastructure references in string values
    private static readonly Regex UrlPattern = new(
        @"https?://[\w\-\.]+(?::\d+)?(?:/[\w\-\./%]*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HostPortPattern = new(
        @"(?<host>[\w\-\.]+\.(?:internal|local|corp|svc|cluster\.local|com|net|io))(?::(?<port>\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ConnectionStringPattern = new(
        @"(?:Server|Data Source|Host)=(?<host>[^;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QueuePattern = new(
        @"(?:queue|exchange|topic)[\w\-\s]*[:=]\s*[""']?(?<name>[\w\-\.]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private void ScanForReferences(YamlNode root)
    {
        var parentQN = ParentQN();
        var allValues = new List<string>();
        CollectScalarValues(root, allValues);

        foreach (var value in allValues)
        {
            // URLs - potential HTTP cross-repo calls
            foreach (Match m in UrlPattern.Matches(value))
            {
                var url = m.Value;
                if (_discoveredReferences.Add($"url:{url}"))
                {
                    _unresolvedCalls.Add(new UnresolvedCall(
                        parentQN, url, "ansible_url_ref", 0.4));
                }
            }

            // Host:port patterns - potential service references
            foreach (Match m in HostPortPattern.Matches(value))
            {
                var host = m.Groups["host"].Value;
                if (_discoveredReferences.Add($"host:{host}"))
                {
                    _unresolvedCalls.Add(new UnresolvedCall(
                        parentQN, host, "ansible_host_ref", 0.3));
                }
            }

            // Connection strings
            foreach (Match m in ConnectionStringPattern.Matches(value))
            {
                var host = m.Groups["host"].Value;
                if (_discoveredReferences.Add($"connstr:{host}"))
                {
                    _unresolvedCalls.Add(new UnresolvedCall(
                        parentQN, host, "ansible_connection_ref", 0.5));
                }
            }

            // Queue/exchange names
            foreach (Match m in QueuePattern.Matches(value))
            {
                var queueName = m.Groups["name"].Value;
                if (_discoveredReferences.Add($"queue:{queueName}"))
                {
                    _unresolvedCalls.Add(new UnresolvedCall(
                        parentQN, queueName, "ansible_queue_ref", 0.5));
                }
            }
        }
    }

    private static void CollectScalarValues(YamlNode node, List<string> values)
    {
        switch (node)
        {
            case YamlScalarNode scalar when scalar.Value is not null:
                values.Add(scalar.Value);
                break;
            case YamlSequenceNode seq:
                foreach (var child in seq.Children)
                    CollectScalarValues(child, values);
                break;
            case YamlMappingNode map:
                foreach (var entry in map.Children)
                {
                    // Include "key: value" pairs so patterns like QueuePattern can match on key names
                    if (entry.Key is YamlScalarNode key && key.Value is not null
                        && entry.Value is YamlScalarNode val && val.Value is not null)
                    {
                        values.Add($"{key.Value}: {val.Value}");
                    }
                    CollectScalarValues(entry.Value, values);
                }
                break;
        }
    }

    // -- Helpers ------------------------------------------------------

    private string ParentQN()
    {
        var roleName = DetectRoleName();
        if (roleName != null)
            return $"{_context.ProjectName}.role.{roleName}";

        var fileName = Path.GetFileNameWithoutExtension(_filePath);
        return $"{_context.ProjectName}.taskfile.{fileName}";
    }

    private string? DetectRoleName()
    {
        var normalized = _filePath.Replace('\\', '/');
        var segments = normalized.Split('/');

        // Standard: roles/{rolename}/tasks/main.yml -> rolename
        for (int i = 0; i < segments.Length - 2; i++)
        {
            if (segments[i].Equals("roles", StringComparison.OrdinalIgnoreCase))
                return segments[i + 1];
        }

        // Standalone role: the repo root IS the role (tasks/main.yml directly).
        // Detect by checking if the parent directory is a known role subdirectory.
        if (segments.Length >= 2)
        {
            var parentDir = segments[^2].ToLowerInvariant();
            if (parentDir is "tasks" or "handlers" or "vars" or "defaults" or "meta")
                return _context.ProjectName;
        }

        return null;
    }

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

    private static string? GetScalar(YamlMappingNode mapping, string key)
    {
        if (mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) &&
            value is YamlScalarNode scalar)
            return scalar.Value;
        return null;
    }

    private static string Sanitize(string name) =>
        name.ToLowerInvariant()
            .Replace(' ', '_')
            .Replace('-', '_')
            .Replace('.', '_');

    private void AddHostsProperty(string nodeQN, string hosts)
    {
        // Track host targeting for potential cross-repo resolution
        _unresolvedCalls.Add(new UnresolvedCall(
            nodeQN,
            hosts,
            "ansible_hosts",
            0.5));
    }
}
