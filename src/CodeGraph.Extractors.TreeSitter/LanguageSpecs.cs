using CodeGraph.Models;
using CodeGraph.Services.Pipeline;
using TreeSitter;

namespace CodeGraph.Extractors.TreeSitter;

/// <summary>
/// Registry of all tree-sitter language specs, keyed by file extension.
/// Add new languages by defining a spec and mapping its extensions.
/// </summary>
public static partial class LanguageSpecs
{
    private static readonly Dictionary<string, LanguageSpec> ByExtension = new(StringComparer.OrdinalIgnoreCase);

    static LanguageSpecs()
    {
        Register(C, ".c", ".h");
        Register(Cpp, ".cpp", ".cc", ".cxx", ".hpp", ".hxx", ".hh");
        Register(Python, ".py", ".pyw");
        Register(Go, ".go");
        Register(Java, ".java");
        Register(Ruby, ".rb");
        Register(Rust, ".rs");
        Register(Php, ".php");
        Register(Bash, ".sh", ".bash", ".zsh");
        Register(Swift, ".swift");
    }

    public static LanguageSpec? ForExtension(string extension) =>
        ByExtension.GetValueOrDefault(extension);

    public static IReadOnlyCollection<string> SupportedExtensions => ByExtension.Keys;

    private static void Register(LanguageSpec spec, params string[] extensions)
    {
        foreach (var ext in extensions)
            ByExtension[ext] = spec;
    }

    // ── C ───────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the function/type name from a C node.
    /// C nests function names at declarator.declarator (function_declarator → identifier).
    /// Typedef names are at the declarator field directly.
    /// Struct/enum names are at the name field.
    /// </summary>
    private static string? ExtractCName(Node node)
    {
        // function_definition: name is at declarator.declarator
        if (node.Type == "function_definition")
        {
            var funcDecl = node.GetChildForField("declarator"); // function_declarator
            if (funcDecl is null) return null;
            var ident = funcDecl.GetChildForField("declarator"); // identifier
            return ident?.Text;
        }

        // type_definition (typedef struct { ... } name_t): name is at declarator
        if (node.Type == "type_definition")
            return node.GetChildForField("declarator")?.Text;

        // struct_specifier / enum_specifier: name field
        return node.GetChildForField("name")?.Text;
    }

    public static readonly LanguageSpec C = new()
    {
        LanguageName = "C",
        Framework = null,
        GetLanguage = () => new Language("C"),
        FunctionNodeTypes = ["function_definition"],
        ClassNodeTypes = ["struct_specifier", "enum_specifier", "type_definition"],
        CallNodeTypes = ["call_expression"],
        ImportNodeTypes = ["preproc_include"],
        VariableNodeTypes = ["declaration"],
        NameExtractor = ExtractCName,
        ReturnTypeField = "type",
        ParametersField = null, // parameters are nested in declarator
        BodyField = "body",
        FunctionLabel = NodeLabel.Function,
        ClassLabel = NodeLabel.Struct
    };

    // ── C++ ─────────────────────────────────────────────────────────

    /// <summary>
    /// C++ name extraction — same as C for functions, plus class_specifier/namespace.
    /// </summary>
    private static string? ExtractCppName(Node node)
    {
        if (node.Type is "function_definition")
        {
            var funcDecl = node.GetChildForField("declarator");
            if (funcDecl is null) return null;
            var ident = funcDecl.GetChildForField("declarator");
            return ident?.GetChildForField("name")?.Text ?? ident?.Text;
        }

        if (node.Type == "type_definition")
            return node.GetChildForField("declarator")?.Text;

        // class_specifier, struct_specifier, enum_specifier, namespace_definition
        return node.GetChildForField("name")?.Text;
    }

