using System.Text.Json;
using CodeGraph.Models;

namespace CodeGraph.Extractors.Rust;

public static class ScipJsonImporter
{
    private const int DefinitionRole = 0x1;

    public static ExtractionResult Import(string json, ExtractorContext context)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var symbolInfos = ReadSymbolInfos(root);
        var nodes = new List<GraphNode>();
        var edges = new List<PendingEdge>();
        var knownNodes = new Dictionary<string, DefinitionNode>(StringComparer.Ordinal);
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);

        if (!TryGetProperty(root, "documents", out var documents) ||
            documents.ValueKind is not JsonValueKind.Array)
        {
            return EmptyRustResult;
        }

        foreach (var document in documents.EnumerateArray())
        {
            var language = GetString(document, "language");
            var relativePath = NormalizePath(GetString(document, "relativePath", "relative_path") ?? "");
            if (!IsRustDocument(language, relativePath))
                continue;

            var fileQN = $"{context.ProjectName}:{relativePath}";
            var definitions = new List<DefinitionNode>();

            if (!TryGetProperty(document, "occurrences", out var occurrences) ||
                occurrences.ValueKind is not JsonValueKind.Array)
            {
                continue;
            }

            foreach (var occurrence in occurrences.EnumerateArray())
            {
                var symbol = GetString(occurrence, "symbol");
                if (string.IsNullOrWhiteSpace(symbol))
                    continue;

                var roles = GetInt32(occurrence, "symbolRoles", "symbol_roles") ?? 0;
                if ((roles & DefinitionRole) == 0)
                    continue;

                if (!TryReadSymbolRange(occurrence, out var symbolRange))
                    continue;

                symbolInfos.TryGetValue(symbol, out var info);
                var label = ToNodeLabel(info?.Kind);
                if (label is null)
                    continue;

                var qualifiedName = ToQualifiedName(context.ProjectName, symbol);
                if (knownNodes.ContainsKey(qualifiedName))
                    continue;

                var name = info?.DisplayName;
                if (string.IsNullOrWhiteSpace(name))
                    name = DeriveDisplayName(symbol);

                var node = new GraphNode
                {
                    Project = context.ProjectName,
                    Label = label.Value,
                    Name = name,
                    QualifiedName = qualifiedName,
                    FilePath = relativePath,
                    StartLine = symbolRange.StartLine + 1,
                    EndLine = Math.Max(symbolRange.EndLine + 1, symbolRange.StartLine + 1),
                    Properties = new Dictionary<string, object>
                    {
                        ["source"] = "scip",
                        ["scip_symbol"] = symbol,
                        ["confidence"] = "high"
                    }
                };

                if (!string.IsNullOrWhiteSpace(info?.Kind))
                    node.Properties["scip_kind"] = info.Kind;
                if (!string.IsNullOrWhiteSpace(info?.Documentation))
                    node.Properties["documentation"] = info.Documentation;
                if (!string.IsNullOrWhiteSpace(info?.Signature))
                    node.Properties["signature"] = info.Signature;

                nodes.Add(node);

                var enclosingRange = TryReadEnclosingRange(occurrence, out var range)
                    ? range
                    : symbolRange;
                var definition = new DefinitionNode(node, enclosingRange);
                definitions.Add(definition);
                knownNodes[qualifiedName] = definition;

                AddEdge(edges, edgeKeys, new PendingEdge(
                    fileQN,
                    qualifiedName,
                    label.Value is NodeLabel.Method or NodeLabel.Constructor
                        ? EdgeType.DEFINES_METHOD
                        : EdgeType.DEFINES,
                    new() { ["source"] = "scip" }));
            }

            foreach (var occurrence in occurrences.EnumerateArray())
            {
                var symbol = GetString(occurrence, "symbol");
                if (string.IsNullOrWhiteSpace(symbol))
                    continue;

                var roles = GetInt32(occurrence, "symbolRoles", "symbol_roles") ?? 0;
                if ((roles & DefinitionRole) != 0)
                    continue;

                var targetQN = ToQualifiedName(context.ProjectName, symbol);
                if (!knownNodes.TryGetValue(targetQN, out var target))
                    continue;
                if (!TryReadSymbolRange(occurrence, out var occurrenceRange))
                    continue;

                var source = FindInnermostDefinition(definitions, occurrenceRange)
                    ?? new DefinitionNode(new GraphNode
                    {
                        Project = context.ProjectName,
                        Label = NodeLabel.File,
                        Name = Path.GetFileName(relativePath),
                        QualifiedName = fileQN,
                        FilePath = relativePath
                    }, SourceRange.Empty);

                if (source.Node.QualifiedName == targetQN)
                    continue;

                AddEdge(edges, edgeKeys, new PendingEdge(
                    source.Node.QualifiedName,
                    targetQN,
                    ReferenceEdgeType(target.Node.Label),
                    new()
                    {
                        ["source"] = "scip",
                        ["confidence"] = 0.85,
                        ["symbol_roles"] = roles
                    }));
            }
        }

        foreach (var (symbol, info) in symbolInfos)
        {
            var sourceQN = ToQualifiedName(context.ProjectName, symbol);
            if (!knownNodes.ContainsKey(sourceQN))
                continue;

            foreach (var relationship in info.Relationships)
            {
                if (!relationship.IsImplementation)
                    continue;

                var targetQN = ToQualifiedName(context.ProjectName, relationship.Symbol);
                if (!knownNodes.ContainsKey(targetQN))
                    continue;

                AddEdge(edges, edgeKeys, new PendingEdge(
                    sourceQN,
                    targetQN,
                    EdgeType.IMPLEMENTS,
                    new() { ["source"] = "scip" }));
            }
        }

        return new ExtractionResult
        {
            Nodes = nodes,
            Edges = edges,
            Metadata = new ProjectMetadata("Rust", "Cargo")
        };
    }

    private static ExtractionResult EmptyRustResult => new()
    {
        Metadata = new ProjectMetadata("Rust", "Cargo")
    };

    private static Dictionary<string, SymbolInfo> ReadSymbolInfos(JsonElement root)
    {
        var result = new Dictionary<string, SymbolInfo>(StringComparer.Ordinal);
        if (TryGetProperty(root, "externalSymbols", out var externalSymbols) &&
            externalSymbols.ValueKind is JsonValueKind.Array)
        {
            foreach (var symbol in externalSymbols.EnumerateArray())
                AddSymbolInfo(result, symbol);
        }

        if (!TryGetProperty(root, "documents", out var documents) ||
            documents.ValueKind is not JsonValueKind.Array)
        {
            return result;
        }

        foreach (var document in documents.EnumerateArray())
        {
            if (!TryGetProperty(document, "symbols", out var symbols) ||
                symbols.ValueKind is not JsonValueKind.Array)
            {
                continue;
            }

            foreach (var symbol in symbols.EnumerateArray())
                AddSymbolInfo(result, symbol);
        }

        return result;
    }

    private static void AddSymbolInfo(Dictionary<string, SymbolInfo> infos, JsonElement element)
    {
        var symbol = GetString(element, "symbol");
        if (string.IsNullOrWhiteSpace(symbol))
            return;

        var relationships = new List<RelationshipInfo>();
        if (TryGetProperty(element, "relationships", out var relationshipElements) &&
            relationshipElements.ValueKind is JsonValueKind.Array)
        {
            foreach (var relationship in relationshipElements.EnumerateArray())
            {
                var relatedSymbol = GetString(relationship, "symbol");
                if (string.IsNullOrWhiteSpace(relatedSymbol))
                    continue;

                relationships.Add(new RelationshipInfo(
                    relatedSymbol,
                    GetBool(relationship, "isImplementation", "is_implementation")));
            }
        }

        infos[symbol] = new SymbolInfo(
            GetString(element, "displayName", "display_name"),
            GetEnumString(element, "kind"),
            GetJoinedStringArray(element, "documentation"),
            ReadSignature(element),
            relationships);
    }

    private static string? ReadSignature(JsonElement element)
    {
        if (!TryGetProperty(element, "signatureDocumentation", out var signature) &&
            !TryGetProperty(element, "signature_documentation", out signature))
        {
            return null;
        }

        return GetString(signature, "text");
    }

    private static NodeLabel? ToNodeLabel(string? kind)
    {
        return kind switch
        {
            "Function" => NodeLabel.Function,
            "Method" or "StaticMethod" or "TraitMethod" or "AbstractMethod" => NodeLabel.Method,
            "Constructor" => NodeLabel.Constructor,
            "Struct" => NodeLabel.Struct,
            "Enum" => NodeLabel.Enum,
            "Trait" or "Interface" or "Protocol" => NodeLabel.Interface,
            "Module" or "Namespace" => NodeLabel.Namespace,
            "Class" or "Type" or "TypeAlias" or "Union" => NodeLabel.Class,
            "Field" or "Property" or "StaticField" or "StaticProperty" or "Constant" => NodeLabel.Property,
            _ => null
        };
    }

    private static EdgeType ReferenceEdgeType(NodeLabel targetLabel) =>
        targetLabel is NodeLabel.Class or NodeLabel.Struct or NodeLabel.Enum or NodeLabel.Interface
            ? EdgeType.USES_TYPE
            : EdgeType.CALLS;

    private static DefinitionNode? FindInnermostDefinition(
        IReadOnlyList<DefinitionNode> definitions,
        SourceRange occurrence)
    {
        return definitions
            .Where(d => d.Range.Contains(occurrence))
            .OrderBy(d => d.Range.LineSpan)
            .ThenBy(d => d.Range.CharacterSpan)
            .FirstOrDefault();
    }

    private static bool TryReadSymbolRange(JsonElement occurrence, out SourceRange range)
    {
        if (TryReadRange(occurrence, "singleLineRange", "single_line_range", out range) ||
            TryReadRange(occurrence, "multiLineRange", "multi_line_range", out range))
        {
            return true;
        }

        if (!TryGetProperty(occurrence, "range", out var packed) ||
            packed.ValueKind is not JsonValueKind.Array)
        {
            range = SourceRange.Empty;
            return false;
        }

        var values = packed.EnumerateArray()
            .Select(e => e.ValueKind is JsonValueKind.Number && e.TryGetInt32(out var value) ? value : -1)
            .ToArray();

        range = values.Length switch
        {
            3 => new SourceRange(values[0], values[1], values[0], values[2]),
            4 => new SourceRange(values[0], values[1], values[2], values[3]),
            _ => SourceRange.Empty
        };

        return values.Length is 3 or 4 && values.All(v => v >= 0);
    }

    private static bool TryReadEnclosingRange(JsonElement occurrence, out SourceRange range) =>
        TryReadRange(occurrence, "singleLineEnclosingRange", "single_line_enclosing_range", out range) ||
        TryReadRange(occurrence, "multiLineEnclosingRange", "multi_line_enclosing_range", out range);

    private static bool TryReadRange(
        JsonElement parent,
        string camelName,
        string snakeName,
        out SourceRange range)
    {
        if (!TryGetProperty(parent, camelName, out var element) &&
            !TryGetProperty(parent, snakeName, out element))
        {
            range = SourceRange.Empty;
            return false;
        }

        var startLine = GetInt32(element, "startLine", "start_line");
        var startCharacter = GetInt32(element, "startCharacter", "start_character");
        var endLine = GetInt32(element, "endLine", "end_line") ?? startLine;
        var endCharacter = GetInt32(element, "endCharacter", "end_character");

        if (startLine is null || startCharacter is null || endLine is null || endCharacter is null)
        {
            range = SourceRange.Empty;
            return false;
        }

        range = new SourceRange(startLine.Value, startCharacter.Value, endLine.Value, endCharacter.Value);
        return true;
    }

    private static void AddEdge(
        List<PendingEdge> edges,
        HashSet<string> edgeKeys,
        PendingEdge edge)
    {
        var key = $"{edge.SourceQN}\n{edge.TargetQN}\n{edge.Type}";
        if (edgeKeys.Add(key))
            edges.Add(edge);
    }

    private static string ToQualifiedName(string projectName, string symbol) =>
        $"{projectName}:scip:{symbol}";

    private static string DeriveDisplayName(string symbol)
    {
        var trimmed = symbol.TrimEnd('.', '#', ')', ':', '/');
        var index = trimmed.LastIndexOfAny([' ', '.', '#', '(', ':', '/']);
        return index >= 0 && index + 1 < trimmed.Length
            ? trimmed[(index + 1)..]
            : trimmed;
    }

    private static bool IsRustDocument(string? language, string relativePath) =>
        string.Equals(language, "rust", StringComparison.OrdinalIgnoreCase) ||
        relativePath.EndsWith(".rs", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind is JsonValueKind.Object && element.TryGetProperty(name, out value))
            return true;

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
                continue;

            if (value.ValueKind is JsonValueKind.String)
                return value.GetString();
            if (value.ValueKind is JsonValueKind.Number)
                return value.GetRawText();
        }

        return null;
    }

    private static string? GetEnumString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt32(out var kind) => kind switch
            {
                7 => "Class",
                8 => "Constant",
                9 => "Constructor",
                11 => "Enum",
                15 => "Field",
                17 => "Function",
                21 => "Interface",
                26 => "Method",
                29 => "Module",
                30 => "Namespace",
                41 => "Property",
                49 => "Struct",
                53 => "Trait",
                54 => "Type",
                55 => "TypeAlias",
                59 => "Union",
                70 => "TraitMethod",
                80 => "StaticMethod",
                _ => null
            },
            _ => null
        };
    }

    private static int? GetInt32(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
                continue;

            if (value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var result))
                return result;
            if (value.ValueKind is JsonValueKind.String && int.TryParse(value.GetString(), out result))
                return result;
        }

        return null;
    }

    private static bool GetBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
                continue;

            if (value.ValueKind is JsonValueKind.True)
                return true;
            if (value.ValueKind is JsonValueKind.False)
                return false;
        }

        return false;
    }

    private static string? GetJoinedStringArray(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) ||
            value.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        var parts = value.EnumerateArray()
            .Where(e => e.ValueKind is JsonValueKind.String)
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        return parts.Length == 0 ? null : string.Join("\n", parts);
    }

    private sealed record SymbolInfo(
        string? DisplayName,
        string? Kind,
        string? Documentation,
        string? Signature,
        IReadOnlyList<RelationshipInfo> Relationships);

    private sealed record RelationshipInfo(string Symbol, bool IsImplementation);

    private sealed record DefinitionNode(GraphNode Node, SourceRange Range);

    private readonly record struct SourceRange(
        int StartLine,
        int StartCharacter,
        int EndLine,
        int EndCharacter)
    {
        public static SourceRange Empty => new(0, 0, 0, 0);
        public int LineSpan => EndLine - StartLine;
        public int CharacterSpan => EndCharacter - StartCharacter;

        public bool Contains(SourceRange other)
        {
            return StartsBeforeOrAt(StartLine, StartCharacter, other.StartLine, other.StartCharacter) &&
                   StartsBeforeOrAt(other.EndLine, other.EndCharacter, EndLine, EndCharacter);
        }

        private static bool StartsBeforeOrAt(int leftLine, int leftCharacter, int rightLine, int rightCharacter) =>
            leftLine < rightLine || (leftLine == rightLine && leftCharacter <= rightCharacter);
    }
}
