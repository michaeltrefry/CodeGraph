using CodeGraph.Models.Responses;

namespace CodeGraph.Services.Analyzers;

public interface ISecurityAnalyzer
{
    Task<SecurityScanResult> ScanAsync(string repositoryName, string repoPath, CancellationToken ct = default);
}
