using System.Text;
using System.Text.Json;
using CodeGraph.Models;

namespace CodeGraph.Services.Reviews;

internal static class ProjectReviewSynthesisPromptBuilder
{
    public static string SystemPrompt => """
        You are normalizing verified project review notes into a strict JSON response.
        Keep the strongest findings first, remove redundancy, and avoid vague commentary.
        The response is rendered in a compact repo-detail panel, so brevity matters.
        Return JSON only.
        """;

    public static string Build(string repo, string projectName, string mode, string workflowJson, int maxFindings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Repository: {repo}");
        sb.AppendLine($"Project: {projectName}");
        sb.AppendLine($"Mode: {mode}");
        sb.AppendLine();
        sb.AppendLine("## Verified Notes");
        sb.AppendLine(workflowJson);
        sb.AppendLine();
        sb.AppendLine("## Instructions");
        sb.AppendLine($$"""
            Normalize these notes into the final review shape.
            Keep at most {{maxFindings}} findings.
            Preserve only evidence-backed findings.
            Keep the overview to 1-2 sentences and short enough to scan quickly.
            Keep list items short and specific.
            Keep finding explanation, evidence, and suggestedImprovement concise without losing the core risk.
            Do not repeat the same concern across multiple findings.
            If mode is update, make it clear when the review focused on changed code and nearby blast-radius files instead of a full project sweep.

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
