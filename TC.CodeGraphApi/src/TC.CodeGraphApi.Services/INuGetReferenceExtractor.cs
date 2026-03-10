namespace TC.CodeGraphApi.Services;

/// <summary>
/// Extracts NuGet package references from project files.
/// </summary>
public interface INuGetReferenceExtractor
{
    IReadOnlyList<(string PackageName, string Version)> ExtractFromProject(string csprojPath);
}
