using CodeGraph.Models;
using TreeSitter;

namespace CodeGraph.Extractors.TreeSitter;

/// <summary>
/// Data-driven specification for extracting graph nodes from a tree-sitter AST.
/// Each language defines which node types correspond to which code concepts.
/// Inspired by the codebase-memory-mcp lang_specs approach.
/// </summary>
public sealed class LanguageSpec
{
    public required string LanguageName { get; init; }
    public required string? Framework { get; init; }
    public required Func<Language> GetLanguage { get; init; }

    /// <summary>
    /// Version of this language's extraction output (spec data and its domain extractor).
    /// Bump it whenever the output changes for unchanged source so existing repositories are
    /// re-extracted (see ExtractionVersionCatalog).
    /// </summary>
    public int ExtractionVersion { get; init; } = 1;

    /// <summary>AST node types that represent function/method definitions.</summary>
    public string[] FunctionNodeTypes { get; init; } = [];

    /// <summary>AST node types that represent class/struct/interface definitions.</summary>
    public string[] ClassNodeTypes { get; init; } = [];

    /// <summary>AST node types that represent call expressions.</summary>
    public string[] CallNodeTypes { get; init; } = [];

    /// <summary>
    /// Ordered field names that may hold the callable expression or callable name.
    /// The defaults cover C-like grammars; specs override them where needed.
    /// </summary>
    public string[] CallTargetFields { get; init; } = ["function", "name", "method"];

    /// <summary>Ordered field names that may hold a call receiver or scope.</summary>
    public string[] CallReceiverFields { get; init; } =
        ["object", "receiver", "scope", "operand", "value", "path", "argument"];

    /// <summary>AST node types that represent import/include statements.</summary>
    public string[] ImportNodeTypes { get; init; } = [];

    /// <summary>AST node types for variable/constant declarations.</summary>
    public string[] VariableNodeTypes { get; init; } = [];

    /// <summary>Field name on definition nodes that holds the identifier.</summary>
    public string NameField { get; init; } = "name";

    /// <summary>
    /// Optional custom name extractor for languages where the name isn't a direct field
    /// (e.g., C functions where name is at declarator.declarator).
    /// Takes the definition node and returns the name, or null.
    /// </summary>
    public Func<Node, string?>? NameExtractor { get; init; }

    /// <summary>
    /// Optional class/type-only name extractor for wrapper declarations whose
    /// name is not exposed through <see cref="NameField"/>.
    /// </summary>
    public Func<Node, string?>? ClassNameExtractor { get; init; }

    /// <summary>
    /// Optional per-node label for class-like definitions, for grammars where one AST node type
    /// covers several kinds (e.g. Swift class_declaration covers class/struct/enum/actor).
    /// Returning null falls back to <see cref="ClassLabel"/>.
    /// </summary>
    public Func<Node, NodeLabel?>? ClassLabelSelector { get; init; }

    /// <summary>
    /// Optional declaration keyword of a class-like definition (e.g. "struct", "protocol"),
    /// recorded as the node's <c>declaration_kind</c> property.
    /// </summary>
    public Func<Node, string?>? ClassKindExtractor { get; init; }

    /// <summary>
    /// Identifies class-like nodes that only add members to a type declared elsewhere
    /// (Rust impl blocks, Swift extensions). They emit no type node of their own; their members
    /// attach to the owning type when it is declared in the same file.
    /// </summary>
    public Func<Node, bool>? IsContainerOnlyClass { get; init; }

    /// <summary>
    /// For a container-only definition whose owning type is not declared in the same file: the
    /// owner's dotted path and a symbolic reference to it, resolved across files by the pipeline.
    /// Its members get qualified names under <c>#extension:{path}</c> and their definition edges
    /// start at the reference. Null keeps the members under the file (Rust impl blocks).
    /// </summary>
    public Func<Node, ContainerOwner?>? ContainerOwnerReference { get; init; }

    /// <summary>
    /// Optional hook run for every class-like definition (container-only ones included) to emit
    /// type-level relations such as inheritance and conformance edges.
    /// </summary>
    public Action<TypeDeclarationContext>? TypeRelationExtractor { get; init; }

    /// <summary>AST node types that may declare a property of the enclosing type.</summary>
    public string[] PropertyNodeTypes { get; init; } = [];

