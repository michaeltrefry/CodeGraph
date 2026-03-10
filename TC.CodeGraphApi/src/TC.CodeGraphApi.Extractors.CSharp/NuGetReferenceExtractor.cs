using System.Xml.Linq;
using TC.CodeGraphApi.Services;

namespace TC.CodeGraphApi.Extractors.CSharp;

public class NuGetReferenceExtractor : INuGetReferenceExtractor
{
    public IReadOnlyList<(string PackageName, string Version)> ExtractFromProject(
        string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);
        return doc.Descendants("PackageReference")
            .Select(pr => (
                PackageName: pr.Attribute("Include")?.Value ?? "",
                Version: pr.Attribute("Version")?.Value ?? ""))
            .Where(p => !string.IsNullOrEmpty(p.PackageName))
            .ToList();
    }
}
