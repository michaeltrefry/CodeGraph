using System.Xml.Linq;
using CodeGraph.Services;

namespace CodeGraph.Extractors.CSharp;

public class NuGetReferenceExtractor : INuGetReferenceExtractor
{
    public IReadOnlyList<(string PackageName, string Version)> ExtractFromProjectXml(
        string csprojXml)
    {
        var doc = XDocument.Parse(csprojXml);
        return doc.Descendants("PackageReference")
            .Select(pr => (
                PackageName: pr.Attribute("Include")?.Value ?? "",
                Version: pr.Attribute("Version")?.Value ?? ""))
            .Where(p => !string.IsNullOrEmpty(p.PackageName))
            .ToList();
    }
}
