namespace CodeGraph.Services.Extractors;

/// <summary>
/// The file extensions indexing supports and the extractor that reads each, built once from the
/// registered extractors. Extensions match by ordinal (case-sensitive) comparison everywhere —
/// default-branch discovery, overlay path filtering, extraction dispatch, extraction versions and
/// <see cref="IndexedLanguageCatalog"/> — even though an extractor's own
/// <see cref="ICodeExtractor.SupportedExtensions"/> set may ignore case, so <c>Foo.SWIFT</c> is never
/// indexed on any path. An extension supported by several extractors belongs to the first registered.
/// </summary>
public sealed class ExtractorExtensions
{
    private readonly Dictionary<string, ICodeExtractor> _byExtension = new(StringComparer.Ordinal);

    public ExtractorExtensions(IEnumerable<ICodeExtractor> extractors)
    {
        foreach (var extractor in extractors)
            foreach (var extension in extractor.SupportedExtensions)
                _byExtension.TryAdd(extension, extractor);
        Extensions = _byExtension.Keys.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every supported extension (case-sensitive).</summary>
    public IReadOnlySet<string> Extensions { get; }

    /// <summary>True when indexing reads files with <paramref name="extension"/> (ordinal match).</summary>
    public bool IsSupportedExtension(string extension) => _byExtension.ContainsKey(extension);

    /// <summary>The extractor that reads files with <paramref name="extension"/>, or null.</summary>
    public ICodeExtractor? ExtractorFor(string extension) => _byExtension.GetValueOrDefault(extension);
}
