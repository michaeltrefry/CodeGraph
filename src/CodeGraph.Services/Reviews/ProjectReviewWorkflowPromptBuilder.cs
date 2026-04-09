using System.Text;
using CodeGraph.Data;

namespace CodeGraph.Services.Reviews;

internal static class ProjectReviewWorkflowPromptBuilder
{
    public static string SystemPrompt => """
        You are performing an evidence-driven code review for a single project within a repository.

        Prioritize:
        1. likely bugs and behavioral risks
        2. security and data-handling risks
        3. reliability issues
        4. maintainability and readability issues
        5. design problems such as oversized classes and mixed responsibilities
        6. dead code and unnecessary abstraction
        7. test gaps around risky behavior

        Diagnostics are lead signals, not proof. Do not convert a diagnostic into a finding unless the source evidence supports it.
        Prefer suspicious code paths, risky control flow, and missing safeguards over cosmetic commentary.
        Anchor evidence in the code's behavior or structure, not just in diagnostic text.
        The output will be shown in a compact repository detail UI, so keep it concise and easy to scan.
        Only emit findings grounded in the provided source and context.
        Return JSON only.
        """;

    public static string Build(
        string repo,
        string projectName,
        string mode,
        string? projectSummary,
        string? updateSummary,
        ProjectReviewBaselineContext? baselineContext,
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<string> blastRadiusFiles,
        IReadOnlyDictionary<string, int> nodeCounts,
        IReadOnlyList<FileMetricsEntity> hotspots,
        IReadOnlyList<ProjectDiagnosticEntity> diagnostics,
        IReadOnlyList<SecurityFindingEntity> securityFindings,
        IReadOnlyList<string> candidateTests,
        IReadOnlyList<(string Type, int Count)> relationshipCounts,
        IReadOnlyList<ReviewInspectionFile> inspectionFiles,
        int maxFindings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Project Review Workflow");
        sb.AppendLine($"Repository: {repo}");
        sb.AppendLine($"Project: {projectName}");
        sb.AppendLine($"Mode: {mode}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(projectSummary))
        {
            sb.AppendLine("## Existing Project Analysis");
            sb.AppendLine(projectSummary);
            sb.AppendLine();
        }

        if (string.Equals(mode, "update", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("## Update Scope");
            if (!string.IsNullOrWhiteSpace(updateSummary))
                sb.AppendLine($"- Summary: {updateSummary}");
            if (changedFiles.Count > 0)
            {
                sb.AppendLine("- Changed files:");
                foreach (var path in changedFiles.Take(12))
                    sb.AppendLine($"  - {path}");
            }
            if (blastRadiusFiles.Count > 0)
            {
                sb.AppendLine("- Blast radius files:");
                foreach (var path in blastRadiusFiles.Take(12))
                    sb.AppendLine($"  - {path}");
            }
            sb.AppendLine();
        }

        if (baselineContext is not null)
        {
            sb.AppendLine("## Baseline Review Context");
            sb.AppendLine(baselineContext.Overview);
            if (baselineContext.Strengths.Count > 0)
                sb.AppendLine($"Strengths: {string.Join("; ", baselineContext.Strengths.Take(4))}");
            if (baselineContext.ReviewedAreas.Count > 0)
                sb.AppendLine($"Previously reviewed areas: {string.Join("; ", baselineContext.ReviewedAreas.Take(5))}");
            if (baselineContext.FollowUps.Count > 0)
                sb.AppendLine($"Prior follow-ups: {string.Join("; ", baselineContext.FollowUps.Take(4))}");
            foreach (var finding in baselineContext.Findings.Take(4))
                sb.AppendLine($"- Prior finding [{finding.Severity}] {finding.Title} in {finding.FilePath}: {finding.Evidence}");
            sb.AppendLine();
        }

        if (nodeCounts.Count > 0)
        {
            sb.AppendLine("## Node Counts");
            foreach (var entry in nodeCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
                sb.AppendLine($"- {entry.Key}: {entry.Value}");
            sb.AppendLine();
        }

        if (relationshipCounts.Count > 0)
        {
            sb.AppendLine("## Relationship Signals");
            foreach (var entry in relationshipCounts)
                sb.AppendLine($"- {entry.Type}: {entry.Count}");
            sb.AppendLine();
        }

        if (hotspots.Count > 0)
        {
            sb.AppendLine("## Risk Queue Signals");
            foreach (var hotspot in hotspots.Take(12))
            {
                sb.AppendLine(
                    $"- {hotspot.FilePath}: risk {hotspot.RiskScore:F1}, health {hotspot.HealthScore:F1}, complexity {hotspot.ComplexityScore}, longest function {hotspot.LongestFunction}, lint {hotspot.LintErrors} errors/{hotspot.LintWarnings} warnings");
            }
            sb.AppendLine();
        }

        if (diagnostics.Count > 0)
        {
            sb.AppendLine("## Diagnostics");
            foreach (var diagnostic in diagnostics
                         .OrderBy(d => SeverityOrder(d.Severity))
                         .ThenBy(d => d.FilePath)
                         .ThenBy(d => d.LineStart ?? 0)
                         .Take(20))
            {
                sb.AppendLine(
                    $"- [{diagnostic.Severity}] {diagnostic.DiagnosticId} in {diagnostic.FilePath}:{diagnostic.LineStart?.ToString() ?? "?"} - {diagnostic.Message}");
            }
            sb.AppendLine();
        }

        if (securityFindings.Count > 0)
        {
            sb.AppendLine("## Security Findings");
            foreach (var finding in securityFindings
                         .OrderBy(f => SeverityOrder(f.Severity))
                         .ThenBy(f => f.FilePath)
                         .Take(10))
            {
                sb.AppendLine(
                    $"- [{finding.Severity}] {finding.Title} {(string.IsNullOrWhiteSpace(finding.FilePath) ? "" : $"in {finding.FilePath}")}: {finding.Description}");
            }
            sb.AppendLine();
        }

        if (candidateTests.Count > 0)
        {
            sb.AppendLine("## Candidate Tests");
            foreach (var testFile in candidateTests.Take(10))
                sb.AppendLine($"- {testFile}");
            sb.AppendLine();
        }

        sb.AppendLine("## Inspected Source");
        foreach (var file in inspectionFiles)
        {
            sb.AppendLine($"### {file.Path}");
            sb.AppendLine($"Reason: {file.Reason}");
            sb.AppendLine("```");
            sb.AppendLine(file.Content);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("## Instructions");
        sb.AppendLine($$"""
            Review only the evidence above. Prefer fewer, stronger findings over many speculative ones.
            Call out strengths when you see them.
            If a suspicious area is not strong enough for a finding, move it to follow-ups instead.
            Do not fill the quota just because space is available; 0-4 strong findings is better than a padded list.
            Keep the overview to 1-2 sentences and under roughly 70 words.
            Keep strengths/reviewedAreas/skippedAreas/followUps to short phrases or single-sentence bullets.
            Sort candidateFindings strongest-first.
            Keep each finding title short and specific.
            Keep explanation, evidence, and suggestedImprovement concise and non-redundant.
            Do not restate every diagnostic; mention a diagnostic only when it materially reinforces a source-backed concern.
            Emit at most {{maxFindings}} findings.
            If mode is update, start with the changed files and their surrounding snippets, then use blast-radius files and baseline context to judge whether prior concerns still look relevant.
            Do not repeat a baseline finding unless the current inspected evidence still supports it.

            Respond as JSON with this shape:
            {
              "overview": "string",
              "strengths": ["string"],
              "reviewedAreas": ["string"],
              "skippedAreas": ["string"],
              "followUps": ["string"],
              "candidateFindings": [
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

    private static int SeverityOrder(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => 0,
        "error" => 1,
        "high" => 2,
        "warning" => 3,
        "medium" => 4,
        "low" => 5,
        "info" => 6,
        "suggestion" => 7,
        _ => 8
    };
}

internal sealed record ReviewInspectionFile(string Path, string Reason, string Content, int LineCount);