    public static readonly LanguageSpec Cpp = new()
    {
        LanguageName = "C++",
        Framework = null,
        GetLanguage = () => new Language("Cpp"),
        // 2: functions with C language linkage (extern "C") record abi C (sc-26145).
        ExtractionVersion = 2,
        CallableAbiExtractor = GetCppLinkage,
        FunctionNodeTypes = ["function_definition"],
        ClassNodeTypes = ["class_specifier", "struct_specifier", "enum_specifier",
                          "type_definition", "namespace_definition"],
        CallNodeTypes = ["call_expression"],
        ImportNodeTypes = ["preproc_include"],
        VariableNodeTypes = ["declaration"],
        NameExtractor = ExtractCppName,
        ReturnTypeField = "type",
        ParametersField = null,
        BodyField = "body",
        FunctionLabel = NodeLabel.Function,
        ClassLabel = NodeLabel.Class
    };

    // ── Python ──────────────────────────────────────────────────────

    public static readonly LanguageSpec Python = new()
    {
        LanguageName = "Python",
        Framework = null,
        GetLanguage = () => new Language("Python"),
        FunctionNodeTypes = ["function_definition"],
        ClassNodeTypes = ["class_definition"],
        CallNodeTypes = ["call"],
        ImportNodeTypes = ["import_statement", "import_from_statement"],
        VariableNodeTypes = ["assignment"],
        NameField = "name",
        ReturnTypeField = "return_type",
        ParametersField = "parameters",
        BodyField = "body",
        SuperclassField = "superclasses",
        FunctionLabel = NodeLabel.Function,
        ClassLabel = NodeLabel.Class
    };

    // ── Go ──────────────────────────────────────────────────────────

    public static readonly LanguageSpec Go = new()
    {
        LanguageName = "Go",
        ExtractionVersion = 2, // Gin registration pass (sc-27974).
        Framework = null,
        GetLanguage = () => new Language("Go"),
        FunctionNodeTypes = ["function_declaration", "method_declaration"],
        ClassNodeTypes = ["type_declaration"],
        CallNodeTypes = ["call_expression"],
        ImportNodeTypes = ["import_declaration"],
        VariableNodeTypes = ["short_var_declaration", "var_declaration"],
        ClassNameExtractor = node => node.NamedChildren
            .Select(child => child.GetChildForField("name"))
            .FirstOrDefault(child => child is not null)?.Text,
        NameField = "name",
        ReturnTypeField = "result",
        ParametersField = "parameters",
        BodyField = "body",
        FunctionLabel = NodeLabel.Function,
        ClassLabel = NodeLabel.Class
    };

    // ── Java ────────────────────────────────────────────────────────

    public static readonly LanguageSpec Java = new()
    {
        LanguageName = "Java",
        Framework = null,
        GetLanguage = () => new Language("Java"),
        FunctionNodeTypes = ["method_declaration", "constructor_declaration"],
        ClassNodeTypes = ["class_declaration", "interface_declaration", "enum_declaration"],
        CallNodeTypes = ["method_invocation"],
        CallTargetFields = ["name"],
        CallReceiverFields = ["object"],
        ImportNodeTypes = ["import_declaration"],
        VariableNodeTypes = ["local_variable_declaration", "field_declaration"],
        NameField = "name",
        ReturnTypeField = "type",
        ParametersField = "parameters",
        BodyField = "body",
        SuperclassField = "superclass",
        FunctionLabel = NodeLabel.Method,
        ClassLabel = NodeLabel.Class
    };

    // ── Ruby ────────────────────────────────────────────────────────

    public static readonly LanguageSpec Ruby = new()
    {
        LanguageName = "Ruby",
        Framework = null,
        GetLanguage = () => new Language("Ruby"),
        FunctionNodeTypes = ["method", "singleton_method"],
        ClassNodeTypes = ["class", "module"],
        CallNodeTypes = ["call", "method_call"],
        CallTargetFields = ["method", "name"],
        CallReceiverFields = ["receiver", "object"],
        ImportNodeTypes = [],
        VariableNodeTypes = ["assignment"],
        NameField = "name",
        ParametersField = "parameters",
        BodyField = "body",
        SuperclassField = "superclass",
        FunctionLabel = NodeLabel.Method,
        ClassLabel = NodeLabel.Class
    };

