using CodeGraph.Models;

namespace CodeGraph.Services.Extractors;

/// <summary>
/// Extracts Cargo package definitions and dependency references from all manifests in a repository.
/// </summary>
public interface ICargoManifestExtractor
{
    ExtractionResult Extract(
        IReadOnlyDictionary<string, string> manifests,
        ExtractorContext context);
}
