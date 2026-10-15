using CodeGraph.Models;

namespace CodeGraph.Services.Extractors;

/// <summary>
/// Extracts Cargo package definitions and dependency references from all manifests in a repository.
/// </summary>
public interface ICargoManifestExtractor
{
    /// <summary>
    /// Version of the extraction output. Bump whenever the emitted nodes, edges or properties
    /// change for the same manifest content: an unchanged manifest's package slice is only
    /// rewritten when its content or this version changes (see <c>ExtractionVersionCatalog</c>).
    /// </summary>
    string ExtractionVersion => "1";

    ExtractionResult Extract(
        IReadOnlyDictionary<string, string> manifests,
        ExtractorContext context);
}