    // ── Rust ────────────────────────────────────────────────────────

    public static readonly LanguageSpec Rust = new()
    {
        LanguageName = "Rust",
        Framework = null,
        GetLanguage = () => new Language("Rust"),
        // 2: functions record their foreign ABI (`extern "C" fn` → abi C).
        // 3: only when exported by symbol name (#[no_mangle] / #[export_name]) (sc-26145).
        ExtractionVersion = 4, // Axum registration pass (sc-27974), including semantic indexing.
        CallableAbiExtractor = GetRustAbi,
        FunctionNodeTypes = ["function_item"],
        ClassNodeTypes = ["struct_item", "enum_item", "trait_item", "impl_item"],
        IsContainerOnlyClass = node => node.Type == "impl_item",
        CallNodeTypes = ["call_expression"],
        ImportNodeTypes = ["use_declaration"],
        VariableNodeTypes = ["let_declaration", "const_item", "static_item"],
        ClassNameExtractor = node => node.Type == "impl_item"
            ? GetRustNominalTypeName(node.GetChildForField("type"))
            : node.GetChildForField("name")?.Text,
        NameField = "name",
        ReturnTypeField = "return_type",
        ParametersField = "parameters",
        BodyField = "body",
        FunctionLabel = NodeLabel.Function,
        ClassLabel = NodeLabel.Class
    };

    /// <summary>
    /// The ABI of an <c>extern "abi" fn</c> (bare <c>extern fn</c> is C) that is exported under its
    /// own symbol name (<c>#[no_mangle]</c> or <c>#[export_name]</c>, also inside <c>unsafe(...)</c>),
    /// so foreign code can link to it by name. Null for a Rust-ABI function and for an
    /// <c>extern "C"</c> function whose symbol stays mangled (a callback passed by pointer, such as
    /// a nested test helper): no C or Swift call reaches it by name.
    /// </summary>
    private static string? GetRustAbi(Node node)
    {
        var externModifier = node.NamedChildren
            .Where(child => child.Type == "function_modifiers")
            .SelectMany(modifiers => modifiers.NamedChildren)
            .FirstOrDefault(child => child.Type == "extern_modifier");
        if (externModifier is null || !IsExportedBySymbolName(node))
            return null;

        var abi = externModifier.NamedChildren.FirstOrDefault(child => child.Type == "string_literal")
            ?.Text.Trim('"');
        return string.IsNullOrEmpty(abi) ? "C" : abi == "Rust" ? null : abi;
    }

    /// <summary>
    /// <c>C</c> for a C++ function with C language linkage (<c>extern "C" int f()</c> or a
    /// definition inside an <c>extern "C" { ... }</c> block), whose symbol name is not mangled, so
    /// Rust can link to it by name; null for C++ linkage.
    /// </summary>
    private static string? GetCppLinkage(Node node)
    {
        var parent = node.Parent;
        if (parent?.Type == "declaration_list")
            parent = parent.Parent;
        if (parent?.Type != "linkage_specification")
            return null;

        var linkage = (parent.GetChildForField("value")
                ?? parent.NamedChildren.FirstOrDefault(child => child.Type == "string_literal"))
            ?.Text.Trim('"');
        return linkage == "C" ? "C" : null;
    }

