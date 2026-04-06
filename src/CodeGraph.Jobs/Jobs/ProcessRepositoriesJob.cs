using CodeGraph.Models.Messages;
using CodeGraph.Services.Messaging;

namespace CodeGraph.Jobs.Jobs;

/// <summary>
/// Reads a list of repositories from StartJob.Args and publishes one
/// ProcessRepository message per repo. Short-lived — returns after publishing.
///
/// Args:
///   repos          — semicolon-separated repo entries. Each entry can be:
///                      "Name::Path"               — explicit local path
///                      "Name::https://host/..."    — explicit clone URL
///                      "Name"                      — resolved via cache or provider
///   shouldIndex    — "true"|"false"  (default: true)
///   shouldAnalyze  — "true"|"false"  (default: true)
///   skipIfUpToDate — "true"|"false"  (default: true)
/// </summary>
public class ProcessRepositoriesJob(
    IMessageBus messageBus,
    ILogger<ProcessRepositoriesJob> logger) : IJob
{
    public async Task ExecuteAsync(StartJob startJob, CancellationToken ct = default)
    {
        var reposArg = startJob.Args?.GetValueOrDefault("repos", "") ?? "";
        if (string.IsNullOrWhiteSpace(reposArg))
        {
            logger.LogWarning("ProcessRepositoriesJob called with no repos argument");
            return;
        }

        var shouldIndex    = ParseFlag(startJob.Args, "shouldIndex",    defaultValue: true);
        var shouldAnalyze  = ParseFlag(startJob.Args, "shouldAnalyze",  defaultValue: true);
        var skipIfUpToDate = ParseFlag(startJob.Args, "skipIfUpToDate", defaultValue: true);

        var entries = reposArg.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        logger.LogInformation("Publishing {Count} ProcessRepository message(s)", entries.Length);

        foreach (var entry in entries)
        {
            var parts = entry.Split("::", 2);
            var name = parts[0].Trim();
            var explicitPath = parts.Length == 2 ? parts[1].Trim() : null;

            var message = new ProcessRepository
            {
                Name           = name,
                ShouldIndex    = shouldIndex,
                ShouldAnalyze  = shouldAnalyze,
                SkipIfUpToDate = skipIfUpToDate
            };

            if (!string.IsNullOrWhiteSpace(explicitPath) && explicitPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                message.RepoUrl = explicitPath;
            }
            else if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                message.Path = explicitPath;
            }

            await messageBus.PublishAsync(message, ct);
        }
    }

    private static bool ParseFlag(Dictionary<string, string>? args, string key, bool defaultValue)
    {
        if (args is null || !args.TryGetValue(key, out var value))
            return defaultValue;
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }
}
