using System.Text;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services.Models;

namespace CodeGraph.Services.Analyzers;

public static class AnalysisPromptBuilder
{
    public static string SystemPrompt => """
        You are analyzing source code and repository structure.

        Describe what the code appears to do based on the provided graph, source snippets,
        configuration, and project structure. Do not assume any specific business domain
        unless the evidence supports it. If the repository could belong to many domains,
        describe the technical responsibilities rather than inventing a business story.

        Separate observed facts from inference when that distinction matters.
        When making inferences, stay close to the evidence and indicate uncertainty clearly.
        Prefer precise, technical descriptions over generic summaries.

        Respond in JSON format matching the requested schema.
        """;

    public static string BuildProjectAnalysisPrompt(
        string projectName,
        string repoName,
        string graphContext,
        IReadOnlyList<(string Path, string Content)> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Analyze Project: {projectName}");
        sb.AppendLine($"Part of repository: {repoName}");
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
            1. A summary (1-2 paragraphs) describing what this project does.
            2. A confidence level (high/medium/low) for your analysis.
            3. Its public endpoints (if any) with route, method, and description.
            4. Its services with descriptions and DI lifetime.
            5. External dependencies (databases, other APIs, message queues).
            6. Database tables it accesses.

            Base the summary on the evidence provided here. If business purpose is unclear,
            describe the technical responsibilities and note uncertainty instead of guessing.

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

    public static string BuildRepoSynthesisPrompt(
        string repoName,
        IReadOnlyList<ProjectAnalysis> projects,
        IReadOnlyList<CrossRepoEdge> crossRepoEdges,
        string summaryPropertyName = "summary")
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Synthesize Repository Summary: {repoName}");
        sb.AppendLine();
        sb.AppendLine("## Project Analyses");
        foreach (var project in projects)
        {
            sb.AppendLine($"### {project.ProjectName} (confidence: {project.Confidence})");
            sb.AppendLine(project.Summary);
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
        sb.AppendLine($$"""
            Based on the analyses above, write a repository summary that explains:
            1. What this repository appears to do overall.
            2. How its projects work together.
            3. What it depends on and what depends on it.
            4. An overall confidence level.

            Stay close to the code evidence. If the business purpose is not clear,
            describe the architecture and technical responsibilities instead of inventing one.

            Respond as JSON:
            { "{{summaryPropertyName}}" : "string", "confidence": "high|medium|low" }
            """);
        return sb.ToString();
    }

    public static string BuildChangeAnalysisPrompt(
        string repoName,
        string diff,
        string commitMessage,
        string existingSummary)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Incremental Analysis: {repoName}");
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
            1. Does this change affect the described behavior, responsibilities, or dependencies?
            2. Or is it a low-level change that leaves the current summary accurate?

            Prefer evidence from the diff. Do not invent a new business story if the change
            only affects technical details.

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
