using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
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
            return Task.FromResult(ExtractionResult.Failure(ex.Message));
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
        var fileQN = GetFileQualifiedName(context, filePath);
        var knownReceiverTypes = CollectKnownReceiverTypes(root, spec, fileQN);

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

        // Generic extraction: walk definitions and their bodies for owned calls.
        if (spec.FunctionNodeTypes.Length > 0 || spec.ClassNodeTypes.Length > 0
            || spec.CallNodeTypes.Length > 0)
        {
            ExtractDefinitionsAndCalls(root, spec, context, filePath, content,
                nodes, edges, unresolvedCalls, MakeNode, knownReceiverTypes);
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

    private static void ExtractDefinitionsAndCalls(Node root, LanguageSpec spec,
        ExtractorContext context, string filePath, string content,
        List<GraphNode> nodes, List<PendingEdge> edges, List<UnresolvedCall> unresolvedCalls,
        Func<string, string, NodeLabel, Dictionary<string, object>?, GraphNode> makeNode,
        IReadOnlyDictionary<string, string> knownReceiverTypes)
    {
        var fileQN = GetFileQualifiedName(context, filePath);
        WalkForDefinitionsAndCalls(root, spec, context, content,
            nodes, edges, unresolvedCalls, makeNode, fileQN,
            enclosingClassQN: null, enclosingOwnerName: null, enclosingCallableQN: null,
            receiverAliases: EmptyReceiverAliases,
            knownReceiverTypes);
    }

    private static void WalkForDefinitionsAndCalls(Node node, LanguageSpec spec,
        ExtractorContext context, string content,
        List<GraphNode> nodes, List<PendingEdge> edges, List<UnresolvedCall> unresolvedCalls,
        Func<string, string, NodeLabel, Dictionary<string, object>?, GraphNode> makeNode,
        string parentQN, string? enclosingClassQN, string? enclosingOwnerName,
        string? enclosingCallableQN,
        IReadOnlyDictionary<string, string> receiverAliases,
        IReadOnlyDictionary<string, string> knownReceiverTypes)
    {
        var nodeType = node.Type;

        // Check if this is a class-like definition
        if (spec.ClassNodeTypes.Contains(nodeType))
        {
            var name = GetClassName(node, spec);
            if (name != null)
            {
                var container = enclosingClassQN ?? parentQN;
                var props = new Dictionary<string, object> { ["confidence"] = "high" };
                var isContainerOnly = spec.LanguageName == "Rust" && nodeType == "impl_item";
                var ownerQN = isContainerOnly
                    ? ResolveKnownReceiverType(name, knownReceiverTypes)
                    : $"{container}#type:{name}";

                if (spec.SuperclassField != null)
                {
                    var superclass = GetFieldText(node, spec.SuperclassField);
                    if (superclass != null)
                        props["superclass"] = superclass;
                }

                var startLine = (int)node.StartPosition.Row + 1;
                var endLine = (int)node.EndPosition.Row + 1;

                if (!isContainerOnly)
                {
                    var graphNode = makeNode(ownerQN!, name, spec.ClassLabel, props) with
                    {
                        StartLine = startLine,
                        EndLine = endLine
                    };

                    nodes.Add(graphNode);
                    edges.Add(new PendingEdge(parentQN, ownerQN!, EdgeType.DEFINES));
                }

                // Recurse into the class body with this class as the enclosing context
                var bodyNode = spec.BodyField != null
                    ? node.GetChildForField(spec.BodyField) : null;
                var walkTarget = bodyNode ?? node;

                foreach (var child in walkTarget.Children)
                {
                    WalkForDefinitionsAndCalls(child, spec, context, content,
                        nodes, edges, unresolvedCalls, makeNode, ownerQN ?? parentQN,
                        enclosingClassQN: ownerQN, enclosingOwnerName: name,
                        enclosingCallableQN: null,
                        receiverAliases: EmptyReceiverAliases,
                        knownReceiverTypes);
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
                var callableContext = GetCallableContext(
                    node, spec, enclosingClassQN, enclosingOwnerName, knownReceiverTypes);
                var effectiveClassQN = callableContext.ContainerQN;
                var container = effectiveClassQN ?? parentQN;
                var parameters = spec.ParametersField is null
                    ? null
                    : GetFieldText(node, spec.ParametersField);
                var kind = callableContext.OwnerName is null ? "function" : "method";
                var qn = $"{container}#{kind}:{name}{NormalizeSignature(parameters)}";
                var label = callableContext.OwnerName is not null
                    ? NodeLabel.Method
                    : spec.FunctionLabel;

                var props = new Dictionary<string, object> { ["confidence"] = "high" };

                if (spec.ReturnTypeField != null)
                {
                    var returnType = GetFieldText(node, spec.ReturnTypeField);
                    if (returnType != null)
                        props["return_type"] = returnType;
                }

                if (spec.ParametersField != null)
                {
                    if (parameters != null)
                        props["parameters"] = parameters;
                }

                if (callableContext.OwnerName is not null)
                    props["receiver_owner"] = callableContext.OwnerName;

                var startLine = (int)node.StartPosition.Row + 1;
                var endLine = (int)node.EndPosition.Row + 1;

                var graphNode = makeNode(qn, name, label, props) with
                {
                    StartLine = startLine,
                    EndLine = endLine
                };

                nodes.Add(graphNode);
                edges.Add(new PendingEdge(effectiveClassQN ?? parentQN, qn, EdgeType.DEFINES_METHOD));

                // Recurse into the body with this function as the call owner. A
                // nested definition takes ownership of calls in its own body.
                var bodyNode = spec.BodyField != null
                    ? node.GetChildForField(spec.BodyField) : null;
                var walkTarget = bodyNode ?? node;
                foreach (var child in walkTarget.Children)
                {
                    WalkForDefinitionsAndCalls(child, spec, context, content,
                        nodes, edges, unresolvedCalls, makeNode, qn,
                        effectiveClassQN, callableContext.OwnerName, enclosingCallableQN: qn,
                        receiverAliases: callableContext.ReceiverAliases,
                        knownReceiverTypes);
                }
                return;
            }
        }

        if (enclosingCallableQN is not null && spec.CallNodeTypes.Contains(nodeType))
        {
            var target = ExtractCallTarget(
                node, spec, enclosingClassQN, enclosingOwnerName,
                receiverAliases, knownReceiverTypes);
            if (target is not null)
            {
                unresolvedCalls.Add(new UnresolvedCall(
                    enclosingCallableQN,
                    target.CalleeName,
                    target.ReceiverType,
                    target.Confidence,
                    target.ReceiverKind));
            }
        }

        // Recurse through the call expression as well so nested calls are not lost.
        foreach (var child in node.Children)
        {
            WalkForDefinitionsAndCalls(child, spec, context, content,
                nodes, edges, unresolvedCalls, makeNode, parentQN,
                enclosingClassQN, enclosingOwnerName, enclosingCallableQN, receiverAliases,
                knownReceiverTypes);
        }
    }

    private static CallTarget? ExtractCallTarget(
        Node callNode,
        LanguageSpec spec,
        string? enclosingClassQN,
        string? enclosingOwnerName,
        IReadOnlyDictionary<string, string> receiverAliases,
        IReadOnlyDictionary<string, string> knownReceiverTypes)
    {
        Node? targetNode = null;
        foreach (var field in spec.CallTargetFields)
        {
            targetNode = callNode.GetChildForField(field);
            if (targetNode is not null)
                break;
        }

        Node? receiverNode = null;
        foreach (var field in spec.CallReceiverFields)
        {
            receiverNode = callNode.GetChildForField(field);
            if (receiverNode is not null)
                break;
        }

        // Bash command nodes and a few grammar versions expose the command name
        // as their first named child rather than a field.
        targetNode ??= callNode.Children.FirstOrDefault(child =>
            child.Type is "command_name" or "word" or "identifier" or "constant");

        if (targetNode is null || spec.CallNodeTypes.Contains(targetNode.Type))
            return null;

        var nameNode = targetNode.GetChildForField("name")
            ?? targetNode.GetChildForField("field")
            ?? targetNode.GetChildForField("method");
        var calleeName = LastIdentifier((nameNode ?? targetNode).Text);
        if (calleeName is null)
            return null;

        receiverNode ??= targetNode.GetChildForField("object")
            ?? targetNode.GetChildForField("receiver")
            ?? targetNode.GetChildForField("scope")
            ?? targetNode.GetChildForField("operand")
            ?? targetNode.GetChildForField("value")
            ?? targetNode.GetChildForField("path")
            ?? targetNode.GetChildForField("argument");

        var receiver = QualifyReceiver(
            receiverNode?.Text,
            enclosingClassQN,
            enclosingOwnerName,
            receiverAliases,
            knownReceiverTypes,
            ReceiverIsSyntacticScope(callNode, targetNode));
        var confidence = receiverNode is null ? 0.7 : receiver.IsResolved ? 0.65 : 0.45;
        var receiverKind = receiverNode is null
            ? CallReceiverKind.Bare
            : receiver.IsResolved
                ? CallReceiverKind.Resolved
                : CallReceiverKind.Unresolved;
        return new CallTarget(calleeName, receiver.Type, confidence, receiverKind);
    }

    private static ReceiverTarget QualifyReceiver(
        string? receiver,
        string? enclosingClassQN,
        string? enclosingOwnerName,
        IReadOnlyDictionary<string, string> receiverAliases,
        IReadOnlyDictionary<string, string> knownReceiverTypes,
        bool isSyntacticScope)
    {
        if (string.IsNullOrWhiteSpace(receiver))
            return new ReceiverTarget(null, IsResolved: false);

        var trimmed = receiver.Trim();
        if (trimmed is "this" or "self" or "$this")
        {
            var owner = enclosingClassQN ?? enclosingOwnerName;
            return new ReceiverTarget(owner, owner is not null);
        }

        if (receiverAliases.TryGetValue(trimmed.TrimStart('$'), out var receiverType))
            return new ReceiverTarget(receiverType, IsResolved: true);

        // Generic syntax cannot prove that an identifier used with '.' or '->'
        // names a type rather than a field, local, parameter, or import alias.
        // Only grammar-proven static scopes are eligible for owner resolution.
        if (trimmed.StartsWith('$'))
            return new ReceiverTarget(null, IsResolved: false);

        var normalized = trimmed
            .Replace("::", ".", StringComparison.Ordinal)
            .Replace("->", ".", StringComparison.Ordinal)
            .TrimStart('$');
        if (!ReceiverPathRegex.IsMatch(normalized))
            return new ReceiverTarget(null, IsResolved: false);

        if (isSyntacticScope)
            return new ReceiverTarget(
                ResolveKnownReceiverType(normalized, knownReceiverTypes) ?? normalized,
                IsResolved: true);

        return new ReceiverTarget(null, IsResolved: false);
    }

    private static bool ReceiverIsSyntacticScope(Node callNode, Node targetNode) =>
        callNode.Type == "scoped_call_expression"
        || targetNode.Type is "qualified_identifier" or "scoped_identifier";

    private static string? LastIdentifier(string text)
    {
        var withoutGenericSuffix = GenericSuffixRegex.Replace(text.Trim(), string.Empty);
        var matches = IdentifierRegex.Matches(withoutGenericSuffix);
        return matches.Count == 0 ? null : matches[^1].Value.TrimStart('$');
    }

    private static void ExtractImports(Node root, LanguageSpec spec,
        ExtractorContext context, string filePath, string content,
        List<UnresolvedImport> unresolvedImports)
    {
        var fileQN = GetFileQualifiedName(context, filePath);
        WalkForImports(root, spec, content, fileQN, unresolvedImports);
    }

    private static string GetFileQualifiedName(ExtractorContext context, string filePath)
    {
        var relativePath = Path.GetRelativePath(context.RootPath, filePath)
            .Replace('\\', '/')
            .TrimStart('/');
        return $"{context.ProjectName}:{relativePath}";
    }

    private static string NormalizeSignature(string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
            return "()";

        return string.Concat(parameters.Where(character => !char.IsWhiteSpace(character)));
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

    private static string? GetClassName(Node node, LanguageSpec spec) =>
        spec.ClassNameExtractor?.Invoke(node) ?? GetName(node, spec);

    private static IReadOnlyDictionary<string, string> CollectKnownReceiverTypes(
        Node root,
        LanguageSpec spec,
        string fileQN)
    {
        var knownTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguousNames = new HashSet<string>(StringComparer.Ordinal);
        CollectKnownReceiverTypes(root, spec, fileQN, knownTypes, ambiguousNames);
        foreach (var ambiguousName in ambiguousNames)
            knownTypes.Remove(ambiguousName);
        return knownTypes;
    }

    private static void CollectKnownReceiverTypes(
        Node node,
        LanguageSpec spec,
        string parentQN,
        Dictionary<string, string> knownTypes,
        HashSet<string> ambiguousNames)
    {
        if (spec.ClassNodeTypes.Contains(node.Type)
            && !(spec.LanguageName == "Rust" && node.Type == "impl_item"))
        {
            var name = GetClassName(node, spec);
            if (!string.IsNullOrWhiteSpace(name))
            {
                var normalized = name
                    .Replace("::", ".", StringComparison.Ordinal)
                    .Trim('.');
                if (ReceiverPathRegex.IsMatch(normalized))
                {
                    var qualifiedName = $"{parentQN}#type:{name}";
                    AddKnownReceiverType(normalized, qualifiedName, knownTypes, ambiguousNames);
                    AddKnownReceiverType(
                        normalized[(normalized.LastIndexOf('.') + 1)..],
                        qualifiedName,
                        knownTypes,
                        ambiguousNames);

                    var body = spec.BodyField is null
                        ? null
                        : node.GetChildForField(spec.BodyField);
                    foreach (var child in (body ?? node).Children)
                    {
                        CollectKnownReceiverTypes(
                            child, spec, qualifiedName, knownTypes, ambiguousNames);
                    }
                    return;
                }
            }
        }

        foreach (var child in node.Children)
            CollectKnownReceiverTypes(child, spec, parentQN, knownTypes, ambiguousNames);
    }

    private static void AddKnownReceiverType(
        string name,
        string qualifiedName,
        Dictionary<string, string> knownTypes,
        HashSet<string> ambiguousNames)
    {
        if (ambiguousNames.Contains(name))
            return;

        if (knownTypes.TryGetValue(name, out var existing) &&
            !string.Equals(existing, qualifiedName, StringComparison.Ordinal))
        {
            knownTypes.Remove(name);
            ambiguousNames.Add(name);
            return;
        }

        knownTypes[name] = qualifiedName;
    }

    private static string? ResolveKnownReceiverType(
        string receiver,
        IReadOnlyDictionary<string, string> knownReceiverTypes)
    {
        if (knownReceiverTypes.TryGetValue(receiver, out var exact))
            return exact;

        if (receiver.Contains('.', StringComparison.Ordinal))
            return null;

        var simpleName = receiver[(receiver.LastIndexOf('.') + 1)..];
        return knownReceiverTypes.GetValueOrDefault(simpleName);
    }

    private static CallableContext GetCallableContext(
        Node node,
        LanguageSpec spec,
        string? enclosingClassQN,
        string? enclosingOwnerName,
        IReadOnlyDictionary<string, string> knownReceiverTypes)
    {
        var containerQN = enclosingClassQN;
        var ownerName = enclosingOwnerName;
        var receiverAliases = new Dictionary<string, string>(StringComparer.Ordinal);

        if (spec.LanguageName == "Go" && node.Type == "method_declaration")
        {
            var receiver = node.GetChildForField("receiver")?.Text;
            var identifiers = receiver is null
                ? []
                : IdentifierRegex.Matches(receiver)
                    .Select(match => match.Value.TrimStart('$'))
                    .ToArray();
            if (identifiers.Length >= 1)
            {
                var receiverTypeName = identifiers.Length >= 2
                    ? identifiers[1]
                    : identifiers[0];
                ownerName = receiverTypeName;
                containerQN = ResolveKnownReceiverType(receiverTypeName, knownReceiverTypes);
                if (identifiers.Length >= 2)
                    receiverAliases[identifiers[0]] = containerQN ?? receiverTypeName;
            }
        }

        if (containerQN is null && spec.LanguageName == "C++" && node.Type == "function_definition")
        {
            var declarator = node.GetChildForField("declarator")
                ?.GetChildForField("declarator");
            var scope = declarator?.GetChildForField("scope")?.Text;
            if (!string.IsNullOrWhiteSpace(scope))
            {
                var normalizedScope = scope
                    .Replace("::", ".", StringComparison.Ordinal)
                    .Trim('.');
                ownerName = normalizedScope[(normalizedScope.LastIndexOf('.') + 1)..];
                containerQN = ResolveKnownReceiverType(normalizedScope, knownReceiverTypes);
            }
        }

        return new CallableContext(containerQN, ownerName, receiverAliases);
    }

    private static string? GetFieldText(Node node, string fieldName)
    {
        var child = node.GetChildForField(fieldName);
        if (child is null) return null;
        var text = child.Text;
        return text.Length > 0 ? text : null;
    }

    private static readonly ExtractionResult EmptyResult = new();

    private sealed record CallTarget(
        string CalleeName,
        string? ReceiverType,
        double Confidence,
        CallReceiverKind ReceiverKind);

    private sealed record ReceiverTarget(string? Type, bool IsResolved);

    private sealed record CallableContext(
        string? ContainerQN,
        string? OwnerName,
        IReadOnlyDictionary<string, string> ReceiverAliases);

    private static readonly IReadOnlyDictionary<string, string> EmptyReceiverAliases =
        new Dictionary<string, string>();

    private static readonly Regex IdentifierRegex = new(
        @"\$?[A-Za-z_][A-Za-z0-9_!?]*",
        RegexOptions.CultureInvariant);

    private static readonly Regex GenericSuffixRegex = new(
        @"<[^<>]*>$",
        RegexOptions.CultureInvariant);

    private static readonly Regex ReceiverPathRegex = new(
        @"^[A-Za-z_][A-Za-z0-9_!?]*(?:\.[A-Za-z_][A-Za-z0-9_!?]*)*$",
        RegexOptions.CultureInvariant);
}
