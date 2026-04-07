using System.ComponentModel;
using System.Text;
using CodeGraph.Data;
using ModelContextProtocol.Server;

namespace CodeGraph.Services.Assistant;

[McpServerResourceType]
public class CodeGraphMcpResources(IGraphStore store, IWikiStore wikiStore)
{
    [McpServerResource(
        UriTemplate = "codegraph://server/info",
        Name = "server_info",
        Title = "CodeGraph Server Info",
        MimeType = "text/markdown")]
    [Description("Overview of the CodeGraph MCP server and available resources.")]
    public string GetServerInfo()
    {
        return """
            # CodeGraph MCP Resources

            This server exposes MCP tools plus a small set of MCP resources.

            Static resources:
            - `codegraph://server/info`
            - `codegraph://projects/index`
            - `codegraph://conventions/index`

            Resource templates:
            - `codegraph://projects/{project}/summary`
            - `codegraph://conventions/{slug}`

            Use the `resources/read` MCP method to fetch the content for a listed resource URI.
            """;
    }

    [McpServerResource(
        UriTemplate = "codegraph://projects/index",
        Name = "projects_index",
        Title = "Indexed Projects",
        MimeType = "text/markdown")]
    [Description("Lists all indexed repositories with metadata.")]
    public async Task<string> GetProjectsIndex()
    {
        var projects = await store.ListRepositoriesAsync();
        if (projects.Count == 0)
            return "No projects indexed yet.";

        var lines = new List<string> { $"## Indexed Projects ({projects.Count})", "" };
        foreach (var project in projects.OrderBy(p => p.Name))
        {
            var flags = new List<string>();
            if (project.IsFoundational) flags.Add("foundational");
            if (!string.IsNullOrWhiteSpace(project.Language)) flags.Add(project.Language!);
            if (!string.IsNullOrWhiteSpace(project.Framework)) flags.Add(project.Framework!);

            var suffix = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";
            lines.Add($"- **{project.Name}**{suffix}");
        }

        return string.Join('\n', lines);
    }

    [McpServerResource(
        UriTemplate = "codegraph://conventions/index",
        Name = "conventions_index",
        Title = "Convention Documents",
        MimeType = "text/markdown")]
    [Description("Lists available convention documents and their slugs.")]
    public async Task<string> GetConventionsIndex()
    {
        var conventionsSection = await wikiStore.GetSectionBySlugAsync("conventions");
        if (conventionsSection is null)
            return "No convention documents found.";

        var pages = await wikiStore.GetPagesBySectionAsync(conventionsSection.Id);
        if (pages.Count == 0)
            return "No convention documents found.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Company Conventions ({pages.Count} documents)");
        sb.AppendLine();
        sb.AppendLine("Use `codegraph://conventions/{slug}` to read a specific document.");
        sb.AppendLine();

        foreach (var page in pages.OrderBy(p => p.Title))
            sb.AppendLine($"- **{page.Slug}** — {page.Title} (v{page.Revision}, by {page.Author})");

        return sb.ToString();
    }

    [McpServerResource(
        UriTemplate = "codegraph://projects/{project}/summary",
        Name = "project_summary",
        Title = "Project Summary",
        MimeType = "text/markdown")]
    [Description("Reads the generated repository summary for a project.")]
    public async Task<string> GetProjectSummary(string project)
    {
        var summary = await store.GetRepositorySummaryAsync(project);
        if (summary is null)
            return $"No analysis available for '{project}'. Run the analyze command first.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {project}");
        sb.AppendLine($"**Confidence:** {summary.Confidence}");
        if (summary.ModelUsed is not null)
            sb.AppendLine($"**Model:** {summary.ModelUsed}");
        sb.AppendLine($"**Last updated:** {summary.UpdatedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine(summary.Summary);

        return sb.ToString();
    }

    [McpServerResource(
        UriTemplate = "codegraph://conventions/{slug}",
        Name = "convention_document",
        Title = "Convention Document",
        MimeType = "text/markdown")]
    [Description("Reads a convention document by slug.")]
    public async Task<string> GetConvention(string slug)
    {
        var conventionsSection = await wikiStore.GetSectionBySlugAsync("conventions");
        if (conventionsSection is null)
            return $"Convention '{slug}' not found. Use codegraph://conventions/index to see available documents.";

        var page = await wikiStore.FindPageAsync(conventionsSection.Id, null, slug);
        if (page is null)
            return $"Convention '{slug}' not found. Use codegraph://conventions/index to see available documents.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {page.Title}");
        sb.AppendLine($"*Revision {page.Revision} by {page.Author} — updated {page.UpdatedAt:yyyy-MM-dd}*");
        sb.AppendLine();
        sb.AppendLine(page.Content);
        return sb.ToString();
    }
}
