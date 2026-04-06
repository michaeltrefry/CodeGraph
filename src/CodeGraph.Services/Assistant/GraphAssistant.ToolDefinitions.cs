using System.Text.Json;
using Anthropic.Models.Messages;

namespace CodeGraph.Services.Assistant;

public partial class GraphAssistant
{
    private static List<ToolUnion> BuildTools() =>
    [
        MakeTool("search_graph",
            "Search for services, endpoints, models, events, or any code element by name pattern.",
            new Dictionary<string, JsonElement>
            {
                ["namePattern"] = Prop("string", "Name pattern to search for (use % as wildcard)"),
                ["label"]       = PropEnum("Filter by node type", "Class","Method","Route","Service","Event","Queue","Table","Interface","Enum","Component","Job","NuGetPackage"),
                ["project"]     = Prop("string", "Filter by project/repository name"),
                ["limit"]       = Prop("integer", "Max results (default 20)")
            },
            ["namePattern"]),

        MakeTool("list_projects",
            "List all indexed repositories with metadata.",
            new Dictionary<string, JsonElement>(), []),

        MakeTool("get_service_summary",
            "Get the natural-language description of a repository — what it does, its endpoints, dependencies.",
            new Dictionary<string, JsonElement>
            {
                ["project"] = Prop("string", "Project/repository name")
            },
            ["project"]),

        MakeTool("trace_call_path",
            "Trace callers or callees of a function/method across services.",
            new Dictionary<string, JsonElement>
            {
                ["functionName"] = Prop("string", "Function or method name to trace"),
                ["direction"]    = PropEnum("Trace direction", "inbound", "outbound", "both"),
                ["depth"]        = Prop("integer", "How many levels deep (default 3)"),
                ["project"]      = Prop("string", "Filter by project")
            },
            ["functionName"]),

        MakeTool("trace_data_lineage",
            "Follow a data model from its database origin through all services that produce or consume it.",
            new Dictionary<string, JsonElement>
            {
                ["modelName"] = Prop("string", "Model/DTO/event class name to trace"),
                ["project"]   = Prop("string", "Filter by project")
            },
            ["modelName"]),

        MakeTool("find_consumers",
            "Find all services/methods that consume a given event, endpoint, or model.",
            new Dictionary<string, JsonElement>
            {
                ["name"]    = Prop("string", "Event, endpoint, or model name"),
                ["project"] = Prop("string", "Filter by project")
            },
            ["name"]),

        MakeTool("find_publishers",
            "Find all services that publish to a given queue, exchange, or event type.",
            new Dictionary<string, JsonElement>
            {
                ["name"]    = Prop("string", "Queue, exchange, or event name"),
                ["project"] = Prop("string", "Filter by project")
            },
            ["name"]),

        MakeTool("get_architecture",
            "Get architecture overview for a project — hotspots, node counts, cross-repo dependencies.",
            new Dictionary<string, JsonElement>
            {
                ["project"] = Prop("string", "Project name")
            },
            ["project"]),

        MakeTool("find_archival_candidates",
            "Find repos with no inbound or outbound cross-repo dependencies — candidates for archival.",
            new Dictionary<string, JsonElement>(), []),

        MakeTool("get_project_health",
            "Get health scores, file-level hotspots, and Claude-generated health analysis for a repository. Shows overall health (1-10), hotspot files, and per-project breakdowns.",
            new Dictionary<string, JsonElement>
            {
                ["project"]     = Prop("string", "Project/repository name"),
                ["topHotspots"] = Prop("integer", "Number of top hotspot files to return (default 10)")
            },
            ["project"]),

        MakeTool("get_fleet_health",
            "Get health overview across all repositories ranked by health score. Use to find which repos need the most attention.",
            new Dictionary<string, JsonElement>
            {
                ["maxHealth"] = Prop("number", "Only show repos with health below this threshold (e.g. 4.0 for unhealthy)"),
                ["limit"]     = Prop("integer", "Max repos to return (default 50)")
            },
            []),

        MakeTool("read_node_source",
            "Read the source code for a graph node by its ID. Returns the file content around the node's line range. Use when you need to see the actual implementation of a class, method, or other code element.",
            new Dictionary<string, JsonElement>
            {
                ["nodeId"] = Prop("integer", "Node ID from the graph")
            },
            ["nodeId"]),

        MakeTool("get_code_snippet",
            "Read source code from a repository file by project name and file path. Use when you know the file path but not a specific node ID.",
            new Dictionary<string, JsonElement>
            {
                ["project"]   = Prop("string", "Project/repository name"),
                ["filePath"]  = Prop("string", "File path relative to repo root"),
                ["startLine"] = Prop("integer", "Start line (0 for beginning)"),
                ["endLine"]   = Prop("integer", "End line (0 for entire file)")
            },
            ["project", "filePath"]),

        MakeTool("get_graph_schema",
            "Describe the available node types, edge types, and their properties in the knowledge graph.",
            new Dictionary<string, JsonElement>(), []),

        MakeTool("list_conventions",
            "List all available company convention documents. These describe patterns, abstractions, and standards used across all repositories.",
            new Dictionary<string, JsonElement>(), []),

        MakeTool("get_convention",
            "Read a company convention document by slug. Conventions describe patterns like gateway calls, messaging, project structure, and other standards. Call list_conventions first to see available documents.",
            new Dictionary<string, JsonElement>
            {
                ["name"] = Prop("string", "Convention slug (e.g., 'gateway-pattern', 'messaging-pattern')")
            },
            ["name"]),
    ];

    private static ToolUnion MakeTool(
        string name, string description,
        Dictionary<string, JsonElement> properties,
        string[] required) =>
        new Tool
        {
            Name = name,
            Description = description,
            InputSchema = new InputSchema
            {
                Properties = properties,
                Required = required.ToList()
            }
        };

    private static JsonElement Prop(string type, string description) =>
        JsonSerializer.SerializeToElement(new { type, description });

    private static JsonElement PropEnum(string description, params string[] values) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "string",
            description,
            @enum = values
        });
}
