namespace TC.CodeGraphApi.Services.Analyzers;

/// <summary>
/// Abstracts file system access for analyzers, enabling unit testing without real files.
/// </summary>
public interface ISourceFileProvider
{
    /// <summary>
    /// Returns relative paths of scannable source files under the given root,
    /// excluding directories like bin/obj/node_modules and filtering to known extensions.
    /// </summary>
    IEnumerable<SourceFile> EnumerateSourceFiles(string rootPath);

    /// <summary>
    /// Returns whether the given root path exists.
    /// </summary>
    bool RootExists(string rootPath);
}

/// <summary>
/// A source file with its relative path and content, read once during enumeration.
/// </summary>
public record SourceFile(string RelativePath, string Extension, string[] Lines);
