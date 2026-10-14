namespace CodeGraph.Services.Pipeline;

/// <summary>
/// Symbolic references to Swift types that an extractor cannot resolve inside one file: the
/// type a cross-file <c>extension</c> adds members to, or a supertype named in an inheritance
/// clause. The extractor emits them as edge endpoints; the indexing pipeline resolves each one
/// to the single Swift type node of that name in the repository, or to an external stub node.
/// </summary>
public static class SwiftTypeReference
{
    /// <summary>Prefix of an unresolved reference endpoint, followed by the dotted type path.</summary>
    public const string Prefix = "swift-type:";

    /// <summary>Prefix of the stub node for a type declared outside the repository.</summary>
    public const string ExternalPrefix = "swift-external:";

    /// <summary>
    /// Edge property naming the symbolic reference of the extended type, on edges whose source
    /// is the extended type of a cross-file extension.
    /// </summary>
    public const string OwnerReferenceProperty = "owner_ref";

    /// <summary>
    /// Edge property naming the repository-relative file of the extension an edge comes from.
    /// The pipeline re-attaches such edges when only the extended type's file is re-indexed.
    /// </summary>
    public const string ExtensionFileProperty = "extension_file";

    /// <summary>
    /// Edge property marking a class's first inheritance-clause entry whose kind (superclass or
    /// protocol) was inferred by name; resolution to a repository type replaces the guess.
    /// </summary>
    public const string InheritanceInferredProperty = "inheritance_inferred";

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

    /// <summary>The simple (last) name of a dotted type path.</summary>
    public static string SimpleName(string typePath) =>
        typePath[(typePath.LastIndexOf('.') + 1)..];
}
