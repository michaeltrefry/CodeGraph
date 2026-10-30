using CodeGraph.Models;

namespace CodeGraph.Services.Extractors;

public interface ICodeExtractor
{
    IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>
    /// Version of the output this extractor produces for files with <paramref name="extension"/>.
    /// Persisted per file (file_hashes.extraction_version); a stored value that differs from the
    /// current one makes incremental indexing re-extract the file even though its content is
    /// unchanged. Bump it whenever the extractor's output changes for unchanged input — see
    /// <see cref="CodeGraph.Services.Pipeline.ExtractionVersionCatalog"/> for the contract.
    /// </summary>
    string GetExtractionVersion(string extension) => "1";

    Task<ExtractionResult> ExtractAsync(string filePath, string content,
        ExtractorContext context, CancellationToken ct = default);
}

/// <summary>
/// Shared context available to all extractors — foundational knowledge, repository info.
/// </summary>
public class ExtractorContext
{
    public required string RepositoryName { get; init; }
    public required string RootPath { get; init; }

    /// <summary>
    /// The actual .csproj project name (e.g. "Orders.ServiceDefaults").
    /// Set by SolutionAnalyzer per Roslyn project. Null when running outside solution analysis.
    /// </summary>
    public string? DotnetProject { get; init; }

    /// <summary>Discovery-admitted, bounded Go module manifests keyed by repository-relative
    /// path. Extractors consume this snapshot and never discover/read manifests themselves.</summary>
    public IReadOnlyDictionary<string, string> GoModuleManifests { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Known foundational types and their meanings (populated after analyzing framework repos).
    /// </summary>
    public FoundationalKnowledge? FoundationalKnowledge { get; init; }

    /// <summary>
    /// Whether repository-controlled tooling may run for this indexing operation.
    /// The default is untrusted so every caller must opt in explicitly.
    /// </summary>
    public RepositoryToolingTrust RepositoryToolingTrust { get; init; } = RepositoryToolingTrust.Untrusted;
}

public enum RepositoryToolingTrust
{
    Untrusted = 0,
    Trusted = 1
}

public class FoundationalKnowledge
{
    /// <summary>
    /// Attribute types that indicate message publishing and their queue name properties.
    /// </summary>
    public Dictionary<string, string> PublishAttributes { get; init; } = new();

    /// <summary>
    /// Attribute types that indicate message consuming.
    /// </summary>
    public Dictionary<string, string> ConsumeAttributes { get; init; } = new();

    /// <summary>
    /// Base classes that indicate specific patterns (e.g., ServiceBus base class).
    /// </summary>
    public Dictionary<string, string> PatternBaseClasses { get; init; } = new();

    /// <summary>
    /// DI extension methods that register services.
    /// </summary>
    public HashSet<string> DIRegistrationMethods { get; init; } = new();
}
