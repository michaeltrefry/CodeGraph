namespace TC.CodeGraphApi.Services.Extractors;

/// <summary>
/// Extracts NuGet package references from project files.
/// </summary>
public interface INuGetReferenceExtractor
{
    IReadOnlyList<(string PackageName, string Version)> ExtractFromProjectXml(string csprojXml);
}
