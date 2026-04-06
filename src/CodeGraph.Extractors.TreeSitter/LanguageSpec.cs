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

    /// <summary>AST node types that represent function/method definitions.</summary>
    public string[] FunctionNodeTypes { get; init; } = [];

    /// <summary>AST node types that represent class/struct/interface definitions.</summary>
    public string[] ClassNodeTypes { get; init; } = [];

    /// <summary>AST node types that represent call expressions.</summary>
    public string[] CallNodeTypes { get; init; } = [];

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

    /// <summary>Field name for function return types (if applicable).</summary>
    public string? ReturnTypeField { get; init; }

    /// <summary>Field name for function parameters.</summary>
    public string? ParametersField { get; init; }

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
