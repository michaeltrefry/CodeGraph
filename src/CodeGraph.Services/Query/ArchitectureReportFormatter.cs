using System.Text;
using CodeGraph.Models;

namespace CodeGraph.Services.Query;

/// <summary>
/// Markdown rendering of an <see cref="ArchitectureReport"/> shared by the MCP tool and the
/// in-app assistant. Every section is bounded: list sections show the report's capped rows,
/// and cross-repo dependencies are summarized per repository and edge type.
/// </summary>
public static class ArchitectureReportFormatter
{
    public static string Format(ArchitectureReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Architecture: {report.Project}\n");

        if (report.Summary is not null)
        {
            sb.AppendLine($"**Confidence:** {report.Confidence}");
            sb.AppendLine();
            sb.AppendLine(report.Summary);
            sb.AppendLine();
        }

        sb.AppendLine("### Node Counts\n");
        foreach (var (label, count) in report.NodeCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.ToString()))
            sb.AppendLine($"- {label}: {count:N0}");
        sb.AppendLine();

        if (report.Hotspots.Count > 0)
        {
            sb.AppendLine("### Hotspots (high fan-in methods)\n");
            foreach (var h in report.Hotspots)
                sb.AppendLine($"- **{h.Name}** (fan-in: {h.FanIn}) — {h.Reason}");
            sb.AppendLine();
        }

        if (report.TopSupertypes.Count > 0)
        {
            sb.AppendLine("### Key Type Relationships (most implemented/inherited types)\n");
            foreach (var t in report.TopSupertypes)
                sb.AppendLine($"- **{t.Name}** ({t.Label}{(t.IsExternal ? ", external" : "")}) — " +
                              $"{t.SubtypeCount} implementer/subtype{(t.SubtypeCount == 1 ? "" : "s")}");
            sb.AppendLine();
        }

        if (report.PackageDependencyCount > 0)
        {
            var ecosystems = string.Join(", ", report.PackageDependencyCountsByEcosystem
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}: {kv.Value}"));
            sb.AppendLine($"### Package Dependencies ({report.PackageDependencyCount:N0} external; {ecosystems})\n");
            foreach (var p in report.PackageDependencies)
                sb.AppendLine($"- {p.Name}{(p.Version is null ? "" : $" {p.Version}")} ({p.Ecosystem}, " +
                              $"referenced by {p.DependentCount})");
            var hidden = report.PackageDependencyCount - report.PackageDependencies.Count;
            if (hidden > 0)
                sb.AppendLine($"- … and {hidden:N0} more");
            sb.AppendLine();
        }

        AppendCrossRepo(sb, "### Inbound Dependencies (other repos depend on this)\n",
            report.InboundDependencies.Select(e => (Repo: e.SourceProject, e.Type)), outbound: false);
        AppendCrossRepo(sb, "### Outbound Dependencies (this depends on other repos)\n",
            report.OutboundDependencies.Select(e => (Repo: e.TargetProject, e.Type)), outbound: true);

        return sb.ToString();
    }

    private static void AppendCrossRepo(
        StringBuilder sb,
        string heading,
        IEnumerable<(string Repo, EdgeType Type)> edges,
        bool outbound)
    {
        var groups = edges
            .GroupBy(e => e)
            .Select(g => (g.Key.Repo, g.Key.Type, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Repo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Type.ToString(), StringComparer.Ordinal)
            .ToList();
        if (groups.Count == 0)
            return;

        sb.AppendLine(heading);
        foreach (var (repo, type, count) in groups)
        {
            var link = outbound ? $"this → {repo}" : $"{repo} → this";
            sb.AppendLine($"- {link} ({type}, {count:N0} edge{(count == 1 ? "" : "s")})");
        }
        sb.AppendLine();
    }
}
