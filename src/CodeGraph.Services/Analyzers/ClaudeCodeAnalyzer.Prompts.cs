using System.Text;
using CodeGraph.Models;
using CodeGraph.Services.Models;

namespace CodeGraph.Services.Analyzers;

public partial class ClaudeCodeAnalyzer
{
    private static string BuildGraphContext(IReadOnlyList<GraphNode> nodes)
    {
        if (nodes.Count == 0)
            return "(No graph data available yet)";

        var sb = new StringBuilder();
        foreach (var group in nodes.GroupBy(n => n.Label))
        {
            sb.AppendLine($"### {group.Key}s");
            foreach (var node in group.Take(50))
            {
                sb.AppendLine($"- {node.QualifiedName}");
            }
            if (group.Count() > 50)
                sb.AppendLine($"  ... and {group.Count() - 50} more");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildProjectAnalysisPrompt(string projectName,
        string repoContext, string graphContext,
        IReadOnlyList<(string Path, string Content)> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Analyze Project: {projectName}");
        sb.AppendLine($"Part of repository: {repoContext}");
        sb.AppendLine();
        sb.AppendLine("## Graph Context (already extracted)");
        sb.AppendLine(graphContext);
        sb.AppendLine();
        sb.AppendLine("## Source Files");
        foreach (var (path, content) in files)
        {
            sb.AppendLine($"### {path}");
            sb.AppendLine("```csharp");
            sb.AppendLine(content);
            sb.AppendLine("```");
            sb.AppendLine();
        }
        sb.AppendLine("## Instructions");
        sb.AppendLine("""
            Analyze this single project/assembly and produce:
            1. A summary (1-2 paragraphs) describing what this project does
               in business terms.
            2. A confidence level (high/medium/low) for your analysis.
            3. Its public endpoints (if any) with route, method, and description.
            4. Its services with descriptions and DI lifetime.
            5. External dependencies (databases, other APIs, message queues).
            6. Database tables it accesses.

            Respond as JSON matching this schema:
            {
              "projectName": "string",
              "summary": "string",
              "confidence": "high|medium|low",
              "endpoints": [
                { "route": "string", "httpMethod": "string",
                  "description": "string",
                  "requestModel": "string|null",
                  "responseModel": "string|null" }
              ],
              "services": [
                { "name": "string", "description": "string",
                  "interfaceName": "string|null", "lifetime": "string" }
              ],
              "externalDependencies": ["string"],
              "databaseTables": ["string"]
            }
            """);
        return sb.ToString();
    }

    private static string BuildSynthesisPrompt(string projectName,
        ProjectAnalysis[] projects,
        IReadOnlyList<CrossRepoEdge> crossRepoEdges)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Synthesize Repository Summary: {projectName}");
        sb.AppendLine();
        sb.AppendLine("## Project Analyses");
        foreach (var p in projects)
        {
            sb.AppendLine($"### {p.ProjectName} (confidence: {p.Confidence})");
            sb.AppendLine(p.Summary);
            sb.AppendLine();
        }
        sb.AppendLine("## Cross-Repository Dependencies");
        if (crossRepoEdges.Count == 0)
        {
            sb.AppendLine("(No cross-repo dependencies found yet)");
        }
        else
        {
            foreach (var edge in crossRepoEdges)
                sb.AppendLine($"- {edge.SourceProject} --{edge.Type}--> {edge.TargetProject}");
        }
        sb.AppendLine();
        sb.AppendLine("## Instructions");
        sb.AppendLine("""
            Based on the per-project analyses above, write a repo-level summary
            (2-4 paragraphs) describing:
            1. What this service does as a whole in business terms.
            2. How the projects within it work together.
            3. What it depends on and what depends on it (cross-repo).
            4. An overall confidence level.

            Respond as JSON: { "summary": "string", "confidence": "high|medium|low" }
            """);
        return sb.ToString();
    }

    private static string BuildChangeAnalysisPrompt(string projectName, string diff,
        string commitMessage, string existingSummary)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Incremental Analysis: {projectName}");
        sb.AppendLine();
        sb.AppendLine("## Current Summary");
        sb.AppendLine(existingSummary);
        sb.AppendLine();
        sb.AppendLine("## Commit Message");
        sb.AppendLine(commitMessage);
        sb.AppendLine();
        sb.AppendLine("## Diff");
        sb.AppendLine("```diff");
        sb.AppendLine(diff.Length > 50_000 ? diff[..50_000] + "\n... (truncated)" : diff);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Instructions");
        sb.AppendLine("""
            Review this diff against the current summary. Determine:
            1. Does this change affect the business-level description of the service?
               (New endpoints, removed features, changed behavior, new dependencies)
            2. Or is it trivial? (Refactoring, tests, comments, formatting, bug fixes
               that don't change described behavior)

            If the summary needs updating, respond with:
            {
              "needsUpdate": true,
              "updatedSummary": "the full revised summary",
              "confidence": "high|medium|low",
              "changeDescription": "brief description of what changed and why the summary was updated"
            }

            If the summary is still accurate, respond with:
            { "needsUpdate": false }
            """);
        return sb.ToString();
    }
}
