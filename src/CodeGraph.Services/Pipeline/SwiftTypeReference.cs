using CodeGraph.Models;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// Symbolic references to Swift types that an extractor cannot resolve inside one file: the
/// type a cross-file <c>extension</c> adds members to, or a supertype named in an inheritance
/// clause. The extractor emits them as edge endpoints; the indexing pipeline resolves each one
/// to the single top-level Swift type at that path in the repository, or to an external stub.
/// </summary>
/// <remarks>
/// A resolved edge records the reference it came from (<see cref="SourceRefProperty"/>,
/// <see cref="TargetRefProperty"/> and their simple names) and the file that emitted it
/// (<see cref="OriginFileProperty"/>), so later incremental runs can find and re-resolve it when
/// a type of that name appears, disappears or moves.
/// </remarks>
public static class SwiftTypeReference
{
    /// <summary>Prefix of an unresolved reference endpoint, followed by the dotted type path.</summary>
    public const string Prefix = "swift-type:";

    /// <summary>Prefix of the stub node for a type declared outside the repository.</summary>
    public const string ExternalPrefix = "swift-external:";

    /// <summary>Repository-relative file whose extraction emitted the edge.</summary>
    public const string OriginFileProperty = "origin_file";

    /// <summary>
    /// Marks a class's first inheritance-clause entry whose kind (superclass or protocol) is
    /// currently a name-based guess; resolution to a repository type replaces the guess.
    /// </summary>
    public const string InheritanceInferredProperty = "inheritance_inferred";

    /// <summary>The extractor's name-based guess (INHERITS or IMPLEMENTS) for an inferred entry.</summary>
    public const string InferredKindProperty = "swift_inferred_kind";

    /// <summary>
    /// Marks a relation bound to every candidate of an ambiguous type name (low confidence); it is
    /// re-bound once the name is unique.
    /// </summary>
    public const string AmbiguousProperty = "swift_ambiguous";

    /// <summary>Marks a relation whose target must be a protocol (protocol refinement).</summary>
    public const string ProtocolTargetProperty = "swift_protocol_target";

    /// <summary>The symbolic reference the edge's source was resolved from.</summary>
    public const string SourceRefProperty = "swift_source_ref";

    /// <summary>The symbolic reference the edge's target was resolved from.</summary>
    public const string TargetRefProperty = "swift_target_ref";

    /// <summary>Simple name of <see cref="SourceRefProperty"/>, the lookup key on later runs.</summary>
    public const string SourceNameProperty = "swift_source_name";

    /// <summary>Simple name of <see cref="TargetRefProperty"/>, the lookup key on later runs.</summary>
    public const string TargetNameProperty = "swift_target_name";

    /// <summary>
    /// Every edge type whose endpoint can be a Swift type reference: members and nested types of
    /// a cross-file extension, supertypes, and scoped imports (<c>import struct Module.Type</c>).
    /// Lookups of persisted reference edges are narrowed to these types.
    /// </summary>
    public static readonly IReadOnlyCollection<EdgeType> EdgeTypes =
    [
        EdgeType.DEFINES, EdgeType.DEFINES_METHOD, EdgeType.INHERITS, EdgeType.IMPLEMENTS, EdgeType.IMPORTS
    ];

    public static string Create(string typePath) => Prefix + typePath;

    public static bool TryParse(string? qualifiedName, out string typePath)
    {
        if (qualifiedName is not null
            && qualifiedName.StartsWith(Prefix, StringComparison.Ordinal)
            && qualifiedName.Length > Prefix.Length)
        {
            typePath = qualifiedName[Prefix.Length..];
            return true;
        }

        typePath = "";
        return false;
    }

    public static string ExternalQualifiedName(string typePath) => ExternalPrefix + typePath;

    public static bool IsExternal(string? qualifiedName) =>
        qualifiedName?.StartsWith(ExternalPrefix, StringComparison.Ordinal) == true;

    /// <summary>The simple (last) name of a dotted type path.</summary>
    public static string SimpleName(string typePath) =>
        typePath[(typePath.LastIndexOf('.') + 1)..];

    /// <summary>
    /// The dotted Swift path of a type node declared in a Swift file: <c>Outer.Inner</c> for
    /// <c>{file}#type:Outer#type:Inner</c>, and <c>Outer.Inner</c> for a type nested in a
    /// cross-file extension (<c>{file}#extension:Outer#type:Inner</c>). Null when the qualified
    /// name does not start with the node's file.
    /// </summary>
    public static string? TypePath(string project, string? filePath, string qualifiedName)
    {
        var filePrefix = $"{project}:{filePath}";
        if (string.IsNullOrEmpty(filePath)
            || !qualifiedName.StartsWith(filePrefix + "#", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var segment in qualifiedName[(filePrefix.Length + 1)..].Split('#'))
        {
            if (segment.StartsWith("type:", StringComparison.Ordinal))
                parts.Add(segment["type:".Length..]);
            else if (segment.StartsWith("extension:", StringComparison.Ordinal))
                parts.Add(segment["extension:".Length..]);
            else
                return null;
        }

        return string.Join('.', parts);
    }
}