    /// <summary>
    /// Describes a <see cref="PropertyNodeTypes"/> declaration that becomes a Property node under
    /// its enclosing type, or returns null when it does not (e.g. a stored property).
    /// </summary>
    public Func<Node, PropertyDeclaration?>? PropertyExtractor { get; init; }

    /// <summary>Field name for function return types (if applicable).</summary>
    public string? ReturnTypeField { get; init; }

    /// <summary>Field name for function parameters.</summary>
    public string? ParametersField { get; init; }

    /// <summary>
    /// Optional parameter-list extractor for grammars whose parameters are not grouped under
    /// <see cref="ParametersField"/>. Takes precedence over <see cref="ParametersField"/>.
    /// </summary>
    public Func<Node, string?>? ParametersExtractor { get; init; }

    /// <summary>
    /// When true, a file whose parse or AST walk throws is logged and skipped with an empty
    /// successful result instead of reporting an extraction failure, so it can never fail an
    /// index run. A grammar that cannot be loaded at all is also skipped, but under a distinct
    /// "grammar unavailable" extraction version so the files are re-extracted once it loads;
    /// a grammar that fails to load mid-run is reported as an extraction failure.
    /// </summary>
    public bool SkipFileOnExtractionFailure { get; init; }

    /// <summary>Field name for the body of a definition.</summary>
    public string? BodyField { get; init; }

    /// <summary>Field name for base/super class on class definitions.</summary>
    public string? SuperclassField { get; init; }

    /// <summary>What NodeLabel to assign to class-like definitions.</summary>
    public NodeLabel ClassLabel { get; init; } = NodeLabel.Class;

    /// <summary>What NodeLabel to assign to function-like definitions.</summary>
    public NodeLabel FunctionLabel { get; init; } = NodeLabel.Function;

    /// <summary>
    /// Optional domain-specific extraction hooks.
    /// Called after generic extraction, can add extra nodes/edges for the language.
    /// </summary>
    public Action<DomainExtractionContext>? DomainExtractor { get; init; }
}

/// <summary>The type a container-only definition extends, when it is declared in another file.</summary>
public sealed record ContainerOwner(string Path, string ReferenceQN);

/// <summary>A property declaration that becomes a Property node.</summary>
public sealed record PropertyDeclaration(string Name, Dictionary<string, object> Properties);

/// <summary>A type declared in the file being extracted.</summary>
public sealed record LocalType(string QualifiedName, NodeLabel Label);

/// <summary>Context passed to <see cref="LanguageSpec.TypeRelationExtractor"/>.</summary>
public sealed class TypeDeclarationContext
{
    public required Node Node { get; init; }

    /// <summary>
    /// The declared type, or the extended type of a container-only definition: its qualified
    /// name when declared in this file, else its <see cref="ContainerOwner.ReferenceQN"/>.
    /// </summary>
    public required string TypeQN { get; init; }

    public required bool IsContainerOnly { get; init; }

    /// <summary>Properties of the emitted type node; null for a container-only definition.</summary>
    public required Dictionary<string, object>? TypeProperties { get; init; }

    /// <summary>Repository-relative path of the file being extracted.</summary>
    public required string OriginFile { get; init; }

    /// <summary>
    /// Resolves a dotted type path to a type declared in this file, looking in the declaration's
    /// enclosing scope and then outward to the file's top level (lexical scoping).
    /// </summary>
    public required Func<string, LocalType?> ResolveLocalType { get; init; }

    public required List<PendingEdge> Edges { get; init; }
}

/// <summary>
/// Context passed to domain-specific extraction hooks.
/// </summary>
public sealed class DomainExtractionContext
{
    public required Node RootNode { get; init; }
    public required string Source { get; init; }
    public required string FilePath { get; init; }
    public required ExtractorContext Context { get; init; }
    public required List<GraphNode> Nodes { get; init; }
    public required List<PendingEdge> Edges { get; init; }
    public required List<UnresolvedCall> UnresolvedCalls { get; init; }
    public required List<UnresolvedImport> UnresolvedImports { get; init; }
    public required Func<string, string, NodeLabel, Dictionary<string, object>?, GraphNode> MakeNode { get; init; }
}
