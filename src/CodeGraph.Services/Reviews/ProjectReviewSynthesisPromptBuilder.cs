using System.Text;
using System.Text.Json;
using CodeGraph.Models;

namespace CodeGraph.Services.Reviews;

internal static class ProjectReviewSynthesisPromptBuilder
{
    public static string SystemPrompt => """
        You are normalizing verified project review notes into a strict JSON response.
        Keep the strongest findings first, remove redundancy, and avoid vague commentary.
        Return JSON only.
        """;

    public static string Build(string repo, string projectName, string workflowJson, int maxFindings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Repository: {repo}");
        sb.AppendLine($"Project: {projectName}");
        sb.AppendLine();
        sb.AppendLine("## Verified Notes");
        sb.AppendLine(workflowJson);
        sb.AppendLine();
        sb.AppendLine("## Instructions");
        sb.AppendLine($$"""
            Normalize these notes into the final review shape.
            Keep at most {{maxFindings}} findings.
            Preserve only evidence-backed findings.

            Respond as JSON with this shape:
            {
              "overview": "string",
              "strengths": ["string"],
              "reviewedAreas": ["string"],
              "skippedAreas": ["string"],
              "followUps": ["string"],
              "findings": [
                {
                  "severity": "critical|high|medium|low",
                  "category": "bug|security|reliability|maintainability|readability|design|dead-code|test-gap",
                  "title": "string",
                  "explanation": "string",
                  "evidence": "string",
                  "filePath": "string",
                  "lineStart": 1,
                  "lineEnd": 1,
                  "suggestedImprovement": "string",
                  "confidence": "high|medium|low"
                }
              ]
            }
            """);
        return sb.ToString();
    }
}
