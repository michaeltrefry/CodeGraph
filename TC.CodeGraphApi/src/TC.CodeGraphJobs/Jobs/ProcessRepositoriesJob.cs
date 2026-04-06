using TC.CodeGraphApi.Models.Messages;
using TC.Common.TcServiceStack.Queue.Abstractions;
using TC.JobUtilities;

namespace TC.CodeGraphJobs.Jobs;

/// <summary>
/// Reads a list of repositories from StartJob.Args and publishes one
/// ProcessRepository message per repo. Short-lived — returns after publishing.
///
/// Args:
///   repos          — semicolon-separated repo entries. Each entry can be:
///                      "Name::Path"               — explicit local path
///                      "Name::https://gitlab/..."  — explicit GitLab URL (converted to SSH for clone)
///                      "Name"                      — resolved via cache or GitLab BaseUrl
///                    e.g. "TC.OrdersApi::https://gitlab.tcdevops.com/Group/TC.OrdersApi;TC.BillingApi"
///   shouldIndex    — "true"|"false"  (default: true)
///   shouldAnalyze  — "true"|"false"  (default: true)
///   skipIfUpToDate — "true"|"false"  (default: true)
/// </summary>
public class ProcessRepositoriesJob(
    ILogger<ProcessRepositoriesJob> logger,
    ITcServiceBus serviceBus,
    Guid instanceKey)
    : Job(logger, serviceBus, instanceKey)
{
    protected override async Task ExecuteAsync(StartJob startJob)
    {
        var reposArg = startJob.Args?.GetValueOrDefault("repos", "") ?? "";
        if (string.IsNullOrWhiteSpace(reposArg))
        {
            Logger?.LogWarning("ProcessRepositoriesJob called with no repos argument");
            return;
        }

        var shouldIndex    = ParseFlag(startJob.Args, "shouldIndex",    defaultValue: true);
        var shouldAnalyze  = ParseFlag(startJob.Args, "shouldAnalyze",  defaultValue: true);
        var skipIfUpToDate = ParseFlag(startJob.Args, "skipIfUpToDate", defaultValue: true);

        var entries = reposArg.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Logger?.LogInformation("Publishing {Count} ProcessRepository message(s)", entries.Length);

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
                // Explicit GitLab URL provided
                message.GitLabUrl = explicitPath;
            }
            else if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                // Explicit local path provided
                message.Path = explicitPath;
            }
            // else: name only — consumer will resolve via cached repo, stored repo_url, or fail

            await ServiceBus.Publish(message);
        }
    }

    private static bool ParseFlag(Dictionary<string, string>? args, string key, bool defaultValue)
    {
        if (args is null || !args.TryGetValue(key, out var value))
            return defaultValue;
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }
}