    /// <summary>Whether the attributes directly above a Rust item keep its symbol name unmangled.</summary>
    private static bool IsExportedBySymbolName(Node item)
    {
        if (item.Parent is not { } parent)
            return false;

        var siblings = parent.Children.ToList();
        var index = siblings.FindIndex(sibling =>
            sibling.StartIndex == item.StartIndex && sibling.EndIndex == item.EndIndex && sibling.Type == item.Type);
        for (var previous = index - 1; previous >= 0; previous--)
        {
            var sibling = siblings[previous];
            if (sibling.Type is "line_comment" or "block_comment")
                continue;
            if (sibling.Type != "attribute_item")
                break;
            if (SymbolExportAttribute().IsMatch(sibling.Text))
                return true;
        }

        return false;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(no_mangle|export_name)\b")]
    private static partial System.Text.RegularExpressions.Regex SymbolExportAttribute();

    private static string? GetRustNominalTypeName(Node? node)
    {
        if (node is null)
            return null;

        if (node.Type == "generic_type")
        {
            var genericOwner = node.GetChildForField("type")
                ?? node.Children.FirstOrDefault(child => child.Type != "type_arguments");
            return GetRustNominalTypeName(genericOwner);
        }

        if (node.Type == "scoped_type_identifier")
        {
            var name = GetRustNominalTypeName(node.GetChildForField("name"));
            return string.IsNullOrWhiteSpace(name)
                ? GetRustNominalTypeName(node.GetChildForField("path"))
                : name;
        }

        if (node.Type is "type_identifier" or "identifier")
            return node.Text;

        foreach (var child in node.Children)
        {
            var name = GetRustNominalTypeName(child);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return null;
    }

    // ── PHP ─────────────────────────────────────────────────────────

    public static readonly LanguageSpec Php = new()
    {
        LanguageName = "PHP",
        Framework = null,
        GetLanguage = () => new Language("Php"),
        FunctionNodeTypes = ["function_definition", "method_declaration"],
        ClassNodeTypes = ["class_declaration", "interface_declaration", "trait_declaration"],
        CallNodeTypes = ["function_call_expression", "member_call_expression", "scoped_call_expression"],
        CallTargetFields = ["function", "name"],
        CallReceiverFields = ["object", "scope"],
        ImportNodeTypes = ["namespace_use_declaration"],
        VariableNodeTypes = ["property_declaration"],
        NameField = "name",
        ReturnTypeField = "return_type",
        ParametersField = "parameters",
        BodyField = "body",
        FunctionLabel = NodeLabel.Function,
        ClassLabel = NodeLabel.Class
    };

    // ── Bash ────────────────────────────────────────────────────────

    public static readonly LanguageSpec Bash = new()
    {
        LanguageName = "Bash",
        Framework = null,
        GetLanguage = () => new Language("Bash"),
        FunctionNodeTypes = ["function_definition"],
        ClassNodeTypes = [],
        CallNodeTypes = ["command"],
        CallTargetFields = ["name", "command"],
        ImportNodeTypes = [],
        VariableNodeTypes = ["variable_assignment"],
        NameField = "name",
        BodyField = "body",
        FunctionLabel = NodeLabel.Function
    };

    // ── Swift ───────────────────────────────────────────────────────

    // Grammar: alex-pinkus/tree-sitter-swift, built by native/build-tree-sitter-swift.sh.
    // class_declaration covers class/struct/enum/actor/extension (told apart by its
    // declaration_kind field); protocols have their own protocol_declaration node.

    public static readonly LanguageSpec Swift = new()
    {
        LanguageName = "Swift",
        Framework = null,
        GetLanguage = () => new Language("Swift"),
        // 2: @_exported imports are marked as re-exports (sc-26145).
        // 3: every node is marked as a tree-sitter fallback node (sc-26135).
        ExtractionVersion = 3,
        // No Swift semantic tier exists: tree-sitter output is fallback output.
        TreeSitterFallbackReason = GraphNodeConfidence.SwiftSemanticUnavailableReason,
        FunctionNodeTypes = ["function_declaration", "protocol_function_declaration",
                             "init_declaration", "deinit_declaration", "subscript_declaration"],
        ClassNodeTypes = ["class_declaration", "protocol_declaration"],
        ClassNameExtractor = GetSwiftTypeName,
        ClassKindExtractor = GetSwiftDeclarationKind,
        ClassLabelSelector = node => GetSwiftDeclarationKind(node) switch
        {
            "protocol" => NodeLabel.Interface,
            "struct" => NodeLabel.Struct,
            "enum" => NodeLabel.Enum,
            _ => NodeLabel.Class
        },
        // An extension adds members to a type declared elsewhere; it never emits a type node.
        IsContainerOnlyClass = node => GetSwiftDeclarationKind(node) == "extension",
        // Members of an extension of a type declared in another file attach to that type once
        // the pipeline resolves it (SwiftTypeReference).
        ContainerOwnerReference = node =>
            GetSwiftDeclarationKind(node) == "extension"
            && SwiftCallSites.TypePath(node.GetChildForField("name")) is { } extendedPath
                ? new ContainerOwner(extendedPath, SwiftTypeReference.Create(extendedPath))
                : null,
        TypeRelationExtractor = AddSwiftTypeRelations,
        PropertyNodeTypes = ["property_declaration", "protocol_property_declaration"],
        PropertyExtractor = GetSwiftProperty,
        ParametersExtractor = GetSwiftParameters,
        ArgumentLabelsExtractor = SwiftCallSites.DeclarationLabels,
        CallNodeTypes = ["call_expression", "constructor_expression"],
        CallSiteReader = SwiftCallSites.For,
        ImportNodeTypes = ["import_declaration"],
        ImportReader = SwiftCallSites.ReadImport,
        NameExtractor = GetSwiftCallableName,
        NameField = "name",
        ReturnTypeField = "return_type",
        BodyField = "body",
        FunctionLabel = NodeLabel.Function,
        ClassLabel = NodeLabel.Class,
        SkipFileOnExtractionFailure = true
    };

    /// <summary>
    /// Initializers, deinitializers and subscripts are named by their keyword. Their "name" field
    /// is not usable: deinit has none, and on a subscript the grammar tags the element type
    /// with it.
    /// </summary>
    private static string? GetSwiftCallableName(Node node) => node.Type switch
    {
        "init_declaration" => "init",
        "deinit_declaration" => "deinit",
        "subscript_declaration" => "subscript",
        _ => node.GetChildForField("name")?.Text
    };

    private static string? GetSwiftDeclarationKind(Node node) =>
        node.GetChildForField("declaration_kind")?.Text;

    /// <summary>
    /// The declared type's name. An extension names an existing type, possibly qualified
    /// (<c>Outer.Inner</c>) or specialized (<c>Array&lt;Int&gt;</c>); its owner is the last
    /// simple type identifier.
    /// </summary>
    private static string? GetSwiftTypeName(Node node)
    {
        var name = node.GetChildForField("name");
        if (name is null)
            return null;

        if (GetSwiftDeclarationKind(node) != "extension" || name.Type == "type_identifier")
            return name.Text;

        return name.Children.LastOrDefault(child => child.Type == "type_identifier")?.Text
            ?? name.Text;
    }

    /// <summary>
    /// Swift parameters are direct children of the declaration rather than a grouped list. A
    /// Swift function's identity is its argument labels and parameter types, not its internal
    /// parameter names: <c>thumbnail(of image: CGImage)</c> → <c>(of:CGImage)</c>,
    /// <c>f(_ x: Int)</c> → <c>(_:Int)</c>.
    /// </summary>
    private static string GetSwiftParameters(Node node) => SwiftCallSites.DeclarationParameters(node);

    // Standard-library and Apple-framework protocols that commonly come first in a class's
    // inheritance clause. A first entry that is not declared in the repository is a protocol
    // when it is listed here or carries a protocol naming convention, else a superclass.
    private static readonly HashSet<string> WellKnownSwiftProtocols = new(StringComparer.Ordinal)
    {
        "Actor", "AnyActor", "App", "AsyncIteratorProtocol", "AsyncSequence", "BidirectionalCollection",
        "CaseIterable", "Codable", "Collection", "Comparable", "CustomDebugStringConvertible",
        "CustomStringConvertible", "Decodable", "Encodable", "Equatable", "Error",
        "ExpressibleByArrayLiteral", "ExpressibleByDictionaryLiteral", "ExpressibleByIntegerLiteral",
        "ExpressibleByStringLiteral", "Hashable", "Identifiable", "IteratorProtocol", "LocalizedError",
        "MutableCollection", "NSCoding", "NSCopying", "NSSecureCoding", "ObservableObject",
        "OptionSet", "PreviewProvider", "RandomAccessCollection", "RawRepresentable",
        "RangeReplaceableCollection", "Scene", "Sendable", "Sequence", "SetAlgebra", "Strideable",
        "View", "ViewModifier"
    };

    private static readonly string[] SwiftProtocolNameSuffixes = ["Protocol", "Delegate", "DataSource"];

    // Raw-value types an enum's first inheritance entry may name; they are not protocols.
    private static readonly HashSet<string> SwiftEnumRawValueTypes = new(StringComparer.Ordinal)
    {
        "String", "Character", "Int", "Int8", "Int16", "Int32", "Int64", "UInt", "UInt8", "UInt16",
        "UInt32", "UInt64", "Float", "Float16", "Float80", "Double", "CGFloat"
    };

    // Framework classes whose names carry a protocol naming convention.
    private static readonly HashSet<string> WellKnownSwiftClasses = new(StringComparer.Ordinal)
    {
        "UITableViewDiffableDataSource", "UICollectionViewDiffableDataSource",
        "NSTableViewDiffableDataSource", "NSCollectionViewDiffableDataSource",
        "UITableViewDiffableDataSourceReference", "UICollectionViewDiffableDataSourceReference",
        "NSCollectionViewDiffableDataSourceReference"
    };

    private static bool LooksLikeSwiftProtocol(string typePath)
    {
        var simpleName = SwiftTypeReference.SimpleName(typePath);
        if (WellKnownSwiftClasses.Contains(simpleName))
            return false;
        return WellKnownSwiftProtocols.Contains(simpleName)
            || SwiftProtocolNameSuffixes.Any(suffix =>
                simpleName.Length > suffix.Length
                && simpleName.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Inheritance and conformance edges from a Swift type's inheritance clause. Swift syntax does
    /// not say which entry is a superclass, so:
    /// <list type="bullet">
    /// <item>struct, enum, actor and extension entries are protocols: IMPLEMENTS. An enum's first
    /// entry naming a raw-value type (String, Int, ...) is recorded as <c>raw_type</c> instead.</item>
    /// <item>protocol entries are inherited protocols: INHERITS, as for interface-extends-interface
    /// in C#. <c>AnyObject</c> marks a class-bound protocol (<c>class_bound</c>), not an edge.</item>
    /// <item>class: only the first entry can be a superclass. It is INHERITS when it names a class
    /// declared in the file and IMPLEMENTS when it names a protocol declared in the file; otherwise
    /// it is IMPLEMENTS when it looks like a protocol (well-known protocol, or a Protocol/Delegate/
    /// DataSource suffix, except well-known classes such as UITableViewDiffableDataSource) and
    /// INHERITS when not, marked <c>inheritance_inferred</c> so the pipeline replaces the guess
    /// when the name resolves to a repository type. Later entries are IMPLEMENTS.</item>
    /// </list>
    /// Names resolve lexically: a type declared in the declaration's enclosing scope, then outward
    /// to the file's top level. Targets not found in the file are symbolic references the pipeline
    /// resolves against top-level repository types.
    /// </summary>
    private static void AddSwiftTypeRelations(TypeDeclarationContext context)
    {
        var kind = GetSwiftDeclarationKind(context.Node);
        var entries = context.Node.NamedChildren
            .Where(child => child.Type == "inheritance_specifier")
            .Select(child => SwiftCallSites.TypePath(
                child.GetChildForField("inherits_from") ?? child.NamedChildren.FirstOrDefault()))
            .ToList();

        for (var index = 0; index < entries.Count; index++)
        {
            var path = entries[index];
            if (path is null)
                continue;

            if (kind == "enum" && index == 0 && SwiftEnumRawValueTypes.Contains(path))
            {
                if (context.TypeProperties is not null)
                    context.TypeProperties["raw_type"] = path;
                continue;
            }

            if (kind == "protocol" && path == "AnyObject")
            {
                if (context.TypeProperties is not null)
                    context.TypeProperties["class_bound"] = true;
                continue;
            }

            var local = context.ResolveLocalType(path);
            var inferred = false;
            EdgeType edgeType;
            if (kind == "protocol")
            {
                edgeType = EdgeType.INHERITS;
            }
            else if (kind == "class" && index == 0)
            {
                inferred = local is null;
                edgeType = local is not null
                    ? local.Label == NodeLabel.Interface ? EdgeType.IMPLEMENTS : EdgeType.INHERITS
                    : LooksLikeSwiftProtocol(path) ? EdgeType.IMPLEMENTS : EdgeType.INHERITS;
            }
            else
            {
                edgeType = EdgeType.IMPLEMENTS;
            }

            var targetQN = local?.QualifiedName ?? SwiftTypeReference.Create(path);
            var properties = new Dictionary<string, object>
            {
                ["confidence"] = inferred ? "medium" : "high"
            };
            if (inferred)
                properties[SwiftTypeReference.InheritanceInferredProperty] = true;
            if (kind == "protocol")
                properties[SwiftTypeReference.ProtocolTargetProperty] = true;
            // An edge with a symbolic endpoint is owned by this file: the pipeline resolves it,
            // and re-resolves it when this file or a type of that name changes.
            if (SwiftTypeReference.TryParse(context.TypeQN, out _) || SwiftTypeReference.TryParse(targetQN, out _))
                properties[SwiftTypeReference.OriginFileProperty] = context.OriginFile;

            context.Edges.Add(new PendingEdge(context.TypeQN, targetQN, edgeType, properties));
        }
    }

    /// <summary>
    /// A computed property (<c>var area: Double { ... }</c>, with or without get/set) or a protocol
    /// property requirement becomes a Property node. Stored properties (with or without
    /// willSet/didSet observers) do not: like fields in the other tree-sitter languages, they are
    /// not graph nodes.
    /// </summary>
    private static PropertyDeclaration? GetSwiftProperty(Node node)
    {
        var isRequirement = node.Type == "protocol_property_declaration";
        var computed = node.GetChildForField("computed_value");
        if (!isRequirement && computed is null)
            return null;

        var pattern = node.GetChildForField("name");
        var name = pattern?.GetChildForField("bound_identifier")?.Text ?? pattern?.Text;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var properties = new Dictionary<string, object>
        {
            [isRequirement ? "requirement" : "computed"] = true
        };

        var type = node.NamedChildren.FirstOrDefault(child => child.Type == "type_annotation");
        if ((type?.GetChildForField("name") ?? type?.NamedChildren.LastOrDefault())?.Text is { Length: > 0 } typeText)
            properties["type"] = typeText;

        var modifiers = node.NamedChildren.FirstOrDefault(child => child.Type == "modifiers");
        properties["is_static"] = modifiers is not null && modifiers.NamedChildren.Any(child =>
            child.Type == "property_modifier" && child.Text is "static" or "class");

        var accessorParent = isRequirement
            ? node.NamedChildren.FirstOrDefault(child => child.Type == "protocol_property_requirements")
            : computed;
        var accessors = (accessorParent?.NamedChildren ?? [])
            .Select(child => child.Type switch
            {
                "computed_getter" or "getter_specifier" => "get",
                "computed_setter" or "setter_specifier" => "set",
                "computed_modify" or "modify_specifier" => "modify",
                _ => null
            })
            .OfType<string>()
            .Distinct()
            .ToList();
        // A computed property with only a body is a read-only getter.
        properties["accessors"] = accessors.Count == 0 ? "get" : string.Join(",", accessors);
        return new PropertyDeclaration(name, properties);
    }
}
