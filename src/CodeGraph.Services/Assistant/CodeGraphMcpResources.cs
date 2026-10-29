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
            - `codegraph://repositories/index`
            - `codegraph://conventions/index`

            Resource templates:
            - `codegraph://repositories/{repository}/summary`
            - `codegraph://conventions/{slug}`

            Use the `resources/read` MCP method to fetch the content for a listed resource URI.
            """;
    }

    [McpServerResource(
        UriTemplate = "codegraph://repositories/index",
        Name = "repositories_index",
        Title = "Indexed Repositories",
        MimeType = "text/markdown")]
    [Description("Lists all indexed repositories with metadata.")]
    public async Task<string> GetRepositoriesIndex()
    {
        var repositories = await store.ListRepositoriesAsync();
        if (repositories.Count == 0)
            return "No repositories indexed yet.";

        var lines = new List<string> { $"## Indexed Repositories ({repositories.Count})", "" };
        foreach (var repository in repositories.OrderBy(p => p.Name))
        {
            var flags = new List<string>();
            if (repository.IsFoundational) flags.Add("foundational");
            if (!string.IsNullOrWhiteSpace(repository.Language)) flags.Add(repository.Language!);
            if (!string.IsNullOrWhiteSpace(repository.Framework)) flags.Add(repository.Framework!);

            var suffix = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";
            lines.Add($"- **{repository.Name}**{suffix}");
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
        UriTemplate = "codegraph://repositories/{repository}/summary",
        Name = "repository_summary",
        Title = "Repository Summary",
        MimeType = "text/markdown")]
    [Description("Reads the generated repository summary for a repository.")]
    public async Task<string> GetRepositorySummary(string repository)
    {
        var summary = await store.GetRepositorySummaryAsync(repository);
        if (summary is null)
            return $"No analysis available for '{repository}'. Run the analyze command first.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {repository}");
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
