using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeGraph.Models;

namespace CodeGraph.Services.Metadata;

public static class DotnetSupportInspector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly IReadOnlyDictionary<string, DateTime> SupportedChannels =
        new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            // Based on Microsoft .NET support policy / releases-and-support docs as of 2026-04-07.
            ["1.0"] = new DateTime(2019, 6, 27, 0, 0, 0, DateTimeKind.Utc),
            ["1.1"] = new DateTime(2019, 6, 27, 0, 0, 0, DateTimeKind.Utc),
            ["2.0"] = new DateTime(2018, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            ["2.1"] = new DateTime(2021, 8, 21, 0, 0, 0, DateTimeKind.Utc),
            ["2.2"] = new DateTime(2019, 12, 23, 0, 0, 0, DateTimeKind.Utc),
            ["3.0"] = new DateTime(2020, 3, 3, 0, 0, 0, DateTimeKind.Utc),
            ["3.1"] = new DateTime(2022, 12, 13, 0, 0, 0, DateTimeKind.Utc),
            ["5.0"] = new DateTime(2022, 5, 10, 0, 0, 0, DateTimeKind.Utc),
            ["6.0"] = new DateTime(2024, 11, 12, 0, 0, 0, DateTimeKind.Utc),
            ["7.0"] = new DateTime(2024, 5, 14, 0, 0, 0, DateTimeKind.Utc),
            ["8.0"] = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc),
            ["9.0"] = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc),
            ["10.0"] = new DateTime(2028, 11, 14, 0, 0, 0, DateTimeKind.Utc)
        };

    public static DotnetSupportInfo? InspectRepository(string? repoPath, DateTime? asOfUtc = null)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return null;

        var targetFrameworks = GetTargetFrameworks(repoPath);
        var sdk = GetSdkSupport(repoPath, asOfUtc);

        if (sdk is null && targetFrameworks.Count == 0)
            return null;

        var overallStatus = GetOverallStatus(
            sdk is null ? [] : [sdk.SupportStatus],
            targetFrameworks.Select(t => t.SupportStatus));

        return new DotnetSupportInfo(
            overallStatus,
            BuildSummary(sdk, targetFrameworks, overallStatus),
            sdk,
            targetFrameworks);
    }

    public static DotnetSupportInfo? TryReadStoredSupport(Dictionary<string, object>? properties)
    {
        if (properties is null || !properties.TryGetValue("dotnetSupport", out var value) || value is null)
            return null;

        if (value is DotnetSupportInfo typed)
            return typed;

        if (value is JsonElement json && json.ValueKind == JsonValueKind.Object)
            return json.Deserialize<DotnetSupportInfo>(JsonOptions);

        if (value is string jsonText && !string.IsNullOrWhiteSpace(jsonText))
            return JsonSerializer.Deserialize<DotnetSupportInfo>(jsonText, JsonOptions);

        return null;
    }

    public static Dictionary<string, object>? BuildRepositoryProperties(DotnetSupportInfo? dotnetSupport)
    {
        if (dotnetSupport is null)
            return null;

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["dotnetSupport"] = dotnetSupport
        };
    }

    private static DotnetSdkSupportInfo? GetSdkSupport(string repoPath, DateTime? asOfUtc)
    {
        var globalJsonPath = Path.Combine(repoPath, "global.json");
        if (!File.Exists(globalJsonPath))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(globalJsonPath));
            if (!doc.RootElement.TryGetProperty("sdk", out var sdkElement))
                return null;

            if (!sdkElement.TryGetProperty("version", out var versionElement))
                return null;

            var version = versionElement.GetString();
            if (string.IsNullOrWhiteSpace(version))
                return null;

            var channel = GetSdkChannel(version);
            var status = DescribeChannel(channel, asOfUtc);

            return new DotnetSdkSupportInfo(
                version,
                channel,
                $".NET SDK {channel}",
                status.SupportStatus,
                status.SupportEndedOn,
                true);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<DotnetTargetFrameworkSupportInfo> GetTargetFrameworks(string repoPath)
    {
        var results = new List<DotnetTargetFrameworkSupportInfo>();

        foreach (var csproj in Directory.EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories))
        {
            if (IsIgnoredProjectPath(csproj))
                continue;

            try
            {
                var doc = XDocument.Load(csproj);
                foreach (var tfm in ReadTargetFrameworks(doc))
                {
                    var status = DescribeTargetFramework(tfm);
                    if (results.All(existing => !existing.Moniker.Equals(status.Moniker, StringComparison.OrdinalIgnoreCase)))
                        results.Add(status);
                }
            }
            catch
            {
                // Ignore malformed project files; they shouldn't block the rest of the repo metadata.
            }
        }

        return results
            .OrderBy(t => t.Moniker, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsIgnoredProjectPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/packages/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadTargetFrameworks(XDocument doc)
    {
        var frameworks = new List<string>();
        foreach (var element in doc.Descendants())
        {
            var name = element.Name.LocalName;
            if (!name.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("TargetFrameworks", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = element.Value;
            if (string.IsNullOrWhiteSpace(value))
                continue;

            frameworks.AddRange(value
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static tfm => !string.IsNullOrWhiteSpace(tfm)));
        }

        return frameworks
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetSdkChannel(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return version;

        return $"{parts[0]}.{parts[1]}";
    }

    private static DotnetTargetFrameworkSupportInfo DescribeTargetFramework(string tfm)
    {
        if (string.IsNullOrWhiteSpace(tfm))
            return new DotnetTargetFrameworkSupportInfo(tfm, tfm, "unknown", null);

        var normalized = tfm.Trim();

        if (normalized.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            return new DotnetTargetFrameworkSupportInfo(
                normalized,
                FormatNetStandardDisplayName(normalized),
                "not_applicable",
                null);
        }

        if (TryParseNetFramework(normalized, out var netFrameworkVersion))
        {
            return new DotnetTargetFrameworkSupportInfo(
                normalized,
                $".NET Framework {netFrameworkVersion}",
                "os_lifecycle",
                null);
        }

        if (TryParseDotnetChannel(normalized, out var channel, out var displayName))
        {
            var status = DescribeChannel(channel, null);
            return new DotnetTargetFrameworkSupportInfo(
                normalized,
                displayName,
                status.SupportStatus,
                status.SupportEndedOn);
        }

        return new DotnetTargetFrameworkSupportInfo(normalized, normalized, "unknown", null);
    }

    private static bool TryParseDotnetChannel(string tfm, out string channel, out string displayName)
    {
        channel = "";
        displayName = tfm;

        var match = Regex.Match(tfm, @"^(netcoreapp|net)(\d+)\.(\d+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        channel = $"{match.Groups[2].Value}.{match.Groups[3].Value}";
        var family = match.Groups[1].Value.Equals("netcoreapp", StringComparison.OrdinalIgnoreCase)
            ? ".NET Core"
            : int.Parse(match.Groups[2].Value) >= 5
                ? ".NET"
                : ".NET";

        var major = match.Groups[2].Value;
        var minor = match.Groups[3].Value;
        var formattedVersion = family == ".NET" && minor == "0"
            ? major
            : $"{major}.{minor}";

        displayName = $"{family} {formattedVersion}";
        return true;
    }

    private static bool TryParseNetFramework(string tfm, out string version)
    {
        version = "";
        var match = Regex.Match(tfm, @"^net([1-4])([0-9])([0-9]?)$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        version = match.Groups[3].Length > 0
            ? $"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}"
            : $"{match.Groups[1].Value}.{match.Groups[2].Value}";
        return int.Parse(match.Groups[1].Value) <= 4;
    }

    private static string FormatNetStandardDisplayName(string tfm)
    {
        var version = tfm["netstandard".Length..];
        if (version.Length >= 2)
            version = $"{version[0]}.{version[1..]}";

        return $".NET Standard {version}";
    }

    private static (string SupportStatus, DateTime? SupportEndedOn) DescribeChannel(string channel, DateTime? asOfUtc)
    {
        if (!SupportedChannels.TryGetValue(channel, out var supportEndedOn))
            return ("unknown", null);

        var effectiveDate = (asOfUtc ?? DateTime.UtcNow).Date;
        return supportEndedOn.Date < effectiveDate
            ? ("out_of_support", supportEndedOn)
            : ("supported", supportEndedOn);
    }

    private static string GetOverallStatus(
        IReadOnlyList<string> sdkStatuses,
        IEnumerable<string> targetStatuses)
    {
        var statuses = sdkStatuses
            .Concat(targetStatuses)
            .Where(static status => status is "supported" or "out_of_support")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return statuses.Count switch
        {
            0 => "unknown",
            1 => statuses[0],
            _ => "mixed"
        };
    }

    private static string BuildSummary(
        DotnetSdkSupportInfo? sdk,
        IReadOnlyList<DotnetTargetFrameworkSupportInfo> targetFrameworks,
        string overallStatus)
    {
        var parts = new List<string>();

        if (sdk is not null)
        {
            parts.Add(sdk.SupportStatus switch
            {
                "out_of_support" when sdk.SupportEndedOn is not null =>
                    $"Pinned SDK {sdk.Version} is out of support (ended {sdk.SupportEndedOn.Value:MMMM d, yyyy})",
                "supported" when sdk.SupportEndedOn is not null =>
                    $"Pinned SDK {sdk.Version} is supported until {sdk.SupportEndedOn.Value:MMMM d, yyyy}",
                _ => $"Pinned SDK {sdk.Version} has unknown support status"
            });
        }

        var actionableTargets = targetFrameworks
            .Where(t => t.SupportStatus is "supported" or "out_of_support")
            .ToList();

        if (actionableTargets.Count > 0)
        {
            var joinedTargets = string.Join(", ", actionableTargets.Select(t => t.DisplayName));
            parts.Add(overallStatus switch
            {
                "out_of_support" => $"Target frameworks are out of support: {joinedTargets}",
                "supported" => $"Target frameworks are supported: {joinedTargets}",
                "mixed" => $"Target frameworks are mixed support: {joinedTargets}",
                _ => $"Target frameworks detected: {joinedTargets}"
            });
        }

        if (parts.Count == 0 && targetFrameworks.Any(t => t.SupportStatus == "not_applicable"))
            return ".NET Standard targets do not have an independent support lifecycle.";

        if (parts.Count == 0 && targetFrameworks.Any(t => t.SupportStatus == "os_lifecycle"))
            return ".NET Framework support follows the host Windows lifecycle.";

        return string.Join(". ", parts);
    }
}
