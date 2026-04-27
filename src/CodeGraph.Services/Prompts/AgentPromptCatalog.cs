using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Reviews;

namespace CodeGraph.Services.Prompts;

public static class AgentPromptCatalog
{
    public const string CodeReviewWorkflowSystemPromptKey = "code-review.workflow.system";
    public const string CodeReviewSynthesisSystemPromptKey = "code-review.synthesis.system";
    public const string RepositoryReviewSynthesisSystemPromptKey = "repository-review.synthesis.system";
    public const string RepositoryAnalysisSystemPromptKey = "repository-analysis.system";
    public const string GraphAssistantSystemPromptKey = "graph-assistant.system";
    public const string GraphAssistantDefaultSystemPrompt = """
        You are an expert assistant for a codebase knowledge graph covering many repositories
        across a large multi-repository software ecosystem.

        You have access to tools that query the structural graph of all repositories.
        Use them to answer questions about service dependencies, API endpoints, events,
        data flow, architecture, and repository health. Always search before answering.
        For health questions, use get_project_health (single repo) or get_fleet_health (all repos).
        When you need to see actual code, use read_node_source (by node ID) or get_code_snippet (by file path).

        Shared contracts, event schemas, and package references are often important
        cross-repository linking keys.

        When answering, cite specific repositories, node names, and edge types.
        Be specific and concrete - use the data from the graph, not guesses.
        You must call at least one tool before providing any substantive answer to the user.
        """;

    private static readonly IReadOnlyList<AgentPromptDefinition> Definitions =
    [
        new(
            CodeReviewWorkflowSystemPromptKey,
            "code-review",
            "Code Review",
            "Workflow System Prompt",
            "System prompt for the review workflow that generates candidate findings from project evidence.",
            ProjectReviewWorkflowPromptBuilder.SystemPrompt,
            100),
        new(
            CodeReviewSynthesisSystemPromptKey,
            "code-review",
            "Code Review",
            "Project Synthesis System Prompt",
            "System prompt for normalizing verified project review notes into the final review payload.",
            ProjectReviewSynthesisPromptBuilder.SystemPrompt,
            110),
        new(
            RepositoryReviewSynthesisSystemPromptKey,
            "code-review",
            "Code Review",
            "Repository Synthesis System Prompt",
            "System prompt for synthesizing repository-level review output from project reviews.",
            RepositoryReviewSynthesisPromptBuilder.SystemPrompt,
            120),
        new(
            RepositoryAnalysisSystemPromptKey,
            "repository-analysis",
            "Repository Analysis",
            "Repository Analyzer System Prompt",
            "System prompt for repository and project analysis.",
            AnalysisPromptBuilder.SystemPrompt,
            200),
        new(
            GraphAssistantSystemPromptKey,
            "ask-assistant",
            "Ask Assistant",
            "Graph Assistant System Prompt",
            "System prompt for the graph-backed assistant that answers codebase questions with tool use.",
            GraphAssistantDefaultSystemPrompt,
            300)
    ];

    private static readonly IReadOnlyDictionary<string, AgentPromptDefinition> DefinitionsByKey =
        Definitions.ToDictionary(d => d.Key, d => d, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<AgentPromptDefinition> All => Definitions;

    public static bool TryGet(string promptKey, out AgentPromptDefinition? definition)
        => DefinitionsByKey.TryGetValue(promptKey, out definition);
}
