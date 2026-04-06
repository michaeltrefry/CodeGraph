using Microsoft.Extensions.Logging;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Extractors.Ansible;

public class AnsibleExtractor : ICodeExtractor
{
    private readonly ILogger<AnsibleExtractor> _logger;

    public AnsibleExtractor(ILogger<AnsibleExtractor> logger)
    {
        _logger = logger;
    }

    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string> { ".yml", ".yaml" };

    public Task<ExtractionResult> ExtractAsync(string filePath, string content,
        ExtractorContext context, CancellationToken ct = default)
    {
        var parser = new AnsibleParser(context, filePath, _logger);
        var result = parser.Parse(content);
        return Task.FromResult(result);
    }
}
