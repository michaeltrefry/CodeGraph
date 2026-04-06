using TC.CodeGraphApi.Models;

namespace TC.CodeGraphApi.Services.Extractors;

public interface ICodeExtractor
{
    IReadOnlySet<string> SupportedExtensions { get; }
    Task<ExtractionResult> ExtractAsync(string filePath, string content,
        ExtractorContext context, CancellationToken ct = default);
}

/// <summary>
/// Shared context available to all extractors — foundational knowledge, project info.
/// </summary>
public class ExtractorContext
{
    public required string ProjectName { get; init; }
    public required string RootPath { get; init; }

    /// <summary>
    /// The actual .csproj project name (e.g. "TC.OrdersApi.Services").
    /// Set by SolutionAnalyzer per Roslyn project. Null when running outside solution analysis.
    /// </summary>
    public string? DotnetProject { get; init; }

    /// <summary>
    /// Known foundational types and their meanings (populated after analyzing framework repos).
    /// </summary>
    public FoundationalKnowledge? FoundationalKnowledge { get; init; }
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
