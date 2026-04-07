using Microsoft.Extensions.Logging;
using CodeGraph.Models;
using TreeSitter;

namespace CodeGraph.Extractors.TreeSitter;

/// <summary>
/// Tree-sitter based extractor that supports multiple languages through
/// data-driven language specs. Each language defines which AST node types
/// map to functions, classes, calls, and imports.
///
/// Languages with domain-specific semantics can use a custom extractor hook
/// for deeper extraction beyond the generic AST walk.
/// </summary>
public class TreeSitterExtractor : ICodeExtractor
{
    private readonly ILogger<TreeSitterExtractor> _logger;

    public TreeSitterExtractor(ILogger<TreeSitterExtractor> logger)
    {
        _logger = logger;
    }

    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(LanguageSpecs.SupportedExtensions, StringComparer.OrdinalIgnoreCase);

    public Task<ExtractionResult> ExtractAsync(string filePath, string content,
        ExtractorContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult(EmptyResult);

        var ext = Path.GetExtension(filePath);
        var spec = LanguageSpecs.ForExtension(ext);
        if (spec is null)
            return Task.FromResult(EmptyResult);

        try
        {
            var result = ExtractWithSpec(spec, filePath, content, context);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tree-sitter extraction failed for {File} ({Lang})",
                filePath, spec.LanguageName);
            return Task.FromResult(EmptyResult);
        }
    }

    private ExtractionResult ExtractWithSpec(LanguageSpec spec, string filePath,
        string content, ExtractorContext context)
    {
        using var parser = new Parser();
        parser.Language = spec.GetLanguage();

        using var tree = parser.Parse(content);
        if (tree is null) return EmptyResult;
        var root = tree.RootNode;

        var nodes = new List<GraphNode>();
        var edges = new List<PendingEdge>();
        var unresolvedCalls = new List<UnresolvedCall>();
        var unresolvedImports = new List<UnresolvedImport>();

        GraphNode MakeNode(string qualifiedName, string name, NodeLabel label,
            Dictionary<string, object>? properties = null)
        {
            var relativePath = Path.GetRelativePath(context.RootPath, filePath)
                .Replace('\\', '/');
            return new GraphNode
            {
                Project = context.ProjectName,
                Label = label,
                Name = name,
                QualifiedName = qualifiedName,
                FilePath = relativePath,
                Properties = properties ?? new()
            };
        }

        // Generic extraction: walk the AST for functions, classes, imports
        if (spec.FunctionNodeTypes.Length > 0 || spec.ClassNodeTypes.Length > 0)
        {
            ExtractDefinitions(root, spec, context, filePath, content, nodes, edges, MakeNode);
        }

        if (spec.ImportNodeTypes.Length > 0)
        {
            ExtractImports(root, spec, context, filePath, content, unresolvedImports);
        }

        // Domain-specific extraction hook
        spec.DomainExtractor?.Invoke(new DomainExtractionContext
        {
            RootNode = root,
            Source = content,
            FilePath = filePath,
            Context = context,
            Nodes = nodes,
            Edges = edges,
            UnresolvedCalls = unresolvedCalls,
            UnresolvedImports = unresolvedImports,
            MakeNode = MakeNode
        });

        if (nodes.Count == 0 && edges.Count == 0 &&
            unresolvedCalls.Count == 0 && unresolvedImports.Count == 0)
            return EmptyResult;

        return new ExtractionResult
        {
            Nodes = nodes,
            Edges = edges,
            UnresolvedCalls = unresolvedCalls,
            UnresolvedImports = unresolvedImports,
            Metadata = new ProjectMetadata(spec.LanguageName, spec.Framework)
        };
    }

    private static void ExtractDefinitions(Node root, LanguageSpec spec,
        ExtractorContext context, string filePath, string content,
        List<GraphNode> nodes, List<PendingEdge> edges,
        Func<string, string, NodeLabel, Dictionary<string, object>?, GraphNode> makeNode)
    {
        var fileQN = $"{context.ProjectName}.file.{Path.GetFileNameWithoutExtension(filePath)}";
        WalkForDefinitions(root, spec, context, content,
            nodes, edges, makeNode, fileQN, enclosingClassQN: null);
    }

    private static void WalkForDefinitions(Node node, LanguageSpec spec,
        ExtractorContext context, string content,
        List<GraphNode> nodes, List<PendingEdge> edges,
        Func<string, string, NodeLabel, Dictionary<string, object>?, GraphNode> makeNode,
        string parentQN, string? enclosingClassQN)
    {
        var nodeType = node.Type;

        // Check if this is a class-like definition
        if (spec.ClassNodeTypes.Contains(nodeType))
        {
            var name = GetName(node, spec);
            if (name != null)
            {
                var qn = $"{context.ProjectName}.{name}";
                var props = new Dictionary<string, object> { ["confidence"] = "high" };

                if (spec.SuperclassField != null)
                {
                    var superclass = GetFieldText(node, spec.SuperclassField);
                    if (superclass != null)
                        props["superclass"] = superclass;
                }

                var startLine = (int)node.StartPosition.Row + 1;
                var endLine = (int)node.EndPosition.Row + 1;

                var graphNode = makeNode(qn, name, spec.ClassLabel, props) with
                {
                    StartLine = startLine,
                    EndLine = endLine
                };

                nodes.Add(graphNode);
                edges.Add(new PendingEdge(parentQN, qn, EdgeType.DEFINES));

                // Recurse into the class body with this class as the enclosing context
                var bodyNode = spec.BodyField != null
                    ? node.GetChildForField(spec.BodyField) : null;
                var walkTarget = bodyNode ?? node;

                foreach (var child in walkTarget.Children)
                {
                    WalkForDefinitions(child, spec, context, content,
                        nodes, edges, makeNode, qn, enclosingClassQN: qn);
                }
                return;
            }
        }

        // Check if this is a function-like definition
        if (spec.FunctionNodeTypes.Contains(nodeType))
        {
            var name = GetName(node, spec);
            if (name != null)
            {
                var container = enclosingClassQN ?? context.ProjectName;
                var qn = $"{container}.{name}";
                var label = enclosingClassQN != null ? NodeLabel.Method : spec.FunctionLabel;

                var props = new Dictionary<string, object> { ["confidence"] = "high" };

                if (spec.ReturnTypeField != null)
                {
                    var returnType = GetFieldText(node, spec.ReturnTypeField);
                    if (returnType != null)
                        props["return_type"] = returnType;
                }

                if (spec.ParametersField != null)
                {
                    var parameters = GetFieldText(node, spec.ParametersField);
                    if (parameters != null)
                        props["parameters"] = parameters;
                }

                var startLine = (int)node.StartPosition.Row + 1;
                var endLine = (int)node.EndPosition.Row + 1;

                var graphNode = makeNode(qn, name, label, props) with
                {
                    StartLine = startLine,
                    EndLine = endLine
                };

                nodes.Add(graphNode);
                edges.Add(new PendingEdge(enclosingClassQN ?? parentQN, qn, EdgeType.DEFINES_METHOD));

                return; // Don't recurse into function bodies
            }
        }

        // Not a definition node — recurse into children
        foreach (var child in node.Children)
        {
            WalkForDefinitions(child, spec, context, content,
                nodes, edges, makeNode, parentQN, enclosingClassQN);
        }
    }

    private static void ExtractImports(Node root, LanguageSpec spec,
        ExtractorContext context, string filePath, string content,
        List<UnresolvedImport> unresolvedImports)
    {
        var fileQN = $"{context.ProjectName}.file.{Path.GetFileNameWithoutExtension(filePath)}";
        WalkForImports(root, spec, content, fileQN, unresolvedImports);
    }

    private static void WalkForImports(Node node, LanguageSpec spec, string content,
        string fileQN, List<UnresolvedImport> unresolvedImports)
    {
        if (spec.ImportNodeTypes.Contains(node.Type))
        {
            var modulePath = ExtractImportPath(node, content);
            if (modulePath != null)
            {
                unresolvedImports.Add(new UnresolvedImport(fileQN, modulePath));
            }
            return; // Don't recurse into import nodes
        }

        foreach (var child in node.Children)
        {
            WalkForImports(child, spec, content, fileQN, unresolvedImports);
        }
    }

    private static string? ExtractImportPath(Node importNode, string content)
    {
        // Try common field names for the imported module
        foreach (var field in new[] { "module_name", "source", "path", "name" })
        {
            var child = importNode.GetChildForField(field);
            if (child is not null)
                return child.Text.Trim('"', '\'', '`', '<', '>');
        }

        // Fallback: find the first string literal or dotted name child
        foreach (var child in importNode.Children)
        {
            if (child.Type is "string" or "interpreted_string_literal" or "string_literal"
                or "dotted_name" or "scoped_identifier")
            {
                return child.Text.Trim('"', '\'', '`');
            }
        }

        return null;
    }

    private static string? GetName(Node node, LanguageSpec spec) =>
        spec.NameExtractor?.Invoke(node) ?? GetFieldText(node, spec.NameField);

    private static string? GetFieldText(Node node, string fieldName)
    {
        var child = node.GetChildForField(fieldName);
        if (child is null) return null;
        var text = child.Text;
        return text.Length > 0 ? text : null;
    }

    private static readonly ExtractionResult EmptyResult = new();
}
